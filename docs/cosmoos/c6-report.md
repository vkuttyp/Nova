# C6 — A controlled kernel component in Nova (plan and report)

Status: **C6a (LZ4) complete 2026-10-24**; C6b (the framebuffer validator, with ADR 0008's imported C
structs) is next. Roadmap acceptance: *one small, non-critical component reimplemented beside its C
original; the same test suite passes for both; performance compared (measured, reproducible)*
(`roadmap.md`). Prompt: `cosmoos-native.md` C6. Stage S0 as before: out of tree, CosmoOS read-only.
Scripts: `integrations/cosmoos/c6.py`, `benchmarks/lz4/run.py`.

## Choice of component (with the owner, 2026-10-24)

The survey looked for pure code with clear boundaries that CosmoOS already tests on the host. The short
list:

| Candidate | C lines | Existing tests | Status at the survey |
|---|---:|---|---|
| LZ4 codec, `kernel/core/lz4.c` | 199 | host test, fuzz target | ready: its API is pointers and sizes |
| CRC-32C, `kernel/core/crc32c.c` | 35 | `test_crypto.c` | ready, but too small to show much |
| Framebuffer validator, `kernel/core/fbvalid.c` | 82 | host test, fuzz target | needed CosmoOS's boot-protocol structs |
| Module ELF validator, `kernel/module/modelf.c` | 252 | host test, fuzz target, `modtest.c` | needed structs; in the loader's trust path |
| GPT, `kernel/block/gpt.c` | 378 | host test, fuzz target, `parttest.c` | needed structs and a read callback |
| ChaCha20/Poly1305 | 342 | RFC vectors | needs wrapping arithmetic and constant-time guarantees |

The struct blocker led to ADR 0008 (C struct types imported from the codebase's headers;
`profiles.md` §3.2, §5). The owner chose "LZ4 first, then fbvalid": C6a is the codec, and C6b is the
first user of the imported structs.

## C6a — LZ4

### Audit (CosmoOS `98040b9b`, v1.0.12-12; read, not copied)

| Interface | Where | What it says |
|---|---|---|
| API | `kernel/include/kernel/lz4.h:22-48` | `size_t lz4_compress(const void *src, size_t slen, void *dst, size_t dcap, void *work)`: the compressed length, or 0 if it does not fit. `size_t lz4_decompress(const void *src, size_t slen, void *dst, size_t dcap)`: the decompressed length, or 0 for malformed input or input that would write past `dcap`. `work` is `LZ4_WORK_BYTES` = 4096 × 4 bytes, aligned to 4. Pure: no allocation, locks or globals |
| Implementation | `kernel/core/lz4.c` (199 lines) | 4096-entry hash table (Knuth's multiplicative hash, `>> 20`); `MF_LIMIT` 12, last 5 bytes literal; literals copied with `memcpy` (`:103,136,162`); the match copy byte by byte, since it may overlap (`:194`) |
| Users | `kernel-services/filesystem/cosmofs/cosmofs.c:872, 1297-1303` | cosmofs compresses and decompresses whole records with the work table taken from `kmalloc`. The match table used to live on the stack: an earlier kernel stack overflow (`docs/audit/2026-10-10-m2-persistent-system-report.md:146-159`) |
| Host test | `tests/host/test_lz4.c` | `lz4-roundtrip`, `lz4-malformed`, `lz4-bounds` |
| Fuzz target | `tests/fuzz/fuzz_lz4.c` | arbitrary bytes into the decompressor, with output capacities from the input's first two bytes, under ASan; and compress/decompress round trips |
| Build | CosmoOS Make: `HOST_LZ4_SRCS`, `FUZZ_LZ4_SRCS`, `HOST_CFLAGS`, `FUZZ_CFLAGS` | `c6.py` asks Make for these, so the tests are built exactly as CosmoOS builds them |

### What was built

- **`integrations/cosmoos/c6/lz4.nova`** (`--profile freestanding`): the codec in safe Nova over slices,
  `compress(src: Slice<u8>, out: SliceMut<u8>, table: SliceMut<u32>)` and `decompress(src, out)`. Every
  index is bounds-checked and every length computation overflow-checked. Overflow checks that would
  otherwise panic on hostile lengths are explicit comparisons that return 0, as the C does. `unsafe`
  appears only in the two exports with the C API (`lz4_compress`, `lz4_decompress`), where the raw
  pointers become slices under lz4.h's contract (`/// Safety:`). The output is byte-identical to the C's.
- **`c6.py`** (no QEMU):
  1. builds CosmoOS's own `test_lz4` and `fuzz_lz4` twice with its own flags, once with `lz4.c` and once
     with the Nova object in its place;
  2. runs `c6/diff_lz4.c`, a differential test of both codecs on the same inputs (identical compressed
     bytes; identical results and output buffers for valid and corrupted streams);
  3. compiles the Nova object with `MODULE_CFLAGS`, links it with `c4/nova_rt_kernel.c`, and applies C4's
     checks.
- **`benchmarks/lz4/`**: `bench.c` (both codecs in one binary) and `run.py`; see its README.

### Results (run 2026-10-24)

`python3 integrations/cosmoos/c6.py --arch aarch64` and `--arch x86_64` on the Mac. The host builds use the
Mac's clang; `--arch` selects the kernel flags for step 3.

| Check | C original | Nova port |
|---|---|---|
| `tests/host/test_lz4.c` (CosmoOS's `HOST_CFLAGS`: ASan, UBSan, `-Werror`) | PASS | PASS |
| `tests/fuzz/fuzz_lz4.c`, 20,000 mutations, seed 1 (3 seeds) | no failure | no failure |
| Differential test, 20,000 cases (17,793 compressed; 37,030 corrupted streams, all rejected identically) | 0 mismatches | |
| AArch64 object under `MODULE_CFLAGS`: C4's checks (relocations `ABS64`, `ADD_ABS_LO12_NC`, `ADR_PREL_PG_HI21`, `CALL26`, `JUMP26`; undefined `panic` only; largest frame 160 B) | | PASS |
| x86-64 object under `MODULE_CFLAGS`: C4's checks (relocations `32S`, `64`, `PLT32`; undefined `panic` only; largest frame 136 B) | | PASS |
| CosmoOS working tree unchanged | PASS | |

**Negative control** (2026-10-24): an off-by-one planted in the Nova decompressor's offset check
(`offset > op + 1` for `offset > op`) has to be caught, or the passes above would mean nothing. It was.
The fuzz target and the differential test both stopped with `nova panic: integer overflow at
lz4.nova:169:20` (fuzz crash input: 231 bytes). The same bug planted in a copy of the C original was found
by ASan as a heap-buffer-overflow READ; built without sanitizers, as the kernel is, the C passed silently.
Both files were restored afterwards. This is the case Nova is for: the kernel runs without ASan, and there
Nova's checks still panic where the C reads out of bounds.

**Performance** (`benchmarks/lz4/README.md`, three machine/compiler pairs, -O2, 4 KiB blocks):

- compression: Nova at 0.48–0.78 of C's speed;
- decompression of text: 0.65–1.12;
- decompression of short matches (`records`): 0.18–0.31;
- decompression of incompressible data: 0.73 on the M1, but 0.02–0.03 on x86-64, where Nova's literal
  copy is a checked byte loop and C's is `memcpy`. With the generated copy replaced by one range check and
  `memcpy` (a scratch experiment), Nova reached 50.9 GB/s against C's 54.2 GB/s.

These are inputs for M9: a checked bulk slice copy, and bounds-check hoisting.

### What C6a proves, and what it does not

- Proved: a CosmoOS component that parses untrusted disk bytes can be written in safe Nova with the same
  API, the same output, and CosmoOS's own tests and fuzzer passing. The object meets the module loader's
  rules on both arches. A memory-safety bug the C would carry silently into a sanitizer-free kernel
  becomes a panic.
- Not done: running the Nova codec inside the kernel. cosmofs links `lz4.c` into the kernel image, so
  replacing it there needs an in-tree change (S1) or a module whose selftest calls both codecs (the C5
  pipeline). The second is possible without touching CosmoOS and is left as an option.
- Not done: the CRC-32C warm-up from the survey. It was not needed once LZ4 covered the same ground.
- Performance is below C, and the report says by how much. Parity is not part of the acceptance; M9 owns it.

## C6b — framebuffer validator (next)

Specify ADR 0008 in `type-system.md`, implement imported C structs, and port `kernel/core/fbvalid.c`
(`bootinfo_fb_validate(const struct cosmoboot_info *, struct bootinfo_framebuffer *, …)`). Then hold the
port to CosmoOS's `test_fbvalid` and `fuzz_fbvalid` in the same way as C6a, and benchmark it.
