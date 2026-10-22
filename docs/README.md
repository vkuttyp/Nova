# Nova Documentation — Milestone 0 / C0 Deliverable

Date: 2026-10-22. Status: **M0/C0 complete; all decisions accepted ([ADR 0002](decisions/0002-milestone-0-decision-set.md)).**
**M1 and C1 complete**: Nova compiles to C and runs, including natively inside CosmoOS on x86-64 and AArch64.
**M2 (ownership and borrowing) and M3 (safe heap) complete** ([milestone-2.md](milestone-2.md), [milestone-3.md](milestone-3.md), [ADR 0003](decisions/0003-m3-built-in-generic-types.md)). **C2 complete** ([cosmoos/c2-report.md](cosmoos/c2-report.md)). **M4 (regions) complete** ([milestone-4.md](milestone-4.md), [ADR 0004](decisions/0004-m4-regions.md)). **M6 (generics and collections) in progress**: 6a generics, 6b interfaces, 6c optionals/`StringView`, 6d `Vector`/`String` and 6e `SlotMap`/`StableVector` done ([milestone-6.md](milestone-6.md), [ADR 0005](decisions/0005-m6-generics-and-built-in-collections.md)); next 6f (property tests, OD-3 design). M5 may run in parallel ([roadmap.md](roadmap.md)).
Implemented-feature specs: [grammar.md](grammar.md) (M1 grammar), [type-system.md](type-system.md)
(M1 static semantics), [intermediate-representation.md](intermediate-representation.md) (HIR/NIR as
implemented) and [diagnostics.md](diagnostics.md) (error codes).

## Master prompt §23 deliverables

| § | Deliverable | Document |
|---|---|---|
| A | Language vision | [vision.md](vision.md) |
| B | Goose technical analysis | [research/goose.md](research/goose.md) (Goose commit `f605b5a`) |
| — | Comparative research (Rust, Zig, Cyclone, Swift, Nim, Vale, escape analysis, regions, UB, fuzzing) | [research/comparative.md](research/comparative.md) |
| C | Hybrid memory model, comparison of three architectures, recommendation | [memory-model.md](memory-model.md) |
| D | Language syntax, 19 example programs, MVP vs future | [language-spec.md](language-spec.md) |
| E | Formal safety design: invariants, rejection, runtime checks | [ownership.md](ownership.md) |
| F | Compiler passes and IRs | [architecture.md](architecture.md), [c-backend.md](c-backend.md) |
| G | Implementation language decision | [decisions/0001-compiler-implementation-language.md](decisions/0001-compiler-implementation-language.md) |
| H | Repository structure | [architecture.md §6](architecture.md) |
| I | Milestone roadmap | [roadmap.md](roadmap.md) |
| J | Risk register | [risks.md](risks.md) |
| K | Open architecture decisions | [open-decisions.md](open-decisions.md) |
| L | First executable milestone | [milestone-1.md](milestone-1.md) |

## CosmoOS prompt §12 deliverables

| # | Deliverable | Document |
|---|---|---|
| 1 | Integration feasibility report | [cosmoos/feasibility.md](cosmoos/feasibility.md) |
| 2 | Revised Nova memory architecture | [memory-model.md](memory-model.md) + [cosmoos/kernel-memory-and-lifetimes.md](cosmoos/kernel-memory-and-lifetimes.md) |
| 3 | Kernel and userland profile specs | [cosmoos/profiles.md](cosmoos/profiles.md) |
| 4 | ABI compatibility report | [cosmoos/abi.md](cosmoos/abi.md) |
| 5 | Allocator integration design | [cosmoos/kernel-memory-and-lifetimes.md §2–3](cosmoos/kernel-memory-and-lifetimes.md) |
| 6 | Ownership and quiescence design | [cosmoos/kernel-memory-and-lifetimes.md §4–7](cosmoos/kernel-memory-and-lifetimes.md) |
| 7 | Freestanding runtime design | [cosmoos/profiles.md §3.3](cosmoos/profiles.md) |
| 8 | Compiler/backend integration design | [cosmoos/build-integration.md](cosmoos/build-integration.md) |
| 9 | Security and correctness risk register | [risks.md](risks.md) |
| 10 | Detailed milestone roadmap | [roadmap.md](roadmap.md) |
| 11 | Acceptance-test matrix | [testing.md](testing.md) |
| 12 | Recommendation for first integration | [cosmoos/feasibility.md](cosmoos/feasibility.md); result: [cosmoos/c1-report.md](cosmoos/c1-report.md) |

Primary-source audits: [research/cosmoos-kernel-audit.md](research/cosmoos-kernel-audit.md),
[research/cosmoos-platform-audit.md](research/cosmoos-platform-audit.md)
(CosmoOS HEAD `30d34b29`, main `4527d16d`; read-only).

## Still to be written (scheduled)

`lifetimes.md`
(M2, split from ownership.md); `allocators.md`, `escape-analysis.md` (M3–M5); `runtime.md`,
`ffi.md`, `unsafe.md` (M8); `benchmarks.md` (M9). Today their content is covered by the
documents above.
