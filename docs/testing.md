# Testing Strategy and Acceptance-Test Matrix

Status: M0/C0 (CosmoOS deliverable 11). **No test in this document has been
executed yet.** Every row is *proposed*. When a row is first executed, it is marked with
the date and result. Tests that were performed are never mixed with proposals.

## 1. Test kinds

| Kind | Location | Oracle |
|---|---|---|
| Unit | `tests/Nova.Compiler.Tests` | xUnit assertions per pass |
| parse/check pass/fail | `tests/corpus/*` | `// error: Nxxxx @line:col` annotations |
| compile-fail (ownership) | `tests/corpus/compile-fail` | annotations. Mutants carry independently assigned verdicts |
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
- Later kinds (`compile-fail`, `run-*`) reuse the same annotation syntax and add
  `// expect-stdout:` and `// expect-panic:` directives (from increment 1c on).

## 4. Execution log

| Date | What ran | Result |
|---|---|---|
| 2026-10-22 | `dotnet test` on macOS arm64 (.NET 10.0.401): lexer and parser unit tests, 20 parse-pass and 18 parse-fail corpus files | 151 passed, 0 failed |
| 2026-10-22 | same, plus 11 check-pass and 19 check-fail files and the checker crash test (increment 1b) | 201 passed, 0 failed |

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
