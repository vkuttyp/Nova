# LZ4: CosmoOS's C codec against Nova's port (C6a)

What it measures: the throughput of `lz4_compress` and `lz4_decompress` in CosmoOS's C original
(`kernel/core/lz4.c`, CosmoOS `98040b9b`) and in Nova's port (`integrations/cosmoos/c6/lz4.nova`). Both are
compiled by the same C compiler at the same optimisation level, without sanitizers, and linked into one
binary (`bench.c`). The C original is compiled with its symbols renamed to `c_lz4_*`. The Nova port is
compiled with `--profile freestanding`, as in the kernel, and keeps every bounds and overflow check.
The report is `docs/cosmoos/c6-report.md`.

```sh
python3 benchmarks/lz4/run.py [--cosmoos ~/dev/CosmoOS] [--cc clang|gcc] [--opt -O2] [--rounds 20]
```

`run.py` needs only CosmoOS's `kernel/include`, `kernel/core/lz4.c` and `tests/host/shim`, so it also runs on a
machine with only that subset. It builds `novac` output into `out/benchmarks/lz4/`.

## Method

- Data: three deterministic sets (xorshift, seed 1), each of 256 blocks of 4 KiB, which is cosmofs's
  record size (`cosmofs.c:1297-1303` compresses whole blocks):
  - `text`: words from an 8-word vocabulary;
  - `random`: incompressible bytes;
  - `records`: 48 fixed bytes, then 16 random ones, repeating every 64 bytes.
- Each repetition compresses all 256 blocks 20 times, then decompresses them 20 times. The figure is the
  median of 7 repetitions, in MB/s of uncompressed data (10^6 bytes).
- `ratio` is compressed size over input size. It must match between the codecs, because the output must
  be byte-identical (`c6.py`'s differential test checks this). `check` is a checksum of decompressed
  lengths, so the work cannot be optimised away.
- The data is cache-hot: 1 MiB per set. The `random` decompression figures measure one 4 KiB literal copy
  per block, which is close to `memcpy` speed.

## Results (run 2026-10-24, -O2)

| Machine, compiler | Set | C compress | Nova compress | Nova/C | C decompress | Nova decompress | Nova/C |
|---|---|---:|---:|---:|---:|---:|---:|
| Apple M1 (arm64), macOS 27, Apple clang 21.0.0 | text | 557.8 | 409.2 | 0.73 | 671.2 | 582.3 | 0.87 |
| | random | 255.6 | 153.6 | 0.60 | 54050 | 39644 | 0.73 |
| | records | 1099.5 | 548.9 | 0.50 | 8081.5 | 1535.5 | 0.19 |
| AMD Ryzen 5 PRO 3600 (x86-64), Debian 12, clang 18.1.8 | text | 474.5 | 368.3 | 0.78 | 586.4 | 657.7 | 1.12 |
| | random | 270.6 | 145.0 | 0.54 | 63619 | 1987.7 | 0.03 |
| | records | 940.6 | 533.6 | 0.57 | 9178.7 | 1688.1 | 0.18 |
| same, gcc 12.2.0 | text | 516.6 | 334.4 | 0.65 | 686.1 | 449.0 | 0.65 |
| | random | 240.6 | 128.3 | 0.53 | 63453 | 1495.6 | 0.02 |
| | records | 978.0 | 470.3 | 0.48 | 3419.0 | 1069.6 | 0.31 |

Figures are MB/s. The ratios are the same for both codecs: 0.395 (text), 1.004 (random) and 0.338 (records).

## Reading them

- **Compression** runs at 0.48–0.78 of C's speed. The match loop does a checked index on every byte it
  compares, and checked additions for its positions (inferred from the generated C, where every
  `src[ip + mlen]` becomes `nv_add_u64` plus `nv_check_index`).
- **Decompression of long literal runs** is where the port loses most. C copies literals with `memcpy`
  (`lz4.c:103,136,162`). Nova has no bulk slice copy, so `copy` is a byte loop with two checked indexes per
  byte. That gives 0.02–0.03 on `random` on x86-64, but 0.73 on the M1, where the loop apparently
  compiles to something much faster (inferred from the numbers; the disassembly was not compared). Experiment (Mac, 2026-10-24, scratch build only): with the generated `copy` replaced by one
  range check and `memcpy`, Nova's `random` decompression went from 39644 to 50902 MB/s (C: 54190).
- **Decompression of short matches** (`records`) is dominated by the overlapping match copy. It is a byte
  loop in both codecs (`lz4.c:194`), but Nova's version checks two indexes and adds twice per byte.
- What would close the gap, in M9: a checked bulk copy on slices (one range check, then `memcpy`), and
  hoisting bounds checks out of loops whose range is already checked. Neither is implemented. Both are
  performance work and leave the safety checks in place.
