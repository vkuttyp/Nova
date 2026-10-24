# Testing Strategy and Acceptance-Test Matrix

Status: M0/C0 (CosmoOS deliverable 11). **No test in this document has been
executed yet.** Every row is *proposed*. When a row is first executed, it is marked with
the date and result. Tests that were performed are never mixed with proposals.

## 1. Test kinds

| Kind | Location | Oracle |
|---|---|---|
| Unit | `tests/Nova.Compiler.Tests` | xUnit assertions per pass |
| parse/check pass/fail | `tests/corpus/*` | `// error: Nxxxx @line:col` annotations |
| compile-fail (ownership) | `tests/corpus/check-fail` | `// error[Nxxxx]` annotations, as for the other check-fail files |
| Fault-injection sweep (M3) | `tests/Nova.Compiler.Tests/FaultInjectionTests.cs` | every run-pass program that allocates, built with ASan+UBSan, is run once per allocation attempt *k* with attempt *k* failing. Each run must return from `main` with `live=0`, or panic with "unwrapped an Err value"; no sanitizer report |
| Mutation suite (M2) | `tests/Nova.Compiler.Tests/MutationTests.cs` | mutants of seed programs; the expected (code, line) diagnostics come from an independent oracle, not from the checker |
| run-pass / run-fail | `tests/corpus/run-*` | `// expect-stdout:`, `// expect-panic: overflow`, exit status |
| IR snapshots | `tests/corpus/*/` + `.nir` files | reviewed text dumps |
| Differential | runner | clang×gcc×O0/O2×sanitizers agree |
| Fault injection | runner + `FailingAllocator` | no leaks or double frees (ASan), expected `AllocError` paths |
| Fuzz (M7) | `tests/Nova.Fuzz` (§5) | generator + NIR interpreter + native agreement |
| CosmoOS boot | `integrations/cosmoos` (S0), later `run_boot_test.py` markers (S1) | `NOVATEST: PASS` marker |
| Kernel selftest (C5+) | CosmoOS selftest framework (owner-approved unit) | selftest pass + lockdep clean |

Sanitizer results are **supporting evidence**, never proof of language safety
(master prompt §18).

## 3. Corpus format (implemented in increment 1a)

Runner: `tests/Nova.Compiler.Tests/CorpusTests.cs`, run by `dotnet test`.

- `tests/corpus/parse-pass/*.nova`: must parse with **no** diagnostics. The AST dump
  (`novac parse --dump-ast`) must equal the sibling `.ast` snapshot.
  To create or update snapshots, run `NOVA_BLESS=1 dotnet test`. **Every blessed snapshot must
  be reviewed by hand before it is committed.** A snapshot is an expected value, not a recording.
- `tests/corpus/parse-fail/*.nova`: annotate each expected diagnostic on the line where it
  is reported, as `// error[N0100]`, or `// error[N0100]@14` to also pin the column. The test
  fails on any missing **or extra** diagnostic, so it also checks error recovery.
- `tests/corpus/check-pass/*.nova`: must type-check with no diagnostics. The HIR dump
  (`novac check --dump-hir`) must equal the sibling `.hir` snapshot (also blessed and reviewed).
- `tests/corpus/check-fail/*.nova`: must **parse** cleanly and produce exactly the annotated
  semantic diagnostics.
- Every parse-pass file is also run through the type checker to check that it never crashes
  (semantic errors are expected there).
- `tests/corpus/run-pass/*.nova` and `run-fail/*.nova` (`RunCorpusTests`) are compiled to C,
  built with **every available C configuration** with strict warnings and `-Werror`, then run.
  Directives:
  - `// expect-stdout: text` — one per output line, in order. `// expect-stdout:` alone is an empty line.
  - `// expect-exit: N` — the exit status (default 0).
  - `// expect-panic: reason` — placed on the line where the panic must be reported. The test
    requires exit status 134 (SIGABRT) and `nova panic: <reason> at <file>:<line>:` on stderr.
- C configurations (`CToolchains`), each built with `-Werror`:
  - clang `-O0`;
  - clang `-O2`;
  - clang `-O2 -DNV_NO_BUILTIN_OVERFLOW` (the portable fallback);
  - clang `-O1` with ASan+UBSan and `-fno-sanitize-recover`;
  - the same three without sanitizers for a **real GCC** when one is found (`gcc`, `gcc-12`…`gcc-15`),
    plus `NOVA_TEST_CC` if set.

  Every configuration must match the expectations, so the run corpus is also a differential test.
- `tests/corpus/emit-c/*.nova` have reviewed `.c` snapshots of the generated `main.c`.
- `RuntimeSemanticsTests` generates a C harness that calls every checked helper in `nova/rt.h` on
  edge values of every integer type (~9,000 cases) and compares results and panic kinds with
  `IntegerSemantics`, under every C configuration. Planting a bug in `nv_rem_*` was checked to
  fail this test (2026-10-22).
- `BackendTests`: generated C is byte-identical across runs and contains no host paths. It also
  compiles freestanding (`-ffreestanding -nostdinc`) for `x86_64-` and `aarch64-unknown-none-elf`.
- `CollectionPropertyTests` (M6f; M6 acceptance criterion 2) are model-based property tests of the
  built-in collections. For each collection (a `Vector` of items whose `drop` prints, `String`,
  `SlotMap` with stored `Handle`s, `StableVector`) and each of a fixed set of seeds, a generator
  writes a Nova program of random operations (`System.Random` with the seed, so the program is
  the same on every machine). A C# model of the collection predicts the program's exact output:
  values read back, lengths, capacities (the growth rule of `type-system.md` §23.1), stale-handle
  results, slices at character boundaries, and the order of every `drop`. The program then runs
  under every C configuration (as run-pass, including ASan+UBSan) and must print exactly that,
  and goes through the fault-injection sweep. Any disagreement is reported with the seed, so it
  can be reproduced.

## 5. Random program generator (M7d)

`tests/Nova.Fuzz` is a Csmith-style generator of Nova programs. It is a library and a campaign
tool: `dotnet run --project tests/Nova.Fuzz -c Release -- [--seed S] [--count N | --minutes M]
[--jobs J] [--out DIR]`. `FuzzTests` runs a fixed set of seeds in the test suite.

**Programs.** Each seed gives one program, the same on every machine (`System.Random` with a seed).
Every program is meant to be:
- **well-typed and borrow-correct**, so the checker accepts it;
- **terminating**:
  - calls go only to later-declared functions (a call DAG, no recursion);
  - loops have constant trip counts (`for (i in 0..N)`, counted `while`);
  - the total iteration budget is bounded;
- **deterministic**: no I/O except standard output, no addresses printed.

It uses:
- integers of every width (`i8`–`i64`, `u8`–`u64`, `usize`) and `bool`;
- literals at the edges of each range;
- `as` (lossless only), `truncate<T>`, and every operator;
- structs (Copy, and with a `drop` block that prints), with field reads and writes;
- enums with payloads, `match` on enums, integers and booleans;
- fixed arrays (indexing, `for` over them) and slices;
- `borrow`/`borrow mut` parameters;
- `Box`, `Vector` (`push`, indexing, `length`, `pop`), optionals (`== null`, `!`), and `Result` with `?`
  and `!`;
- `if`/`else`, `for`, `while`, `loop` with `break`/`continue`, and early `return`.

**Arithmetic.** Most operations go through *guarded* helper functions written in Nova, as Csmith
does (its `safe_add` and friends):
- `add`, `sub`: compare against the limits first;
- `mul`: widen narrow types; reduce 64-bit operands below 2³² (unsigned) or 3·10⁹ (signed);
- `div`, `rem`: skip a zero divisor and `MIN / -1`;
- shifts: amount `% width`;
- `neg`: skip `MIN`.

A guard returns its left operand instead of panicking. A small fraction of operations, bounds
and unwraps are *raw*. Those may panic, so the panic path (message, location, exit status) is
compared too. The generated programs print running checksums of their variables throughout, and
`main` returns a value derived from them.

**Oracle.** For each program:
1. `novac` must accept it. A rejection is a finding: of the generator, or a checker bug.
2. The NIR interpreter's outcome (standard output, standard error, exit status) is the
   reference. An interpreter fault is a finding.
3. Every C configuration must compile it warning-free (`-Werror`, strict warnings) and give the
   same outcome: clang `-O0`, clang `-O2`, clang `-O2` flat form, and, where a real GCC exists,
   GCC `-O0` and `-O2`. A run is limited to 10 s.

Each finding keeps the program and a report (`DIR/finding-<seed>.nova`, `.txt`). A finding that
reveals a compiler bug is fixed, and its program, reduced by hand, joins the corpus.

## 4. Execution log

| Date | What ran | Result |
|---|---|---|
| 2026-10-22 | `dotnet test` on macOS arm64 (.NET 10.0.401): lexer and parser unit tests, 20 parse-pass and 18 parse-fail corpus files | 151 passed, 0 failed |
| 2026-10-22 | same, plus 11 check-pass and 19 check-fail files and the checker crash test (increment 1b) | 201 passed, 0 failed |
| 2026-10-22 | Linux CI run 39792099920 (ubuntu-24.04, clang 18.1.3, GCC 13.3.0): every corpus and run test passed under both compilers. The runtime harness failed only on GCC `-Wclobbered` (harness-only; fixed) | 379/380 |
| 2026-10-24 | C6a: `c6.py --arch aarch64` and `--arch x86_64` (CosmoOS `98040b9b`, macOS host): CosmoOS's `test_lz4` and `fuzz_lz4` built with its own `HOST_CFLAGS`/`FUZZ_CFLAGS`, once with `lz4.c` and once with `c6/lz4.nova`; the differential test; the Nova object under `MODULE_CFLAGS` with C4's checks. Negative control: an off-by-one planted in each codec. `benchmarks/lz4/run.py` on the Mac (Apple clang 21) and on a Ryzen 5 PRO 3600 (clang 18, gcc 12) | both codecs pass the host test and 20,000 fuzz mutations; 20,000 differential cases, 0 mismatches; kernel checks pass on both arches (largest frame 160 / 136 B, only `panic` undefined); the planted bug panics in Nova (`lz4.nova:169:20`) and is silent in C without ASan; throughput in `benchmarks/lz4/README.md` |
| 2026-10-24 | C5: `c5.py --arch x86_64` and `--arch aarch64` (CosmoOS `98040b9b`, QEMU): the out-of-tree module `nova_c5` (KernelHeap, the selftest) built with CosmoOS's rules, signed with its developer key, checked with `check-module-elf.py` and C4's checks, loaded at boot; local suite; `c2.py` on the VM (AArch64) and in QEMU (x86-64) with `run-pass/user-allocators.nova` | every check passes on both arches; the module loads and its 5 in-kernel tests pass with 0 blocks live; 15 boot modules, 0 failed; 1,945 tests; 147/147 programs and 230/230 sweep runs on both arches |
| 2026-10-24 | C4: `c4.py --arch x86_64` and `--arch aarch64` (CosmoOS `98040b9b`): `kdemo.nova` built with CosmoOS's `MODULE_CFLAGS` and `ld.lld -r`, checked against `modelf.c`'s ELF rules, `modreloc.c`'s relocation types, the kernel's exports, the 2048-byte frame limit and `check-fpregs.sh`; 8 negative controls. Also `c2.py` on the VM (AArch64) and in QEMU (x86-64), since the core runtime changed (`nova_panic_reason`) | every check passes on both arches; 8/8 controls rejected; 1,932 tests; 146/146 programs and 230/230 sweep runs on both arches |
| 2026-10-24 | M8e (M8 acceptance): local suite on macOS. `FreestandingTests` checks the cross objects' sections and symbols (`llvm-objdump -h`, `llvm-nm`); a hand-built object with unwind tables and a mutable global was seen to show `.eh_frame` and `.data`, which the checks reject. Hosted output did not change (only freestanding flags did), so the M8d guest runs stand | 1,932 passed |
| 2026-10-24 | M8d (`lib/core`, volatile accesses, C function pointers, `@align`, `@noncopy`): local suite on macOS; `c2.py` on the CosmoOS VM over ssh (AArch64) and in QEMU (x86-64), with `run-pass/mmio.nova` and `function-pointers.nova` running in the guests | 1,932 passed; 146/146 programs and 230/230 sweep runs on both arches |
| 2026-10-24 | M8c (profiles, freestanding objects; the core runtime split by `NV_FREESTANDING`): local suite on macOS; `c2.py` and `c3.py` on the CosmoOS VM over ssh (AArch64), `c2.py` in QEMU (x86-64). Checked by hand on the demo's cross objects: no `.init_array`, `.tdata`, `.tbss` or `.eh_frame` (`llvm-objdump -h`) | 1,902 passed; 144/144 programs and 230/230 sweep runs on both arches, 26/26 `nwc` cases, `c3-io` |
| 2026-10-24 | M8b code on the CosmoOS VM, now at 10.211.55.15 (v1.0.12, AArch64): `c2.py` and `c3.py` over ssh | 144/144 programs, 230/230 sweep runs, 26/26 `nwc` cases, `c3-io` pass; tree unchanged |
| 2026-10-24 | M8b (C FFI): local suite on macOS; `c2.py` in QEMU on x86-64 and AArch64 (the CosmoOS VM still did not answer). `run-pass/ffi-libc.nova` calls CosmoOS libc's `puts`, `strlen`, `abs`, `memcmp`, `memcpy` and `memset` in the guests | 1,897 passed; 144/144 programs and 230/230 sweep runs on both arches |
| 2026-10-24 | M8a (unsafe and raw pointers): local suite on macOS; `c2.py` in QEMU on x86-64 and AArch64 (the CosmoOS VM did not answer ssh or ping). On x86-64, the check "CosmoOS working tree unchanged" failed: four files under CosmoOS `tests/boot` and `tests/host` changed during the run. That work is outside Nova (c2.py only builds into `out/`; the latest CosmoOS commits are in that area). The AArch64 run, after it, kept the tree unchanged | 1,880 passed; 143/143 programs and 230/230 sweep runs on both arches |
| 2026-10-23 to 24 | M7e 24-hour fuzz campaign (`tests/Nova.Fuzz`, commit `973ea0e`) on Debian 12 x86-64, 12 jobs: clang 18.1.8 `-O0`/`-O2`/flat, GCC 12.2 `-O0`/`-O2`, interpreter. All findings were one kind, clang `-Warray-bounds` on a constant slice index, fixed after the first three; all 24 seeds re-run on `01aa4e0` give 0 findings | 423,773 programs, no miscompile; 24 findings, one fixed kind |
| 2026-10-23 | After the CosmoOS VM was reinstalled (now v1.0.11-rc1, AArch64): `c2.py` and `c3.py` over ssh on the current code (M5 and the campaign fix included) | 141/141 programs, 230/230 sweep runs, 26/26 `nwc` cases, `c3-io` pass |
| 2026-10-23 | M7e campaign, interim: 3 findings in the first 25,600 programs (constant slice index, `-Warray-bounds`); fixed, CI run 39953749461 green; `c2.py` in QEMU (x86-64) passes (141/141, 230/230). The CosmoOS AArch64 VM was not reachable (ssh refused), so AArch64 was not repeated | fixed |
| 2026-10-23 | M5 (placement): local suite on macOS with `PlacementTests`, the `clang -O2 placement` run configuration, and 500 fuzz programs with a placement configuration (`milestone-5.md`) | 1843 passed; 0 fuzz findings |
| 2026-10-23 | M7e MSVC: `MsvcTests` on a Windows 11 ARM64 VM (MSVC 19.51) and on x64 in CI (`test-windows`, run 39944590259) | 145/145 on both |
| 2026-10-23 | M7d CI run 39936293723 failed: `FuzzTests` under GCC found `-Wtype-limits` warnings in generated C (fixed, finding 7 in `milestone-7.md`); then 1,500 fuzz programs on the Debian x86-64 box with GCC 12.2 and clang 18.1.8 | 0 findings after the fix |
| 2026-10-23 | M7d (random program generator): local suite on macOS with `FuzzTests` (32 seeds through the whole oracle); campaigns of 2,000 and 500 programs (0 findings after the fixes listed in `milestone-7.md`: a C name collision with the runtime, a false N0272, four kinds of warnings on valid programs); `c2.py`/`c3.py` on both arches (generated C changed: `nvF_` names) | 1272 passed; 141/141 programs and 230/230 sweep runs in CosmoOS, 26/26 `nwc` cases, on both arches |
| 2026-10-23 | M7c (NIR reference interpreter): local suite on macOS with `InterpreterTests`, interpreter agreement in the fault sweep, the property tests and the I/O programs (`milestone-7.md`) | 1216 passed |
| 2026-10-23 | M7b (structured C, now the default): local suite on macOS with the `clang -O2 flat` oracle configuration, `-Wimplicit-fallthrough` added to the strict set, and `StructuredCTests`; `c2.py` and `c3.py` on x86-64 (QEMU) and AArch64 (VM). Found during the work: a one-pass transfer elimination left a `goto` where an empty exit block's own `goto` was removed later (now iterated to a fixpoint) | 1058 passed; 137/137 programs, 228/228 sweep runs, 26/26 `nwc` cases on both arches |
| 2026-10-23 | M7a (source maps): local suite on macOS with the new `clang -O0 -g line-directives` run configuration and `SourceMapTests`; manual lldb session on `novac build -g` output (`milestone-7.md`). Default C output unchanged (all earlier snapshots equal), so guest runs were not repeated | 951 passed |
| 2026-10-23 | C3 (files, sockets, processes, `nwc`): local suite on macOS; `c3.py` in QEMU (x86-64) and on the CosmoOS VM (AArch64); `c2.py` on both. The first `nwc` matched BusyBox on 24/26 cases: BusyBox skips non-ASCII and control bytes for words and `-L`, and ends lines at `\v`, `\f`, `\r` too; fixed, then 26/26. The QEMU harness needed kernel log lines filtered and the inputs under `bin/` | suite passes; 26/26 cases and `c3-io` on both arches; c2 137/137 programs, 228/228 sweep runs |
| 2026-10-23 | M6x (region parameters, OD-3): local suite on macOS; `c2.py` on the CosmoOS VM (AArch64) and in QEMU (x86-64). During the work, the probes found and fixed: reference fields assigned through (now re-pointed), loans attached to holders before their creation (now reachability-limited), field stores of fresh borrows creating no loan (now spilled to a temporary), and struct emission order for slice and collection fields | 815 passed; 136/136 programs and 228/228 sweep runs on both arches |
| 2026-10-23 | M6f (property tests, invalidation suite, OD-3 design; M6 complete): local suite on macOS; `c2.py` on the CosmoOS VM (AArch64) and in QEMU (x86-64). Property tests checked against planted bugs: minimum capacity 8 instead of 4 (8 of 24 model cases fail), no generation bump on slot-map removal (4 fail); both reverted | 785 passed; 133/133 programs and 217/217 sweep runs pass |
| 2026-10-23 | CI stalls investigated. Two more runs (42b102f) ended in SIGTERM after a slowdown, and one runner died without uploading logs. Not reproduced on a real x86-64 Debian 12 machine (GCC 12, clang 18.1.8, .NET 10.0.402): the committed suite passes with 12 cores (1.3 min), pinned to 4 and to 2 cores, and on 2 cores under a 7 GB memory cap (2.8 min). Under x86-64 emulation (Docker on arm64) the test host deadlocked with unreaped child processes and threads in `rt_mutex_schedule`; the 6d baseline was not compared there, so that may be an emulation artefact (inferred). Hardening: tools run on at most one per core, and their output is read on dedicated threads instead of the thread pool. **Root cause** (found with resource samples streamed into the CI log, run 39879716478): every panic aborts, and the runner pipes each abort to apport/systemd-coredump. Over hundreds of aborting runs (run-fail tests, fault-injection sweeps, ASan builds) the load average rose to 224 and free memory to 116 MB, then the job was killed. The workflow now disables crash reports (`kernel.core_pattern=core`, `ulimit -c 0`, apport stopped) | suite green on real x86-64 in every configuration; CI fixed (see the next run) |
| 2026-10-23 | Linux CI run 39860779514 (M6e commit): the first attempt stalled after `run-pass/generic-containers.nova` and hit the 30-minute job limit with no test failing or timing out; the re-run passed every test in 7 min. The tool runner bounded only process exit, not the end of its output, so a process left holding the pipes could block forever without a diagnosis (inferred cause); both are now bounded | transient; re-run green |
| 2026-10-23 | M6e (SlotMap, Handle, StableVector): local suite on macOS; `c2.py` on the Parallels VM over ssh (aarch64) and in QEMU on x86-64. Found and fixed two older bugs: borrow-checker liveness ignored `Unpack` definitions (a false N0240/N0241 before a consuming match binding a reference payload; regression test `check-pass/scoped-payload-liveness.nova`), and a discarded Copy `e!` result crashed the C emitter. The sweeps now detect stable-vector chunks (`nova_svec_slot(`). Not tested: generation retirement after 2³¹ reuses of one slot (impractical in the corpus; proposed as a runtime unit test) | 736 passed; 133/133 programs and 217/217 sweep runs (19 allocating programs) pass |
| 2026-10-23 | M6d (Vector, String; Ex9, Ex10): local suite on macOS; `c2.py` on the Parallels VM over ssh (aarch64) and in QEMU on x86-64. The sweeps now also detect vector growth (`nova_vec_grow(`) | 724 passed; 131/131 programs and 204/204 sweep runs (17 allocating programs) pass |
| 2026-10-23 | M6c (optionals, StringView): local suite on macOS; `c2.py` in QEMU on x86-64 and on the Parallels VM over ssh (aarch64). Two lowering bugs were found and fixed: a match on a value binding a reference payload took its address, and a string constant used as a place dropped its statement (now an internal-error invariant) | 703 passed; 125/125 programs and 181/181 sweep runs pass in both |
| 2026-10-22 | M6b (interfaces): local suite on macOS; `c2.py` in QEMU on x86-64 and AArch64, and on the Parallels VM over ssh (aarch64, new `--ssh` mode). A first QEMU x86-64 run hung in firmware while another (non-Nova) QEMU boot test shared the host; it was stopped and re-run | 685 passed; 120/120 programs and 179/179 sweep runs pass in all three |
| 2026-10-22 | M6a (generics): local suite on macOS; `c2.py` on x86-64 and AArch64 | 677 passed; 119/119 programs and 179/179 guest sweep runs pass on both arches. The live-allocation report caught a leak (outer instances marked Copy while inner ones were made); fixed |
| 2026-10-22 | M4c (Pool, PoolBox, `var` scoped values; M4 complete): local suite on macOS; `c2.py` on x86-64 and AArch64 | 663 passed; 117/117 programs and 170/170 guest sweep runs (11 allocating programs) pass on both arches |
| 2026-10-22 | M4b (FixedBufferAllocator, scratch frames, pinned `using`): local suite on macOS; `c2.py` on x86-64 and AArch64 | 653 passed; 115/115 programs and 166/166 guest sweep runs (10 allocating programs) pass on both arches |
| 2026-10-22 | M4a (scoped results, Arena): local suite on macOS; `c2.py` on x86-64 and AArch64. The first run's sweep missed region allocations (detection only looked for `nova_alloc(`); fixed, then re-run | 645 passed; 113/113 programs and 161/161 guest sweep runs (9 allocating programs, arena programs included) pass on both arches |
| 2026-10-22 | C2: `c2.py` on x86-64 and AArch64 (CosmoOS `c4ac584b`): corpus plus guest fault-injection sweep | 111/111 programs and 123/123 sweep runs pass on both arches; attempt counts equal the host's; local suite 633 passed |
| 2026-10-22 | M3c (moves out of boxes, consuming match, tail-iterative drops): local suite on macOS; `c1.py --corpus` on x86-64 (CosmoOS `d45235c3`) and AArch64 (`c4ac584b`). The first AArch64 run reported a changed CosmoOS tree because the owner committed `c4ac584b` during the run; the re-run was clean | 629 passed; 110/110 corpus programs pass inside CosmoOS on both arches |
| 2026-10-22 | M3b (Box, allocators, fault-injection sweep): local suite on macOS (the sweep runs every allocating run-pass program under ASan+UBSan); `c1.py --corpus` on x86-64 and AArch64 | 610 passed; 106/106 corpus programs (heap programs included, CosmoOS libc `malloc`) pass inside CosmoOS on both arches |
| 2026-10-22 | M3a (results, `?`, `!`, early-exit temporaries): local suite on macOS; `c1.py --corpus` on x86-64 and AArch64 | 515 passed; 102/102 corpus programs pass inside CosmoOS on both arches |
| 2026-10-22 | M2d (enums, arrays, slices, `for`, mutation suite): local suite on macOS; `c1.py --corpus` on x86-64 and AArch64 | 504 passed (1,152 mutants checked against the oracle); 99/99 corpus programs pass inside CosmoOS on both arches |
| 2026-10-22 | M2c (moves, drops, using, defer): local suite on macOS; `c1.py --corpus` on x86-64 and AArch64 | 451 passed; 87/87 corpus programs (drop order included) pass inside CosmoOS on both arches |
| 2026-10-22 | M2b (references + borrow checker): local suite on macOS; `c1.py --corpus` on x86-64 and AArch64 | 428 passed; 81/81 corpus programs pass inside CosmoOS on both arches |
| 2026-10-22 | M2a (structs): local suite on macOS; `c1.py --corpus` on x86-64 and AArch64 | 405 passed; 77/77 corpus programs pass inside CosmoOS on both arches |
| 2026-10-22 | Linux CI run 39797265501 (after the `-Wclobbered` harness fix) | 380 passed, 0 failed (~10 min) |
| 2026-10-22 | `integrations/cosmoos/c1.py --corpus` on x86-64 and AArch64 (CosmoOS `30d34b29`, QEMU TCG): nova-hello, nova-panic and all 70 run programs inside CosmoOS | all pass on both arches (`cosmoos/c1-report.md`) |
| 2026-10-22 | increment 1c: plus 51 run-pass and 19 run-fail programs under 4 clang configurations (Apple clang 21: `-O0`, `-O2`, portable overflow, ASan+UBSan), runtime/semantics agreement harness, determinism, freestanding cross-compiles, 3 C snapshots. **No real GCC on this host**, so the GCC configurations did not run | 379 passed, 0 failed |

## 2. Acceptance-test matrix

Legend: ● required for acceptance, ○ optional / best-effort, — not applicable.

### Profile A (userland) and hosted

| Test group | Milestone | macOS arm64 host (hosted) | Linux x86-64 CI (hosted) | CosmoOS x86-64 QEMU | CosmoOS AArch64 QEMU |
|---|---|---|---|---|---|
| Parser/type corpus | M1 | ● | ● | — | — |
| Run-pass (clang/gcc, O0/O2) | M1 | ● | ● | — | — |
| Cross-compile with USER_CFLAGS | M1 | ● | ● | — | — |
| Hello World + `.note.cosmo` + `nm -u` check | C1 | — | — | ● | ● |
| Ownership compile-fail suite | M2 | ● | ● | — | — |
| Drop order / move run tests | M2 | ● | ● | ○ | ○ |
| Heap + fault-injection sweep | M3/C2 | ● | ● | ● | ● |
| Arena/scratch escape compile-fail | M4 | ● | ● | — | — |
| Placement equivalence | M5 | ● | ● | ○ | ○ |
| Collection property tests | M6 | ● | ● | ○ | ○ |
| Utility vs reference output | C3 | ○ | ○ | ● | ● |
| Fuzz campaign (24 h) | M7 | ○ | ● | — | — |
| Reproducible build (byte-identical C and ELF) | C1+ | ● | ● | ● | ● |

### Profile B (kernel)

| Test group | Milestone | Kernel build x86-64 | Kernel build AArch64 | QEMU x86-64 | QEMU AArch64 |
|---|---|---|---|---|---|
| Freestanding object: symbols / relocs / sections allowlist | C4 | ● | ● | — | — |
| Frame ≤ 2 KiB, call-chain ≤ budget | C4 | ● | ● | — | — |
| No FP registers (disassembly) | C4 | ● | ● | — | — |
| Kernel allocator adapter selftest (incl. exhaustion) | C5 | ● | ● | ● | ● |
| Component parity vs C original | C6 | ● | ● | ● | ● |
| Effect compile-fail suite | C7 | ● | ● | — | — |
| Lockdep-clean stress (debug build) | C7 | — | — | ● | ● |
| Module load/unload ×1000, no zombie, signed | C8 | ● | ● | ● | ● |

Preservation requirement for all C milestones (prompt §11): existing CosmoOS
`make test` on both arches, host tests, sanitizer tests and the analyzer gate stay
green, and CosmoOS's own build targets are unchanged unless an approved unit changes them.
