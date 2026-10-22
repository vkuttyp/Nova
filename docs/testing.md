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
| Fuzz (M7) | `tests/fuzz` | generator + NIR interpreter + native agreement |
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

## 4. Execution log

| Date | What ran | Result |
|---|---|---|
| 2026-10-22 | `dotnet test` on macOS arm64 (.NET 10.0.401): lexer and parser unit tests, 20 parse-pass and 18 parse-fail corpus files | 151 passed, 0 failed |
| 2026-10-22 | same, plus 11 check-pass and 19 check-fail files and the checker crash test (increment 1b) | 201 passed, 0 failed |
| 2026-10-22 | Linux CI run 39792099920 (ubuntu-24.04, clang 18.1.3, GCC 13.3.0): every corpus and run test passed under both compilers. The runtime harness failed only on GCC `-Wclobbered` (harness-only; fixed) | 379/380 |
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
