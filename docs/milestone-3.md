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
| 3c | **Moves out of boxes and deep structures**: moving fields out of a box's contents and re-initialising them (Ex14), moving payloads out of a `match` on an owned value, drop elaboration of partially moved boxes, and iterative destruction for deep recursive structures (R-9) | type-system §18.3–§18.5 | **done 2026-10-22** |

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

**M3 is complete** (2026-10-22). Status of the criteria:
1. **Met.** `FaultInjectionTests` sweeps every allocating run-pass program (6 programs: boxes, deep-structures, ex4, ex5, ex14, heap-error-paths) under
   ASan+UBSan. Each failure point must end with `live=0` or the unwrap panic. Programs with more
   than 200 attempts (`deep-structures.nova`, 600,000) are sampled at 200 points.
2. **Met.** `run-pass/ex4-account.nova` and `ex14-tree.nova` run as written, apart from Ex14's
   arm syntax and `own` parameter. `ex5-allocator.nova` uses a concrete allocator type, and
   `ex11-errors.nova` is the adapted Ex11.
3. **Met.** Check-fail: `results`, `boxes`, `box-borrows`, `box-moves`. Run tests:
   `result-propagation`, `temporaries-on-early-exit`, `boxes`, `heap-error-paths`,
   `consuming-match`, `deep-structures`.
4. **Met.** Local suite (clang -O0/-O2/portable/ASan+UBSan); CI with GCC 13 and clang 18;
   `c1.py --corpus` inside CosmoOS on both arches, heap programs included.
5. **Met.** `type-system.md` §17–§18, `grammar.md`, `memory-model.md` §3.2, `c-backend.md`,
   `intermediate-representation.md`, `diagnostics.md`, `risks.md` (R-7, R-9).

Found and fixed during M3:
- discarded call results of drop types were never destroyed (O4, from M2c);
- statement temporaries were not ended on early exits from inside a statement (O4).

## Deferred to M6 (ADR 0003)

- user generic types and functions (Ex5 as written);
- the `Allocator` interface for user allocators;
- `T?`, `null`, and `?` on optionals;
- `StringView`, `checkedMul` and `okOr` (Ex11 as written);
- `From<E>` conversion in `?`. v0 requires the identical error type.
