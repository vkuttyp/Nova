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
| **M2** ✅ 2026-10-22 Ownership & borrowing ([milestone-2.md](milestone-2.md)) | Structs, enums, references, moves, init check, NLL borrow check, drop elaboration, `drop`, `using`, `defer`, slices of arrays | Each of O1–O5, B1–B5, E1–E4 has ≥ 2 compile-fail and ≥ 1 near-miss compile-pass test (≥ 120 total). Drop-order run tests. Mutation suite. ASan/UBSan clean |
| **M3** ✅ 2026-10-22 Safe heap (ADR 0003: built-in `Result`/`Box`/allocators) | `Allocator`, `SystemHeap`, `Box`, `AllocError`, `Result`/`?`, `FailingAllocator`, recursive types, iterative drops | Fault-injection sweep: for every test that allocates *n* times, run with failure at each *k ≤ n*; no leaks/double frees under ASan. Ex4, Ex5, Ex11, Ex14 pass |
| **C2** ✅ 2026-10-22 Safe allocation in CosmoOS ([report](cosmoos/c2-report.md)) | M2+M3 features in Profile A | Allocation, destruction and fault-injection tests pass inside QEMU on both arches |
| **M4** ✅ 2026-10-22 Regions (ADR 0004) | `Arena`, `Scratch`, `FixedBufferAllocator`, `Pool` | Compile-fail: every arena-escape form (return, store in Box, outlive via `reset`/drop). Run tests for bulk release. R2 enforced |
| **M5** ✅ 2026-10-23 Escape/placement analysis ([milestone-5.md](milestone-5.md)) | CFG analysis, classification, `--explain-allocations`, Box→stack demotion | Optimised vs unoptimised outputs equal on the whole run-pass corpus. Drop timing identical (instrumented). Report snapshots. No program accepted only because of placement |
| **M6** ✅ 2026-10-23 Generics & collections (ADR 0005) | Monomorphisation, interfaces, `Vector`, `String`, `StringView`, `StableVector`, `SlotMap`, iterators. OD-3 design | Invalidation compile-fail tests. Property tests of collections against a model. Ex9, Ex10, Ex12, Ex13 |
| **C3** ✅ 2026-10-23 Collections & syscalls ([report](cosmoos/c3-report.md)) | Files, sockets, processes in Profile A; a useful utility (candidate: a `wc`/`sha256sum`-class tool or a hexdump) | The utility runs in QEMU on both arches, with output compared to the C/BusyBox equivalent on a test corpus |
| **M7** ✅ 2026-10-24 Production C backend ([plan](milestone-7.md)) | Source maps, structured C, Csmith-style fuzzer, NIR reference interpreter, MSVC best-effort | 24 h fuzz campaign with no miscompile across clang/gcc × O0/O2. Interpreter agrees with native output |
| **M8** ✅ 2026-10-24 Systems/freestanding ([plan](milestone-8.md)) | core library split, FFI, raw pointers, MMIO types, custom entry points, `freestanding` profile | Freestanding object with no libc: verified by `llvm-nm -u` allowlist |
| **C4** ✅ 2026-10-24 Freestanding in CosmoOS ([report](cosmoos/c4-report.md)) | Nova kernel-profile object built with kernel flags | Symbols, relocations and sections checked against the module loader allowlist. Frame sizes ≤ 2 KiB. No FP registers (disassembly check) |
| **C5** ✅ 2026-10-24 Kernel memory adapter ([report](cosmoos/c5-report.md)) | `KernelHeap`, `ObjectCache<T>` bindings; isolated test component | In-kernel selftest: allocation, exhaustion (fault-injected), ownership transfer, cleanup. No allocator modifications |
| **C6** ✅ 2026-10-24 Controlled kernel component ([report](cosmoos/c6-report.md): LZ4, fbvalid) | One small, non-critical component reimplemented beside its C original (candidate chosen with the owner) | Same test suite passes for both. Performance compared (measured, reproducible) |
| **C7** Kernel concurrency (in progress: C7a ✅; [report](cosmoos/c7-report.md)) | Effects, `SpinLock<T>`/`Mutex<T>` guards, `KRef`, `QuiesceGuard`, `PerCpu`, deferred reclamation | Negative effect tests. Lockdep clean under QEMU stress. Real concurrency rules exercised |
| **C8** Experimental module | Nova `.ko` with generated `shutdown()` | Load/unload loops (≥ 1000) with no zombie modules, leaks or lockdep reports, on both arches. Signing via `modsign.py` |
| **M9** Performance | Optimisations with preconditions; benchmark suite vs C/C++/Rust/Zig/Goose | Each optimisation: equivalence tests + measured gain. Published methodology. **No claims without data** |
| **M10** Tooling | Formatter, LSP, package manager, docs generator | — |
| **C9** Gradual adoption | Evaluate further components | Per-component ADR |

## Next highest-priority task

**C7b — C state embedded by value** (`cosmoos/c7-report.md`, ADR 0009 §2; spec to be written as `type-system.md` §33).
C7a (context effects) is done; C7b is the language part the lock guards and kernel objects of C7c–C7d stand on.

- Scope: imported C types by value as fields of Nova structs and as embedded members of imported structs;
  such types are C-laid-out (no Nova layout, transitively) and immovable; they are made in place only by a
  struct literal given to `Box.createIn`, with imported fields zero-filled; `@placed` methods run C's
  initialisers at the final address; `Ptr.containerOf` (unsafe) recovers the containing struct.
- Done when: the spec is written; positive run tests (in every C configuration, against a C half whose
  self-referential state, like a list head, survives because nothing moves it) and negative tests (every way
  of moving or copying a C-laid-out value, every use that needs its Nova layout) pass; the interpreter faults
  instead of guessing a layout; generated C is unchanged for programs without such types; CosmoOS guest
  checks pass if generated C changes.
- Then: C7c (`SpinLock<T>`, `IrqSpinLock<T>`, `Mutex<T>` guards), C7d (`KRef<T>`, `Quiesce.synchronize`,
  `Quiesce.deferDrop`), C7e (the in-kernel stress test under lockdep, both arches), then C8.

In parallel, not blocking C7: **M9** (performance; first inputs are the C6 benchmarks and risk P-7), and, when
the owner wants it, the S1 in-tree port for C1 (OD-20).

## Progress log

Completed work, oldest first; the table above has each milestone's status and report.

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

**M6, generics and collections, is done** (`milestone-6.md`; ADR 0005):
- 6a, generics (checked once, monomorphised), done 2026-10-22;
- 6b, interfaces, `impl`, bounds, done 2026-10-22;
- 6c, optionals `T?` and `StringView`, done 2026-10-23;
- 6d, `Vector<T, A>` and `String<A>` (Ex9, Ex10), done 2026-10-23;
- 6e, `SlotMap<T>`/`Handle<T>` and `StableVector<T>`, done 2026-10-23.

- 6f, model-based property tests, the invalidation suite and the OD-3 design
  ([`od3-design.md`](od3-design.md)), done 2026-10-23. **M6 is complete** (acceptance results in
  `milestone-6.md`).

**M6x, region parameters (OD-3), is done** (2026-10-23; `milestone-6x.md`; ADR 0006): references
in structs, enums, generic instances and collections, checked by signature flows and one loan set
per value.

**C3 is done** (2026-10-23; `cosmoos/c3-report.md`): files, TCP sockets, processes and program
arguments (type-system §26), and `nwc`, a Nova word counter that matches BusyBox `wc` byte for byte
inside CosmoOS on both arches (`integrations/cosmoos/c3.py`).

**M7, the production C backend, is complete** (2026-10-24; `milestone-7.md`). 7a, source maps, is done
(2026-10-23): `#line` directives (`--line-directives`, `-g`), host-path-free source paths, and a
line-table test. 7b, structured C, is done (2026-10-23): `if`/`else`, loops and `switch`
reconstructed from the NIR graph, with the flat form kept as an oracle (`--flat-c`).

7c, the NIR reference interpreter, is done (2026-10-23): `novac run --interp` agrees with native
code on the whole run corpus, the allocation-failure sweep and the I/O programs, and faults on
uses of ended or uninitialised memory.

7d, the random program generator, is done (2026-10-23): `tests/Nova.Fuzz`. Its first runs found a
C name collision with the runtime, a borrow-checker false positive and four kinds of warnings in
generated C, all fixed.

7e is done (2026-10-24): generated C builds with MSVC in CI (`test-windows`), and a 24-hour campaign
(423,773 programs through clang and GCC at `-O0`/`-O2`, the flat form and the interpreter) found no
miscompile; its one finding kind, a clang warning on a constant slice index, is fixed.

**M5**, escape and placement analysis, was done in parallel (2026-10-23; `milestone-5.md`):
non-escaping `SystemHeap` boxes on the stack with `--placement`, `--explain-allocations`, equal
outputs and identical drop traces.

**M8, systems and freestanding support, is complete** (2026-10-24; `milestone-8.md`, ADR 0007: a
hybrid core library). 8a is done (2026-10-24): `unsafe` blocks, expressions and functions, raw pointers `*T` and
`*mut T`, the `Safety:` rule, and interpreter faults for invalid accesses through pointers. 8b is
done (2026-10-24): `extern "C"` declarations, `@export`, `@repr(C)` structs and `@repr` enums, a
generated `nova_exports.h`, and C files in a build.

8c is done (2026-10-24): `--profile freestanding` rejects what needs the hosted runtime, links only
the core runtime, and `--emit obj` makes one object whose only undefined symbols are `nova_panic`,
the memory functions and the package's own C functions, on the host and both CosmoOS triples.

8d is done (2026-10-24): `lib/core` (bundled Nova sources: `Volatile<T>`, `MmioPtr<T>`, address
types), volatile accesses, C function pointers, `@align`, `@noncopy`, `Cpu.spinHint()` and target
queries; Ex18 runs, and builds as a freestanding object.

8e, the acceptance, is done (2026-10-24): freestanding objects for both CosmoOS triples have only
allowlisted undefined symbols and no constructor, TLS or unwind sections, mutable globals or COMMON
symbols.

**C4 is done** (2026-10-24; `cosmoos/c4-report.md`): a freestanding Nova object built with
CosmoOS's own module flags meets every structural rule of its module loader (ELF, relocations,
exported symbols), keeps frames under 2 KiB and touches no FP register, on both arches; eight
negative controls are rejected.

**C5 is done** (2026-10-24; `cosmoos/c5-report.md`): user-defined allocators (`Allocator`, `Layout`
in `lib/core`; for `Box`), `KernelHeap` over `kmalloc`/`kfree`, and an out-of-tree, signed test module
whose in-kernel selftest (allocation, real and injected exhaustion, ownership transfer, cleanup)
passes in CosmoOS on both arches. `ObjectCache<T>` waits for statics or scoped cache boxes.

**C6a is done** (2026-10-24; `cosmoos/c6-report.md`): CosmoOS's LZ4 codec in safe Nova, beside
`kernel/core/lz4.c`. CosmoOS's own host test and fuzz target pass for both, a differential test finds no
mismatch in 20,000 cases, and the object meets the kernel checks on both arches. A bug planted in the
offset check panics in Nova and goes unnoticed in sanitizer-free C. Performance is measured
(`benchmarks/lz4/`): 0.48–0.78 of C for compression, lower for literal-heavy decompression.

**C6b is done, and with it C6** (2026-10-24; `cosmoos/c6-report.md`): imported C struct types
(type-system §31, ADR 0008) and CosmoOS's framebuffer validator in Nova over CosmoOS's own
`kernel/bootinfo.h`. CosmoOS's host test and fuzz target pass for both. The differential test reaches
all 11 answers with no mismatch, and the object meets the kernel checks on both arches. A header that
disagrees with a Nova declaration fails to compile, naming the field. With clang the port costs what C
costs (`benchmarks/fbvalid/`).

**C7 is in progress** (`cosmoos/c7-report.md`, ADR 0009; the owner chose C state embedded by value). Its increments:
C7a context effects, C7b embedded C state, C7c lock guards, C7d kernel objects and quiescence, and C7e the in-kernel
stress test under lockdep. `QuiesceGuard` in modules (blocked on KQ-2), `PerCpu<T>` and statics are left out, with
reasons. **C7a is done** (2026-10-24): `sleep`/`alloc` effects inferred over the package and checked in
`@context(atomic|irq)` functions, under live `@context(atomic)` values, and against `@effects` bounds (type-system §32).

