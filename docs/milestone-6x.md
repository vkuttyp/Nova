# Milestone 6x — Region parameters (OD-3; plan and acceptance)

Status: **complete 2026-10-23** (ADR 0006: implement OD-3 before C3). Design: [`od3-design.md`](od3-design.md).
Normative: `type-system.md` §25.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| X1 | **Syntax and types**: `region` parameters, region arguments (`Token<src>`, `StringView<r>`, `Slice<r, T>`, `borrow(r) T`, `static`); erasure; scoped structs, enums, instances and collections; fields and payloads (N0270); scoped type arguments and region contents; N0273 | §25.1–§25.5 | **done 2026-10-23** |
| X2 | **Flows and borrow checking**: signature flows (§25.6) replacing return sources; writes into locals and through references, collection writes, call flows into `borrow mut` arguments, N0272; type parameters as flow names; scoped generic instances (N0274); drop check | §25.6–§25.7 | **done 2026-10-23** |
| X3 | **Programs and tests**: zero-copy tokenizer, CSV field splitting into `Vector<StringView>`, arena-linked graph; compile-fail tests per rule (≥ 2 each) and near-misses; property test with scoped elements; every v0 corpus program unchanged | — | **done 2026-10-23** |

## Acceptance criteria

1. Every rule of §25.4 and §25.7 has at least two compile-fail tests and one near-miss compile-pass
   test (the M2 standard, `ownership.md` §6).
2. The v0 corpus (all of `tests/corpus`) passes unchanged.
3. Three programs run: a zero-copy tokenizer, a field splitter returning `Vector<StringView>`, and
   a graph of nodes linked by references inside one `Arena`. Clean under ASan+UBSan, GCC/clang and
   inside CosmoOS (`c2.py`, both arches).
4. Docs: `type-system.md` §25, `ownership.md` E3/E4, `lifetimes.md`, `diagnostics.md`, `grammar.md`,
   `od3-design.md` (status), `roadmap.md`, `risks.md`.

## Acceptance results (2026-10-23)

| # | Criterion | Evidence | Result |
|---|---|---|---|
| 1 | ≥ 2 compile-fail and ≥ 1 near-miss per rule | `check-fail/region-declarations.nova` (N0270 ×3, N0273 ×3, unknown `borrow(r)`, region values and pool boxes in fields and vectors, duplicate regions), `parse-fail/region-*.nova` (`static`, interfaces, order), `check-fail/region-flows.nova` (rule 1 ×2, rule 2 ×2 incl. a cross-arena link, rule 3 ×3, rule 4 ×2, N0272 ×3, rule 6 ×2), `check-fail/region-instances.nova` (N0274 ×3). Near-misses: `run-pass/region-near-misses.nova` and the accepted lines of those files | met |
| 2 | v0 corpus unchanged | every earlier test passes; 10 annotations changed, only by code (N0246 → N0270 for scoped fields) or because a case became legal by §25.4 (`Vector<borrow i32>`, `SlotMap<borrow i32>`, `Handle<borrow i32>`) | met |
| 3 | Three programs | `run-pass/region-tokenizer.nova` (zero-copy tokens and `Vector<StringView>` fields), `run-pass/region-graph.nova` (arena-linked nodes, re-linking), plus `CollectionPropertyTests` "views" (6 seeds: `Vector<StringView>` against a model, then mutating the string after the vector's last use). Clean under clang/GCC, ASan+UBSan and the fault sweep; `c2.py` on both arches | met |
| 4 | Docs | `type-system.md` §25, `grammar.md`, `ownership.md` E3/E4, `lifetimes.md` §8, `diagnostics.md`, `od3-design.md`, ADR 0006 | met |

Limits recorded (not hidden): one loan set per value (ADR 0006), so a struct with two regions
keeps both alive together; arrays and slices of scoped elements stay rejected; a moved-out value
with a scoped `drop` still counts its scope end as a use (conservative); methods cannot declare
regions of their own; `impl` blocks for scoped types stay rejected.
