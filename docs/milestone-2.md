# Milestone 2 — Ownership and Borrowing (plan and acceptance)

Status: started 2026-10-22. Normative semantics: `ownership.md` (invariants O*/B*/E*/R*),
`type-system.md`, `grammar.md`, and `lifetimes.md` (to be written in 2b). As in M1, each increment
writes its spec section first, then the code and tests.

## Increments

| # | Content | Spec sections | Status |
|---|---|---|---|
| **2a** | **Structs as plain-data value types**: declarations, struct literals, field read and write, nested structs, methods with a by-value `this`, static functions (`Point.origin()`), target layout with C static asserts | grammar §2, type-system §12 | **done 2026-10-22** |
| 2b | **References**: `borrow T`, `borrow mut T`, borrow expressions, implicit call-site borrows and `mut x`, `borrow this` / `borrow mut this` receivers, auto-deref of field access. NIR places and loans. **NLL borrow checker** (B1–B5), return elision (E1–E2), no scoped fields (E3) | lifetimes.md, type-system §13 | **done 2026-10-22** |
| 2c | **Move types and destruction**: `drop { }` blocks, implicit `Copy` (OD-7) versus move types, moves and `move x`, partial moves, init/move analysis on NIR (O2–O5, replacing the HIR flow analysis), drop elaboration with drop flags (O4), `using` (R3), `defer` | ownership.md §2–3, type-system §14 | **done 2026-10-22** |
| 2d | **Enums and arrays**: `enum` with payloads, exhaustive `match`, fixed arrays `[T; N]`, `Slice<T>` / `SliceMut<T>` from arrays, bounds checks, `for` over arrays, slices and ranges; the mutation suite | grammar, type-system §15–§16 | **done 2026-10-22** |

**M2 is complete** (2026-10-22): every acceptance criterion below is met, as recorded in the
status notes after the criteria.

## Acceptance criteria (whole milestone; from `roadmap.md`)

1. Each invariant O1–O5, B1–B5 and E1–E4 has ≥ 2 compile-fail tests and ≥ 1 "near-miss"
   compile-pass test (≥ 120 tests in total for these).
2. Run tests for drop order (locals in reverse declaration order; fields in declaration order),
   drops on early return, `break` and `continue`, and partial moves.
3. Mutation suite: automated mutants of compile-pass programs (reordered statements, removed
   scopes, duplicated moves) whose verdicts are computed independently of the checker.
4. All run tests stay clean under ASan+UBSan and GCC/clang (CI), and still run inside CosmoOS
   (`integrations/cosmoos/c1.py --corpus`) on both architectures.
5. Docs: `lifetimes.md` (the checker algorithm as implemented), plus updated `ownership.md`,
   `type-system.md`, `grammar.md`, `intermediate-representation.md` and `diagnostics.md`.

Status at the end of 2d:
1. **Met.** 133 must-fail annotations for the ownership and borrowing diagnostics (N0212,
   N0240–N0259) in `tests/corpus/check-fail/`, plus 26 near-miss functions in
   `check-pass/{borrows,moves,slice-borrows}-accepted.nova`. The table below maps them to
   invariants.
2. **Met.** `run-pass/drop-*.nova`, `drops-*.nova`, `using-and-defer.nova`, `enum-drops.nova` and
   `for-drops.nova`.
3. **Met.** `tests/Nova.Compiler.Tests/MutationTests.cs`. Five seed programs of straight-line
   ownership code are mutated with swaps, moves and removals of statements, added and removed
   scopes, and duplicated moves. Each mutant's diagnostics, as (code, line) pairs, are predicted
   by an independent oracle (scope-based name resolution, a move-state simulation, and
   last-use liveness for loans). The compiler must report exactly those diagnostics. Each run
   checks 1,152 distinct mutants. 600 of them resolve their names; 325 of those are rejected
   by move or borrow errors and 275 are accepted. Injected checker bugs (shared loans
   conflicting with nothing, use-only liveness, piecewise re-initialisation of a drop type) are
   caught. The suite found one real gap: writing the only field of a moved drop-type struct was
   accepted (fixed; `type-system.md` §14.4).
4. **Met.** Local suite under clang `-O0`, `-O2`, portable overflow and ASan+UBSan; CI with GCC
   13 and clang 18; `c1.py --corpus` runs 99 programs inside CosmoOS on both arches.
5. **Met.** `type-system.md` §15–§16, `grammar.md`, `lifetimes.md` (slices, index overlap,
   write after value), `intermediate-representation.md`, `c-backend.md`, `diagnostics.md`.

## 2a scope details

In scope:
- `struct` declarations at the top level;
- fields of any M1 type or another struct (no recursion by value);
- literals `Point { x: 1, y: 2 }`;
- `p.x` reads and `p.x = e` writes on `var` locals;
- methods `fn len(this) -> u64`, called as `v.len()`;
- static functions in a struct, called as `Point.origin()`;
- structs as parameters, return values and locals, all by value.

Every 2a struct is **Copy** because all fields are Copy and `drop` does not exist yet (OD-7).

Not in 2a:
- `==` on structs;
- printing structs;
- struct constants;
- field shorthand `{ x }`;
- generic structs;
- `borrow this` (2b);
- `drop` (2c).

## Invariant coverage (criterion 1), as of 2d

Must-fail tests are in `tests/corpus/check-fail/`. Near-misses (programs that must be accepted)
are in `tests/corpus/check-pass/borrows-accepted.nova`.

| Invariant | Must-fail tests | Near-miss passes |
|---|---|---|
| B1 XOR aliasing | borrow-conflicts: `twoMutableBorrows`, `sharedThenMutable`, `mutableThenShared`; borrow-two-phase: 2 | `disjointFields`, `manySharedBorrows` |
| B2 loan validity | borrow-conflicts: `writeWhileShared`, `compoundWriteWhileShared`, `readWhileMut`, `passWhileMut`, `wholeWhileFieldBorrowed`, `fieldWhileWholeBorrowed`; borrow-temporaries: 2 | `mutableThenSharedAfterUse`, `temporaryUsedWithinStatement` |
| B3 liveness (NLL) | borrow-conflicts: `inLoop` (loan live across iterations), `writeWhileShared` | `borrowEndsBeforeWrite`, `borrowInLoopBody` |
| B4 reborrow | borrow-reborrow: 3 | `reborrowEndsThenOriginalUsable` |
| B5 two-phase | borrow-two-phase: 2 | `twoPhaseMethodCall` |
| E1 no return of local data | borrow-returns: 3 | `returnsFieldOfParameter` |
| E2 return sources | borrow-returns: 2; reference-types: 5 | `returnsListedParameter` |
| E3 no scoped fields | reference-types: struct field, constant | n/a (a type rule) |
| O1 unique owner / O2 move de-initialises | moves: `useAfterMove`, `useAfterMoveIntoLocal`, `maybeMoved`, `movedInLoop`, `explicitMoveOfCopy`, `fieldThroughReference`, `wholeThroughReference`, `fieldOfDropType`, `fieldOfDropTypeTemporary` | moves-accepted: `reassignAfterMove`, `moveOnOnePathThenReassign`, `returnsMovesOut`, `borrowEndsThenMove` |
| O3 definite initialisation | definite-assignment (5), field-of-unassigned, moves: `wholeAfterPartial` | moves-accepted: `deferReadsLaterAssignment`, `copyStructIsNotMoved` |
| O4 exactly-once destruction | run tests check every drop's order and count: drop-order, drop-flags, drops-early-exit, drops-moves-and-returns, using-and-defer; also run under ASan | — |
| O5 partial initialisation | moves: `wholeAfterPartial`; field-of-unassigned | moves-accepted: `reinitializeMovedField`, `movedFieldOtherFieldStillUsable` |
| R3 using | moves: `fromUsing`, `fieldFromUsing` | moves-accepted: `usingIsBorrowable` |
| Moves vs borrows (B2) | moves: `moveWhileBorrowed`, `moveFieldWhileWholeBorrowed` | moves-accepted: `borrowEndsThenMove` |
| E4 no scoped globals | reference-types: constant; scoped-types: `EMPTY` | n/a (a type rule) |
| 2d: enums | enums, moves: payload moves (N0251, N0258), `movedInArm`, `assignedInSomeArms`, `payloadThroughReference` | enums (check-pass), run-pass/enum-* |
| 2d: arrays and slices (B1–B4, E1, E3) | slice-borrows: 6; slice-returns: 4; arrays: 31 type errors; scoped-types: 8; ownership-more: `sliceThenMutElement`, `loanAcrossIterations`, `elementReborrow`, `returnsLocalElement` | slice-borrows-accepted: 2 functions (5 near-miss patterns); check-pass/arrays |

`check-fail/ownership-more.nova` adds 24 cases across O2, O3, O5, R3, B1–B4, E1 and temporaries.
