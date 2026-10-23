# Milestone 5 — Escape and placement analysis (plan and acceptance)

Status: **complete 2026-10-23** (run in parallel with M7's 24-hour campaign). Normative:
`memory-model.md` §5 (rules, with the M5 implementation rules in §5.1), `c-backend.md` §2.2 (emission),
`intermediate-representation.md` §5.5a (the interpreter). Spec first, then code and tests.

## Scope

- **Analysis** (`src/Nova.Compiler/Analysis/PlacementAnalysis.cs`). It runs on checked NIR after
  drop elaboration, so it never decides whether a program is accepted. For each `Box.create`
  site it decides between:
  - **stack**: the box does not escape, and its allocator is placement-transparent;
  - **heap**: with the first reason it must stay there.
- **Demotion.** A stack-placed box's value lives in a local of the function. The C backend and
  the interpreter carry this out with `--placement` / `CEmitOptions.Placement`. It is **off by
  default**: it removes allocation attempts, which the allocation report and the fault-injection
  sweep observe. Making it the default is an M9 decision, taken with benchmarks.
- **Report.** `novac build --explain-allocations` prints one line per allocation site: every
  `Box.create`, and the collection and region sites that M5 does not analyse.

## Acceptance criteria (`roadmap.md`)

1. Optimised and unoptimised outputs are equal on the whole run corpus: standard output, exit
   status and panics. A `clang -O2 placement` configuration runs every program.
2. Drop timing is identical, checked by instrumentation: the interpreter records every executed
   drop, and the trace with placement equals the trace without it, for every run program.
3. Report snapshots: `tests/corpus/placement/*.nova` with reviewed `.alloc` files. They cover
   each reason a box stays on the heap, and each placement.
4. No program is accepted only because of placement. The analysis runs after checking, so
   diagnostics cannot depend on it. A test checks that it leaves the NIR unchanged.
5. The interpreter with placement agrees with the native placement build, allocation report
   included. With placement, a program makes at most as many allocation attempts as without.
6. Docs: `memory-model.md` §5, `c-backend.md`, `intermediate-representation.md`, `roadmap.md`,
   `risks.md` (R-8), `testing.md`.

## Acceptance results (2026-10-23)

| # | Criterion | Evidence | Result |
|---|---|---|---|
| 1 | Equal outputs | Run-corpus configuration `clang -O2 placement`: all 137 run-pass and run-fail programs give the corpus expectations. `PlacementTests.Placement_is_unobservable`: in the interpreter, standard output, exit status and standard error (without the allocation line) are equal with and without placement, for every run program and `placement/reasons.nova`. The fuzz oracle gained a `clang -O2 placement` configuration: 500 programs (seeds 20000–20499) and the 32 `FuzzTests` seeds, 0 findings | met |
| 2 | Drop timing identical (instrumented) | The same test compares the interpreter's drop traces (every executed `Drop`: function, position, type, in order): equal for every program | met |
| 3 | Report snapshots | `placement/reasons.nova` / `.alloc` (reviewed): placements (field reads and writes, a shared borrow of the box, a field moved out, `?`) and each heap reason (not transparent, size, loop, assigned twice, moved into a call, borrowed mutably, stored outside the locals, an aggregate, a built-in, a result destroyed holding the box, returned), plus the unanalysed vector storage | met |
| 4 | Not accepted because of placement | The analysis runs on checked NIR in the backend; `Analysis_does_not_change_the_program`: the NIR prints the same before and after it, for every run program | met |
| 5 | Interpreter agrees with native placement; attempts never increase | `Native_placement_agrees_with_the_interpreter`: ASan+UBSan build with placement against the interpreter with placement, allocation report included, for every run program; `Placement_is_unobservable` checks attempts with ≤ without | met |
| 6 | Docs | `memory-model.md` §5.1, `c-backend.md` §2.2, `intermediate-representation.md` §5.5a, this file, `roadmap.md`, `risks.md` R-8, `testing.md` | met |

Over the run corpus, 6 of 29 `Box.create` sites are placed on the stack. The rest escape by design
(lists, trees, results stored in structures, failure-injection programs), are created in loops, or
use `FailingAllocator`. In generated fuzz programs, about 70% of sites are placed. Found while
building it, and fixed before acceptance: the first version of rule 4 sent every `Box.create(…)?`
to the heap, because the error path destroys the result, which holds an `Err` there.

Not done (recorded):
- boxes created in loops (one slot per site; a per-iteration slot would need the box dead before
  the back edge);
- vector, string and collection storage;
- `KernelHeap` (no kernel profile yet);
- placement is opt-in until M9's benchmarks decide the default.
