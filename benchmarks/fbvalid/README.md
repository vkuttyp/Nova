# fbvalid: CosmoOS's C framebuffer validator against Nova's port (C6b)

What it measures: the time per call of `bootinfo_fb_validate` in CosmoOS's C original
(`kernel/core/fbvalid.c`, CosmoOS `98040b9b`) and in Nova's port (`integrations/cosmoos/c6/fbvalid.nova`).
The port reads `struct cosmoboot_info` and writes `struct bootinfo_framebuffer` through types imported from
CosmoOS's headers (type-system.md §31). Both are compiled by the same C compiler at the same optimisation
level, without sanitizers, into one binary (`bench.c`). The C original is renamed `c_bootinfo_fb_validate`.
The Nova port is built `--profile freestanding` and keeps every overflow check. The report is
`docs/cosmoos/c6-report.md`.

```sh
python3 benchmarks/fbvalid/run.py [--cosmoos ~/dev/CosmoOS] [--cc clang|gcc] [--opt -O2] [--rounds 2000]
```

It needs only CosmoOS's `kernel/include`, `boot/protocol` and `kernel/core/fbvalid.c`.

## Method

- Data: 4096 deterministic descriptions (xorshift, seed 1). Every fourth is QEMU's 1280×800×32 mode, valid.
  Each of the others changes one field of it (bits per pixel, pitch, size, base, a colour shift, or height),
  which most checks then refuse. 2009 of the 4096 are accepted.
- Each repetition validates all 4096 descriptions 2000 times. The figure is the median of 7 repetitions, in
  nanoseconds per call.
- `accepted` and `check` (the sum of each reason's first byte) are per pass over the set. They must match
  between the two validators, which shows that both did the same work.
- The validator runs once per boot in CosmoOS (`kernel/core/bootinfo.c:124`). The numbers say what its checks
  cost, not that the boot gets faster or slower.

## Results (run 2026-10-24, -O2; two runs each, which agreed within 0.2 ns)

| Machine, compiler | C (ns/call) | Nova (ns/call) | Nova/C |
|---|---:|---:|---:|
| Apple M1 (arm64), macOS 27, Apple clang 21.0.0 | 4.69 | 4.58 | 0.98 |
| AMD Ryzen 5 PRO 3600 (x86-64), Debian 12, clang 18.1.8 | 6.42 | 6.60 | 1.03 |
| same, gcc 12.2.0 | 7.38 | 13.00 | 1.76 |

## Reading them

- With clang, the port costs the same as the C, within 3%, on both machines.
- With gcc 12, it takes 1.76 times as long. gcc keeps the port's `refusal` and `channelProblem` out of line
  (`nm`: `nvF_refusal`, `nvF_channelProblem`), and the reasons travel as 24-byte optional values. Forcing more
  inlining (`-finline-functions -finline-limit=200`) made it slower (14.6 ns), so inlining alone is not the
  cause. The cause is not established. It is recorded for M9.
