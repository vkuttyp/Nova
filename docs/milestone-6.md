# Milestone 6 — Generics and collections (plan and acceptance)

Status: **complete 2026-10-23** (started 2026-10-22). Normative semantics: `type-system.md` §20 onwards (written increment by
increment), `ownership.md` H2, `memory-model.md` §4, ADR 0005. Each increment writes its spec
first, then the code and tests.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| **6a** | **Generics**: type parameters on functions, structs and enums; explicit and inferred type arguments; checking generic bodies once with opaque type parameters (types, moves, borrows); monomorphisation | type-system §20 | **done 2026-10-22** |
| 6b | **Interfaces**: `interface` declarations (generic too), `impl I for T`, bounds `<T: I>`, interface methods called on type parameters, `Copy` as a bound. Ex12, Ex13 (adapted) | type-system §21 | **done 2026-10-22** |
| 6c | **Optionals and string views**: `T?`, `null`, `?` and `!` on optionals; `StringView` with string literals, `length`, indexing, slicing, comparison, printing | type-system §22 | **done 2026-10-23** |
| 6d | **`Vector<T, A>` and `String<A>`**: growth with `Result`, indexing, views as slices, `for` over a vector, invalidation through borrows (H2, B5). Ex9 (adapted: `truncate<i32>` for `as i32`), Ex10 | type-system §23 | **done 2026-10-23** |
| 6e | **`SlotMap<T>` / `Handle<T>` and `StableVector<T>`** | type-system §24 | **done 2026-10-23** |
| 6f | **Model-based property tests** of the collections; invalidation compile-fail suite; the **OD-3 design** (lifetime-parameterised aggregates) | `od3-design.md`, testing.md §3 | **done 2026-10-23** |

## Acceptance criteria (`roadmap.md`, adjusted by ADR 0005)

1. Invalidation compile-fail tests: every operation that can move a collection's storage
   (growth, removal, clear, drop, move) is rejected while an element borrow or view is live.
2. Property tests: random operation sequences on each collection agree with a model, in the
   test runner, under ASan+UBSan and the fault-injection sweep.
3. Ex9, Ex10 and Ex12 as written apart from syntax that has since changed; Ex13 with integer
   geometry (ADR 0005).
4. Generic code is checked once: an error in a generic body is reported at the definition, even
   if the body is never instantiated.
5. Clean under GCC/clang, ASan+UBSan, and inside CosmoOS (`c2.py`) on both arches.
6. Docs: `type-system.md`, `grammar.md`, `c-backend.md`, `diagnostics.md`, `memory-model.md`,
   the OD-3 design, and `risks.md`.

## Acceptance results (2026-10-23)

| # | Criterion | Evidence | Result |
|---|---|---|---|
| 1 | Invalidation compile-fail suite | `check-fail/vector-invalidation.nova` (11 cases: push, pop, clear, element write, move, `viewMut`, growth through a call, push in `for`, escape, `append` of a self-view, element move-out), `check-fail/slot-map-invalidation.nova` (insert, remove, two `getMut`, move; stable-vector push under a mutable borrow), `check-fail/collection-invalidation.nova` (destruction while borrowed for all four collections; String append, clear and move; stable-vector write and move; vector reassignment). Near-misses accepted in the same files (handles across insert/remove/move; stable-vector push under a shared borrow) | met |
| 2 | Model-based property tests | `CollectionPropertyTests`: 4 collections × 6 seeds × 80 operations. Each program matches its model under clang/GCC O0/O2/portable-overflow and ASan+UBSan, and passes the fault-injection sweep (48 cases). Planted bugs are caught: a different minimum capacity fails 8 cases, and a slot map that does not bump generations on removal fails 4 | met |
| 3 | Ex9, Ex10, Ex12, Ex13 | `run-pass/ex9-vectors.nova` (adapted: `truncate<i32>` for a lossy `as`), `ex10-strings.nova` (as written), `ex12-ex13-interfaces.nova` (Ex13 with integer geometry, ADR 0005) | met |
| 4 | Generic code checked once | `check-fail/generics.nova`: errors in never-instantiated generic bodies (`add<T>`, `field<T>`, `literal<T>`, …) are reported at the definition | met |
| 5 | Clean under GCC/clang, sanitizers, CosmoOS | Linux CI (clang 18, GCC 13) green from run 39881675210 on; `c2.py` in QEMU (x86-64) and on the CosmoOS VM (AArch64): 133/133 programs, 217/217 sweep runs | met |
| 6 | Docs | `type-system.md` §20–§24, `grammar.md`, `c-backend.md`, `intermediate-representation.md`, `diagnostics.md`, `memory-model.md` §4, [`od3-design.md`](od3-design.md), `risks.md`, `testing.md` | met |

Not done in M6 (recorded, not hidden): iterators beyond `for` over arrays, slices and vectors;
`SlotMap` iteration; generation retirement is untested at its 2³¹-reuse limit (proposed runtime
unit test); OD-3 is designed, not implemented (awaiting scheduling, `od3-design.md` §9–§10).
