# Roadmap — Language Milestones (M) and CosmoOS Integration Milestones (C)

Status: written at the end of M0/C0 (2026-10-22). Rule: no milestone is complete because a
demo runs. Each one requires its acceptance criteria, positive and negative tests,
updated docs, and an updated risk register (master prompt §22).

## Ordering

```text
M0/C0 ─► M1 ─► C1 ─► M2 ─► M3 ─► C2 ─► M4 ─► M6 ─► C3
                                   │            └─► M7 ─► M8 ─► C4 ─► C5 ─► C6 ─► C7 ─► C8 ─► C9
                                   └─► M5 (after M4)      M9 (after M7 + correctness baseline) ─► M10
```

## Milestones and acceptance criteria

| # | Scope | Acceptance (measurable) |
|---|---|---|
| **M0/C0** ✅ 2026-10-22 | Research, architecture, specs, audits | This document set. All decisions accepted (ADR 0002) |
| **M1** ✅ 2026-10-22 Minimal compiler | Lexer, parser, AST, primitives, locals, functions, arithmetic, conditions, loops, semantic checks, HIR, NIR, LIR, C emission, executable | See `milestone-1.md`: ≥ 150 corpus tests, generated C clean under strict warnings, clang + gcc host runs agree, both CosmoOS triples compile, determinism check |
| **C1** ✅ 2026-10-22 Native Hello World | M1 output linked against CosmoOS libc and run in QEMU (S0, out of tree) | `NOVATEST: PASS` printed by the Nova program inside CosmoOS on **x86-64 and AArch64**. `.note.cosmo` present. `llvm-nm -u` shows only libc symbols. Exact flags recorded. No CosmoOS file modified (`git status` clean) |
| **M2** Ownership & borrowing | Structs, enums, references, moves, init check, NLL borrow check, drop elaboration, `drop`, `using`, `defer`, slices of arrays | Each of O1–O5, B1–B5, E1–E4 has ≥ 2 compile-fail and ≥ 1 near-miss compile-pass test (≥ 120 total). Drop-order run tests. Mutation suite. ASan/UBSan clean |
| **M3** ✅ 2026-10-22 Safe heap (ADR 0003: built-in `Result`/`Box`/allocators) | `Allocator`, `SystemHeap`, `Box`, `AllocError`, `Result`/`?`, `FailingAllocator`, recursive types, iterative drops | Fault-injection sweep: for every test that allocates *n* times, run with failure at each *k ≤ n*; no leaks/double frees under ASan. Ex4, Ex5, Ex11, Ex14 pass |
| **C2** ✅ 2026-10-22 Safe allocation in CosmoOS ([report](cosmoos/c2-report.md)) | M2+M3 features in Profile A | Allocation, destruction and fault-injection tests pass inside QEMU on both arches |
| **M4** ✅ 2026-10-22 Regions (ADR 0004) | `Arena`, `Scratch`, `FixedBufferAllocator`, `Pool` | Compile-fail: every arena-escape form (return, store in Box, outlive via `reset`/drop). Run tests for bulk release. R2 enforced |
| **M5** Escape/placement analysis | CFG analysis, classification, `--explain-allocations`, Box→stack demotion | Optimised vs unoptimised outputs equal on the whole run-pass corpus. Drop timing identical (instrumented). Report snapshots. No program accepted only because of placement |
| **M6** Generics & collections | Monomorphisation, interfaces, `Vector`, `String`, `StringView`, `StableVector`, `SlotMap`, iterators. OD-3 design | Invalidation compile-fail tests. Property tests of collections against a model. Ex9, Ex10, Ex12, Ex13 |
| **C3** Collections & syscalls | Files, sockets, processes in Profile A; a useful utility (candidate: a `wc`/`sha256sum`-class tool or a hexdump) | The utility runs in QEMU on both arches, with output compared to the C/BusyBox equivalent on a test corpus |
| **M7** Production C backend | Source maps, structured C, Csmith-style fuzzer, NIR reference interpreter, MSVC best-effort | 24 h fuzz campaign with no miscompile across clang/gcc × O0/O2. Interpreter agrees with native output |
| **M8** Systems/freestanding | core library split, FFI, raw pointers, MMIO types, custom entry points, `freestanding` profile | Freestanding object with no libc: verified by `llvm-nm -u` allowlist |
| **C4** Freestanding in CosmoOS | Nova kernel-profile object built with kernel flags | Symbols, relocations and sections checked against the module loader allowlist. Frame sizes ≤ 2 KiB. No FP registers (disassembly check) |
| **C5** Kernel memory adapter | `KernelHeap`, `ObjectCache<T>` bindings; isolated test component | In-kernel selftest: allocation, exhaustion (fault-injected), ownership transfer, cleanup. No allocator modifications |
| **C6** Controlled kernel component | One small, non-critical component reimplemented beside its C original (candidate chosen with the owner) | Same test suite passes for both. Performance compared (measured, reproducible) |
| **C7** Kernel concurrency | Effects, `SpinLock<T>`/`Mutex<T>` guards, `KRef`, `QuiesceGuard`, `PerCpu`, deferred reclamation | Negative effect tests. Lockdep clean under QEMU stress. Real concurrency rules exercised |
| **C8** Experimental module | Nova `.ko` with generated `shutdown()` | Load/unload loops (≥ 1000) with no zombie modules, leaks or lockdep reports, on both arches. Signing via `modsign.py` |
| **M9** Performance | Optimisations with preconditions; benchmark suite vs C/C++/Rust/Zig/Goose | Each optimisation: equivalence tests + measured gain. Published methodology. **No claims without data** |
| **M10** Tooling | Formatter, LSP, package manager, docs generator | — |
| **C9** Gradual adoption | Evaluate further components | Per-component ADR |

## Next highest-priority task

**M1 and C1 are complete (2026-10-22).**
- Nova compiles its M1 subset to C.
- The test suite passes on macOS (clang) and Linux CI (clang and GCC).
- All 70 run-corpus programs run natively inside CosmoOS on x86-64 and AArch64
  (`cosmoos/c1-report.md`).

**M2, ownership and borrowing, is done** (2026-10-22; `milestone-2.md`). It delivered:
- 2a, structs;
- 2b, references and the NLL borrow checker;
- 2c, move types, `drop`, move checking on NIR, drop elaboration with flags, `using`, `defer`;
- 2d, enums with payloads and `match`, fixed arrays, slices, bounds checks, `for`, and the
  mutation suite.

**M3, the safe heap, is done** (2026-10-22; `milestone-3.md`; ADR 0003). It delivered:
- 3a, `Result<T, E>`, `?`, `!`;
- 3b, `Box<T, A>`, `SystemHeap`, `FailingAllocator`, runtime allocation accounting, and the
  fault-injection sweep;
- 3c, moves out of boxes, consuming `match`, and tail-iterative destruction.

Heap programs run inside CosmoOS on both arches with its libc `malloc`.

**C2 is done** (2026-10-22; `cosmoos/c2-report.md`): `integrations/cosmoos/c2.py` runs the whole
corpus and a guest-side fault-injection sweep inside CosmoOS on both arches.

**M4, regions, is done** (2026-10-22; `milestone-4.md`; ADR 0004):
- scoped results;
- `Arena`, `FixedBufferAllocator`, LIFO scratch frames and `Pool`/`PoolBox`, all following R1/R2;
- the fault-injection sweeps, which cover region allocations.

Next per the ordering above: **M6** (generics and collections; OD-3 design), which also turns the
built-in `Result`/`Box`/region types into library types (ADRs 0003/0004). **M5** (escape and
placement analysis) may follow M4 in parallel. In parallel, when the owner wants it: propose the
S1 in-tree port for C1 (OD-20).
