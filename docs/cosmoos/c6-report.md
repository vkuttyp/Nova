# C6 — A controlled kernel component in Nova (plan and report)

Status: **complete 2026-10-24**: C6a (LZ4) and C6b (the framebuffer validator, the first user of ADR
0008's imported C structs). Roadmap acceptance: *one small, non-critical component reimplemented beside its
C original; the same test suite passes for both; performance compared (measured, reproducible)*
(`roadmap.md`). Prompt: `cosmoos-native.md` C6. Stage S0 as before: out of tree, CosmoOS read-only.
Scripts: `integrations/cosmoos/c6.py` (both components; `--component`), `benchmarks/lz4/run.py`,
`benchmarks/fbvalid/run.py`.

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

## C6b — the framebuffer validator

### Audit (CosmoOS `98040b9b`; read, not copied)

| Interface | Where | What it says |
|---|---|---|
| API | `kernel/include/kernel/bootinfo.h:76-103` | `bool bootinfo_fb_validate(const struct cosmoboot_info *info, struct bootinfo_framebuffer *out, const char **why)`: "the whole trust boundary for the display"; `why` gets a static reason; each pointer may be NULL. `struct bootinfo_framebuffer` is defined there |
| Boot protocol | `boot/protocol/cosmoboot.h:145-282` | `struct cosmoboot_info`, version 6 framebuffer fields `fb_phys`…`fb_blue_bits` (`:215-224`), with fields before and after them that the validator does not read |
| Implementation | `kernel/core/fbvalid.c` (82 lines) | ten refusals in a fixed order; the wrap check is written `fb_phys + fb_size < fb_phys`, relying on unsigned wrap-around |
| User | `kernel/core/bootinfo.c:124` | `bootinfo_init` validates the loader's framebuffer once; the console draws only inside what it accepted |
| Host test | `tests/host/test_fbvalid.c` (Make: `HOST_FBVALID_SRCS`) | four groups: a real mode, absence, memory the framebuffer does not have, impossible pixels; it checks the answer, and the reason only for "no framebuffer" |
| Fuzz target | `tests/fuzz/fuzz_fbvalid.c` (`FUZZ_FBVALID_SRCS`) | 40 input bytes become the framebuffer fields; an accepted description must keep every pixel the console can write inside the framebuffer |

### What was built

1. **Imported C struct types** (language; `type-system.md` §31, ADR 0008; grammar `ExternStruct`; N0294).
   - In an `extern "C"` block marked `@header("…")`, `struct name { field: Type; }` declares the fields Nova
     uses, by their C names. `struct name;` imports a type opaquely, and `@ctype` names a typedef.
   - The generated C includes the headers and uses the real type. Nova emits no definition and has no
     layout. Each declared field gets two `_Static_assert`s, on `_Generic` and on `sizeof`. A header that
     disagrees fails to compile, naming the field and the Nova line.
   - Imported types live only behind pointers and references (N0294 elsewhere, including pointer
     arithmetic, `read` and `fromRaw`). Pointers to them are FFI-safe.
   - Pointer and size fields convert through `uintptr_t` or a cast when read and written. They cannot be
     borrowed, because their C type is not Nova's.
   - The interpreter faults on their fields.
   - Tests: `ffi/imports.nova` (a C-owned linked list walked through C's own `next` pointers, fields read
     and written in place, an opaque type, a `@ctype` typedef, a `const char **` result, and a member Nova
     does not declare, which keeps its value) in every C configuration; `check-fail/imported-c-types.nova`
     (25 rejections); the AST snapshot; the interpreter fault.
   - Negative controls (2026-10-24), each a C compile error naming the field and the Nova line: a field
     declared `u32` where the header has `int32_t` (`novac import: struct geo_box.height is not uint32_t
     (bad1.nova:4)`), `*mut CChar` for a `const char *`, and a field the header does not have.
2. **`integrations/cosmoos/c6/fbvalid.nova`** (`--profile freestanding`): `refusal(info: borrow
   cosmoboot_info) -> StringView?` holds the checks in safe Nova, in the C's order, with the C's reasons.
   `bootinfo_fb_validate` is the export with bootinfo.h's prototype, the only `unsafe` code, which turns
   the pointers into references. The header is CosmoOS's own, `kernel/bootinfo.h`: Nova names twelve of
   `cosmoboot_info`'s fields and never mentions the rest. The wrap check cannot be written the C's way,
   since the sum would panic, so it is `fb_size > U64_MAX - fb_phys`.
3. **`c6/diff_fbvalid.c`**: both validators on the same descriptions (random fields, each check's
   boundary values, and a valid mode with one field changed; one in 16 made absent; one in 8 with a NULL
   argument). They must give the same answer, the same reason text, and the same copied-out description.
   Every one of the 11 answers is reached.
4. **`benchmarks/fbvalid/`**: `bench.c` and `run.py`; see its README.

### Results (run 2026-10-24)

`python3 integrations/cosmoos/c6.py --arch aarch64` and `--arch x86_64`, which now run both components:

| Check | C original | Nova port |
|---|---|---|
| `tests/host/test_fbvalid.c` (`HOST_CFLAGS`: ASan, UBSan, `-Werror`) | PASS | PASS |
| `tests/fuzz/fuzz_fbvalid.c`, 20,000 mutations, seed 1 (5 seeds) | no failure | no failure |
| Differential test, 20,000 cases (3,119 accepted, all 11 answers reached) | 0 mismatches | |
| AArch64 object under `MODULE_CFLAGS` with CosmoOS's `kernel/bootinfo.h`: C4's checks (relocations `ABS64`, `ADD_ABS_LO12_NC`, `ADR_PREL_PG_HI21`, `CALL26`; undefined `panic` only; largest frame 48 B) | | PASS |
| x86-64 object, the same (relocations `32S`, `64`, `PLT32`; undefined `panic` only; largest frame 40 B) | | PASS |
| CosmoOS working tree unchanged | PASS | |

The same runs repeated C6a's results unchanged.

**Negative controls** (2026-10-24; the Nova file restored after each):

- *A dropped check.* With "the rows do not fit the framebuffer" removed, all three tests fail. The host
  test fails at `test_fbvalid.c:93` and `:108`. The fuzz target fails on its second iteration, on its
  assertion `pitch * height <= size`. The differential test reports 673 mismatches.
- *The C's wrap check, written in Nova.* `fb_phys + fb_size < fb_phys` panics, `integer overflow at
  fbvalid.nova:66:9`, as soon as the fuzz target or the differential test reaches a wrapping description.
  Nova cannot express a check that depends on wrap-around without saying so.
- **Finding in CosmoOS's tests** (reported here, not changed): with that panicking check, CosmoOS's host
  test still passes, so it never reaches the wrap check. Its wrap case (`test_fbvalid.c:110-114`) keeps
  `good()`'s 1280×800 geometry with `fb_size = 0x8000`, which "the rows do not fit" refuses first. The
  test checks only that the answer is false, not why. Only the fuzz target covers the wrap check.

**Performance** (`benchmarks/fbvalid/README.md`, ns per call, 4096 descriptions):

| Machine, compiler | C | Nova | Nova/C |
|---|---:|---:|---:|
| Apple M1, Apple clang 21 | 4.69 | 4.58 | 0.98 |
| Ryzen 5 PRO 3600, clang 18 | 6.42 | 6.60 | 1.03 |
| Ryzen 5 PRO 3600, gcc 12 | 7.38 | 13.00 | 1.76 |

With clang the port costs what the C costs. With gcc it takes 1.76 times as long. The cause is not
established: gcc keeps two of the port's functions out of line, but forcing inlining made it slower. This
is recorded for M9.

### What C6b proves, and what it does not

- Proved: Nova code can read and write a codebase's own C structs, which a configuration or ABI version may
  lay out differently, with no layout written in Nova. The header that the kernel build uses decides every
  offset, and a declaration that disagrees with it does not compile. The same port, unchanged, compiled
  against CosmoOS's host-test flags and its kernel module flags on both arches.
- Not done: embedded imported structs, C arrays, unions and bit-fields as fields (ADR 0008's later
  additions). A Linux build of the same mechanism is designed (`profiles.md` §5), not run. Running the port
  inside the kernel needs `bootinfo.c` to call it, so an in-tree change (S1).
