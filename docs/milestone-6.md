# Milestone 6 — Generics and collections (plan and acceptance)

Status: started 2026-10-22. Normative semantics: `type-system.md` §20 onwards (written increment by
increment), `ownership.md` H2, `memory-model.md` §4, ADR 0005. Each increment writes its spec
first, then the code and tests.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| **6a** | **Generics**: type parameters on functions, structs and enums; explicit and inferred type arguments; checking generic bodies once with opaque type parameters (types, moves, borrows); monomorphisation | type-system §20 | **done 2026-10-22** |
| 6b | **Interfaces**: `interface` declarations (generic too), `impl I for T`, bounds `<T: I>`, interface methods called on type parameters, `Copy` as a bound. Ex12, Ex13 (adapted) | type-system §21 | **done 2026-10-22** |
| 6c | **Optionals and string views**: `T?`, `null`, `?` and `!` on optionals; `StringView` with string literals, `length`, indexing, slicing, comparison, printing | type-system §22 | planned |
| 6d | **`Vector<T, A>` and `String<A>`**: growth with `Result`, indexing, views as slices, `for` over a vector, invalidation through borrows (H2, B5). Ex9, Ex10 | type-system §23 | planned |
| 6e | **`SlotMap<T>` / `Handle<T>` and `StableVector<T>`** | type-system §24 | planned |
| 6f | **Model-based property tests** of the collections; invalidation compile-fail suite; the **OD-3 design** (lifetime-parameterised aggregates) | `od3-design.md` | planned |

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
