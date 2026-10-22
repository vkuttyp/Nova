# References and the Borrow Checker (M2b)

Status: normative for increment 2b. This document turns `ownership.md` invariants B1–B5 and E1–E3
into an algorithm on NIR. The surface rules for references are in `type-system.md` §13.
Where this document is stricter than `ownership.md`, it records a v0 simplification that later
milestones may relax.

## 1. What makes the v0 problem small

The algorithm can be simple and **exact for v0** because of four language rules (type-system §13):

1. **References are values of type `borrow T` / `borrow mut T` held only in locals and temporaries.**
   They never appear in struct fields (E3) or constants.
2. **A reference binding is immutable and initialised at its declaration** (`let r = …;`; no
   `var r: borrow T`). Assigning to `r` writes *through* it (`*r = v`) and never re-points it.
   So each reference local is assigned exactly once, which makes the points-to sets below
   flow-insensitive without losing precision.
3. **No nested references** (`borrow borrow T` is rejected).
4. **A use of a `borrow mut T` value is an implicit reborrow** of its referent (§4). Two usable
   mutable references to one place can therefore never coexist.

Together with lexical scoping, (1) and (2) mean a reference cannot outlive its referent *inside*
a function, except through a **temporary** borrowed at a call site, or through **`return`**. Both
are checked explicitly (§6, §7). Conflicting accesses while a loan is live (B1/B2) are the
remaining work, and they need liveness (§5).

## 2. NIR additions

```text
projection = Field(f) | Deref | Downcast(V) | Index(operand)
place      = local projection*                 -- (*r).f is  r Deref Field(f)
rvalue    += Ref(Shared | Mut, place)           -- creates a loan
           | Slice(Shared | Mut, place, lo, hi) -- M2d: also a loan, of the whole array or slice viewed
stmt      += StorageDead(local)                 -- the local's storage ends (scope exit, statement end for temps)
```

Lowering facts the checker relies on:
- Every `Ref` is assigned directly to a local or temporary: `t = Ref(k, p)`.
- A temporary lives until the end of the source statement that created it. At that point the
  builder emits `StorageDead(t)`.
- User locals get `StorageDead` at the end of their block, and on `break`/`continue` for the
  blocks being left. (`return` ends every local; references escaping through `return` are §7's job.)
- A use of a `borrow mut T` operand at a call argument, an initialiser or a return is lowered as
  `Ref(Mut, (*r))`, a reborrow. A coercion of `borrow mut T` to `borrow T` is `Ref(Shared, (*r))`.

## 3. Loans and points-to sets

- Each `t = Ref(k, p)` at program point P creates a **loan** `L = (k, p, P)`.
- Each reference local `r` has a set `loans(r)` of the loans it may refer to (directly or through
  reborrows). It is the least solution of:

| NIR | Constraint |
|---|---|
| `t = Ref(k, p)` | `L ∈ loans(t)`; and if `p` contains `Deref` of a reference local `q`, then `loans(q) ⊆ loans(t)` (a reborrow keeps the original loan alive) |
| `t = Slice(k, p, lo, hi)` | as `Ref(k, p)`: a slice is a reference to `[T]`, and its loan covers all of `p`, whatever the range |
| `t = Use(Place(r))` with `r` of reference type | `loans(r) ⊆ loans(t)` |
| `t = Call(f, args)` where `f` returns a reference | `loans(arg_i) ⊆ loans(t)` for every parameter `i` that `f`'s signature says the result is derived from (§7) |
| reference **parameter** `p_i` | `Param_i ∈ loans(p_i)`, a placeholder loan for "whatever the caller lent" |

Because every reference local is assigned once (§1), solving these subset constraints by
fixpoint iteration gives the same result as a flow-sensitive analysis.

**Loan-holding locals (M4a).** Since M4a, every local of a *scoped* type holds loans
(`type-system.md` §19.1), not only reference locals. The constraints generalise:
- **Copies and moves.** When an assignment's rvalue reads a place rooted at a loan-holding local
  `q` (`Use`, aggregates, enum values, built-in calls), then `loans(q) ⊆ loans(t)`. This covers a
  payload read such as `t = (q as Ok).0`.
- **`Unpack`.** `Unpack(q, …)` gives each target `loans(q)`.
- **Borrowing a loan holder.** `Ref(k, p)` where `p` is rooted at a loan-holding local `q` and does
  not go through a deref: `loans(q) ⊆ loans(t)`. A reference to a scoped value keeps that
  value's loans alive.
- Liveness (§5) and the return check (§7) consider all loan-holding locals.

## 4. Accesses

Each NIR statement and terminator performs **accesses** to places, in evaluation order:

| Construct | Access |
|---|---|
| operand `Place(p)` | **Read** p |
| `Ref(Shared, p)` | **BorrowShared** p |
| `Ref(Mut, p)` | **BorrowMut** p |
| `Assign(p, …)` | the rvalue's accesses, then **Write** p |
| `StorageDead(l)` | **Dead** l |

Two places **overlap** if they have the same root local and one projection list is a prefix of
the other. `a` overlaps `a.f`, `a.f` does not overlap `a.g`, and `(*r)` overlaps `(*r).f`.
Two `Index` projections always compare equal (M2d): `a[i]` overlaps `a[j]`, because v0 does no
index reasoning. Two mutable slices of one array therefore cannot be live at once, even if
their ranges are disjoint.
Places with different roots never overlap. That is correct because aliasing between different
roots only arises through loans, and every loan is checked when it is created.

**Conflicts** (B1, B2):

| Live loan \ access | Read | Write | BorrowShared | BorrowMut | Dead |
|---|---|---|---|---|---|
| Shared | ok | **conflict** | ok | **conflict** | **conflict** |
| Mut | **conflict** | **conflict** | **conflict** | **conflict** | **conflict** |

## 5. Liveness (NLL)

- A reference local is **live** at a point if some path from that point reads it (as an operand
  or as the root of a place) before the function ends. Reference locals are never redefined
  (§1), so there is no kill except at the definition. This is standard backward dataflow over the
  NIR CFG.
- The **live loans after point P** are the union of `loans(r)` over every reference local `r` live
  after P.
- **Check:** for every access `a` at point P to place `q`, and every loan `L` live after P with
  `L` not created at P, it is an error if `L.place` overlaps `q` and the table in §4 says
  conflict. The errors are N0240 (write), N0241 (borrow), N0242 (read) and N0243 (Dead: the
  place stops existing while borrowed).

This accepts `let r = borrow x; print(r); x = 5;`, because `r` is dead after `print`. It rejects
`let r = borrow x; x = 5; print(r);`.

**Writes happen after the value (M2d).** In `Assign(p, rvalue)` the rvalue is evaluated first and
`p` is written last. The **Write** access is therefore checked against the loans live *after*
the statement; the rvalue's own accesses use the loans live before it. This accepts
`a[0] = sum(a[..])`: the slice is dead once `sum` has returned, before `a[0]` is written.
Rust's MIR gets the same result by writing a call's result in a separate step.

**B5, two-phase borrows (as implemented).** For a method call `e.m(args)` whose receiver is
`borrow mut this` and `e` is a place, the receiver's loan is created **after** `args` are
evaluated, right before the call. Arguments may therefore read `e` (`v.push(v.len())`), but
they cannot hold a borrow of `e` across the call. Evaluating a place has no side effects, so
creating its loan later does not change what the program computes. The loan exists only for the
call, which is exactly the interval in which it must be exclusive.

## 6. Temporaries

`f(makeValue())` with a `borrow T` parameter borrows a temporary that dies at the end of the
statement (`StorageDead`). If the result of `f` is derived from that argument and is stored in a
reference local used after the statement, the `Dead` access conflicts with the live loan: N0243
("temporary dropped while still borrowed"). The fix is to bind the value to a local first.

## 7. Returning references (E1, E2)

- **Signature.** A function whose return type is a reference must name the parameters the result
  may be derived from. With exactly one reference parameter (a `borrow`/`borrow mut` receiver
  counts), that parameter is implied (elision). Otherwise the return type must be written
  `borrow(a, b) T` or `borrow mut(a) T`, naming reference parameters (N0245 if missing or wrong).
  `borrow mut` results may be derived from `borrow mut` parameters only.
- **Body check at each `Return(op)`.** For every loan `L ∈ loans(op)`:
  - `L = Param_i`: `i` must be among the named parameters (N0245);
  - `L` is a real loan whose place starts with `Deref` of reference parameter `p_i`: allowed if
    `i` is named (N0245 otherwise). Examples: `borrow this.items` with `this: borrow Self`, or
    `borrow (*a).f`;
  - any other real loan borrows a local or temporary of this function: N0244 ("returns a reference
    to local data").
- **Callers** use the signature as the `Call` constraint in §3.

## 8. What the v0 checker deliberately does not do

- No references in aggregates, and no reference re-pointing. Both are rejected by type rules.
  Relaxing them requires region variables on types (OD-3, M6).
- Index-sensitive disjointness (`a[i]` versus `a[j]`) comes in 2d with arrays. It will be
  conservative there: all elements overlap.
- Moves and drops are 2c. The `StorageDead` points emitted now are where drops will go.

## 9. Diagnostics

Every borrow error names the conflicting access (primary span) and carries notes for the loan's
creation point and for a later use of the reference that keeps it live, when one exists.
