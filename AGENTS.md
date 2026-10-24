# AGENTS.md — Working on Nova

Nova is a memory-safe systems programming language with C#/TypeScript-style
syntax, compiled ahead of time to portable C. Its primary long-term target is
CosmoOS (`~/dev/CosmoOS`). This file tells agents and contributors how to work in
this repository. Read it before changing anything.

## Governing documents (in order of authority)

1. `prompts/master-prompt.md`: language goals, priorities, and the 15 mandatory development rules (§22).
2. `prompts/cosmoos-native.md`: CosmoOS integration goals, profiles and milestones C0–C9.
3. `docs/decisions/*.md`: accepted ADRs. They override the proposals in other docs.
4. Normative specs: `docs/ownership.md` (safety invariants O*/B*/E*/R*/H*/U*),
   `docs/language-spec.md`, `docs/memory-model.md`, `docs/c-backend.md`,
   `docs/cosmoos/*.md`.
5. Everything else in `docs/` is explanatory or research material.

Start every session with `docs/README.md` (index and status), `docs/roadmap.md`
("Next highest-priority task") and `docs/open-decisions.md`.

## Current state

- Milestone 0 / C0 is complete. All M0 decisions were accepted on 2026-10-22
  (`docs/decisions/0002-milestone-0-decision-set.md`).
- M1 and C1 are complete. Nova compiles to C, and its programs run natively in CosmoOS (`integrations/cosmoos/c1.py`).
- M2 (ownership and borrowing; `docs/milestone-2.md`) and M3 (the safe heap; `docs/milestone-3.md`,
  ADR 0003) are complete, and so is C2 (`docs/cosmoos/c2-report.md`; `integrations/cosmoos/c2.py`).
  M4 (regions; `docs/milestone-4.md`, ADR 0004) is complete. M6 (generics and collections;
  `docs/milestone-6.md`, ADR 0005) is complete, and so is M6x (region parameters, OD-3; `docs/milestone-6x.md`, ADR 0006). C3
  (files, sockets, processes and the `nwc` utility; `docs/cosmoos/c3-report.md`) is complete. M7 (production C backend;
  `docs/milestone-7.md`) is complete: source maps, structured C, the NIR interpreter (`novac run --interp`), the random
  program generator (`tests/Nova.Fuzz`), MSVC, and a 24-hour campaign. M8 (systems and freestanding; `docs/milestone-8.md`,
  ADR 0007) is in progress: 8a (unsafe and raw pointers, `docs/unsafe.md`) 8b (the C FFI, type-system §28) and 8c (`--profile freestanding`, `--emit obj`; `docs/cosmoos/profiles.md` §4) are done; 8d is next. M5 (escape and placement
  analysis; `docs/milestone-5.md`) is complete (`--placement`, `--explain-allocations`).
- Guest checks: `python3 integrations/cosmoos/c2.py --arch x86_64` (QEMU), and for aarch64 either
  QEMU or a running CosmoOS VM over ssh (`--arch aarch64 --ssh root@<host>`). `c3.py` takes the same
  options. Run them one at a time: each rebuilds the Release compiler.
- Test-suite parts to know: the corpus (`docs/testing.md` §3), the M2 mutation suite
  (`MutationTests.cs`), and the M3 fault-injection sweep (`FaultInjectionTests.cs`).

## Build and test

```sh
dotnet build Nova.slnx                     # warnings are errors
dotnet test Nova.slnx                      # unit + corpus tests
NOVA_BLESS=1 dotnet test Nova.slnx         # rewrite .ast snapshots, then REVIEW the diff
dotnet run --project src/Nova.Cli -- parse --dump-ast file.nova
dotnet run --project src/Nova.Cli -- check --dump-hir file.nova
dotnet run --project src/Nova.Cli -- build file.nova -o program && ./program   # needs clang or cc
```

Corpus conventions (`// error[N0100]` annotations, snapshots): `docs/testing.md` §3.
Every new diagnostic needs a code in `DiagnosticCodes` and a row in `docs/diagnostics.md`.

## Rules

- **Spec before code.** Never implement syntax or semantics that are not specified
  in `docs/`. If the spec is silent, write the spec change (or an open-decision
  entry) first.
- **Safety first.** Follow master prompt §22 without exception. In particular:
  never weaken a check to make a test pass, and never treat escape analysis or heap
  promotion as a substitute for borrow checking.
- **Every milestone** delivers working code, positive and negative tests, updated
  docs, and an updated `roadmap.md` and `risks.md`. A running demo is not completion.
- **Evidence discipline.**
  - Cite sources (file:line for code, section for docs).
  - Mark inferences as "(inferred)".
  - Keep tests that were *run* (with date and result) separate from tests that are only *proposed*.
  - Make no performance claim without a reproducible benchmark in `benchmarks/`.
- **Determinism.** Compiler output must be a pure function of its inputs: no hash-order
  iteration in output paths, no timestamps, no absolute host paths.
- **Target layout** comes only from `TargetInfo`. Never use .NET `sizeof`,
  `Marshal` or host properties for target layout.
- **Generated C** must satisfy the checklist in `docs/c-backend.md`: C11, no undefined
  behaviour, no compiler-rt libcalls, warning-clean under CosmoOS flags, and no use of
  plain `char` for data.

## CosmoOS boundaries

- `~/dev/CosmoOS` is a separate repository with its own `AGENTS.md`, which restricts
  work there to the owner-chosen unit. Treat it as **read-only** from Nova work.
  Do not edit, commit, check out or stash there.
- Building CosmoOS's existing targets (e.g. `gmake ARCH=<arch> libc`) into its
  `out/` directory is permitted for integration testing. Source files must stay
  unchanged (`git status` clean, apart from pre-existing untracked files).
- All CosmoOS-specific Nova artefacts live in `integrations/cosmoos/` until the
  owner approves an in-tree unit (`docs/cosmoos/build-integration.md`, stages S0–S3).
- Read CosmoOS **code**, not only its docs: the C0 audits found about 18 doc/code
  disagreements. Re-audit any interface you touch at the start of each C-milestone.

## Layout (planned; see `docs/architecture.md` §6)

```text
src/Nova.Compiler, src/Nova.Cli      compiler (C# / .NET 10)
runtime/                             C runtime: core (freestanding), hosted, cosmoos
lib/core, lib/std                    Nova standard libraries
programs/                            utilities written in Nova (nwc)
tests/                               unit tests + corpus (parse/check/compile-fail/run)
integrations/cosmoos/                CosmoOS build glue, C1 script, binding files
docs/                                specs, research, ADRs
prompts/                             governing prompts
```

## Git

- Commit only when asked. Use small, focused commits; for compiler work the subject is
  imperative and names the pass or area (e.g. `parser: recover at declaration keywords`).
- Never commit generated build output (`out/`, `bin/`, `obj/`).
