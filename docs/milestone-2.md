# Milestone 2 — Ownership and Borrowing (plan and acceptance)

Status: started 2026-10-22. Normative semantics: `ownership.md` (invariants O*/B*/E*/R*),
`type-system.md`, `grammar.md`, and `lifetimes.md` (to be written in 2b). As in M1, each increment
writes its spec section first, then the code and tests.

## Increments

| # | Content | Spec sections | Status |
|---|---|---|---|
| **2a** | **Structs as plain-data value types**: declarations, struct literals, field read and write, nested structs, methods with a by-value `this`, static functions (`Point.origin()`), target layout with C static asserts | grammar §2, type-system §12 | **done 2026-10-22** |
| 2b | **References**: `borrow T`, `borrow mut T`, borrow expressions, implicit call-site borrows and `mut x`, `borrow this` / `borrow mut this` receivers, auto-deref of field access. NIR places and loans. **NLL borrow checker** (B1–B5), return elision (E1–E2), no scoped fields (E3) | lifetimes.md, type-system §13 | **done 2026-10-22** |
| 2c | **Move types and destruction**: `drop { }` blocks, implicit `Copy` (OD-7) versus move types, moves and `move x`, partial moves, init/move analysis on NIR (O2–O5, replacing the HIR flow analysis), drop elaboration with drop flags (O4), `using` (R3), `defer` | ownership.md §2–3, type-system §14 | planned |
| 2d | **Enums and arrays**: `enum` with payloads, exhaustive `match`, fixed arrays `[T; N]`, `Slice<T>` / `SliceMut<T>` from arrays, bounds checks, `for x in slice` | grammar, type-system §15 | planned |

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

## Invariant coverage (criterion 1), as of 2b

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
| O1–O5 | in 2c | in 2c |

Still to do for criterion 1:
- O1–O5 (2c);
- the mutation suite (criterion 3);
- raising the count to ≥ 120 by 2d.
