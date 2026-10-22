# Milestone 3 — Safe heap (plan and acceptance)

Status: started 2026-10-22. Normative semantics: `ownership.md` (H1–H3, O1–O5),
`memory-model.md` §3.2, `type-system.md` §17–§18 (written increment by increment), and
ADR 0003 (built-in `Result`, `Box` and allocators ahead of M6 generics). As before, each
increment writes its spec first, then the code and tests.

## Increments

| # | Content | Spec sections | Status |
|---|---|---|---|
| **3a** | **Results and error propagation**: built-in `Result<T, E>` with `Ok`/`Err`, matching on results, `e?` (propagate) and `e!` (unwrap or panic), built-in `AllocError`. Also fixes discarded call results of drop types, which were never destroyed (an O4 bug from M2c) | type-system §17 | **done 2026-10-22** |
| 3b | **Box and allocators**: `Box<T, A>` (owned, one pointer wide), `Box.create` / `Box.createIn`, auto-deref through boxes, recursive types through `Box`, `SystemHeap`, `FailingAllocator`. The runtime allocation API, with allocation accounting and a fault-injection hook (`NOVA_FAIL_ALLOC_AT`). Ex4 and Ex5 (concrete allocator). The fault-injection sweep in the test runner. Allocation in CosmoOS's libc for C2 | type-system §18, memory-model §3.2, c-backend | **done 2026-10-22** |
| 3c | **Moves out of boxes and deep structures**: moving fields out of a box's contents and re-initialising them (Ex14), moving payloads out of a `match` on an owned value, drop elaboration of partially moved boxes, and iterative destruction for deep recursive structures (R-9) | type-system §18 | planned |

## Acceptance criteria (from `roadmap.md`, adjusted by ADR 0003)

1. **Fault-injection sweep.** For every run test that allocates *n* times, run it with
   allocation *k* failing, for each *k ≤ n*. Every run must end in the expected way: a clean
   exit with no live allocations, or the expected panic. Runs are clean under ASan+UBSan, with
   no double frees.
2. **Examples.** Ex4 and Ex14 as written, apart from syntax that has since changed. Ex5 with a
   concrete allocator type, since generic functions are M6. Ex11 adapted: no `StringView`,
   `checkedMul` or `okOr`, which are M6 library features.
3. **Tests.** Positive and negative tests for every new rule, plus run tests for drop order
   with boxes, on error paths (`?`), and for deep structures.
4. All run tests stay clean under GCC/clang, ASan+UBSan and inside CosmoOS on both arches
   (heap programs included, as groundwork for C2).
5. Docs: `type-system.md`, `grammar.md`, `memory-model.md`, `c-backend.md`,
   `intermediate-representation.md`, `diagnostics.md`, `risks.md` (R-7, R-9).

## Deferred to M6 (ADR 0003)

- user generic types and functions (Ex5 as written);
- the `Allocator` interface for user allocators;
- `T?`, `null`, and `?` on optionals;
- `StringView`, `checkedMul` and `okOr` (Ex11 as written);
- `From<E>` conversion in `?`. v0 requires the identical error type.
