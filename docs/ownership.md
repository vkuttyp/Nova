# Ownership, Borrowing and Lifetimes — Formal Safety Design (v0)

Status: Milestone 0 proposal. This document is normative for Milestone 2, unless
an item is marked *future*. **B1–B5 and E1–E3 are implemented as specified in `lifetimes.md` (M2b),**
which records the v0 simplifications: immutable, once-initialised reference bindings; and B5
implemented as "the receiver's loan starts after the arguments". Related: `memory-model.md`, `language-spec.md`,
`intermediate-representation.md`, `unsafe.md` (to be written in M8).

## 0. Scope of the guarantee

**Memory safety** for Nova means: no execution of a well-typed program that
contains no `unsafe` code (and links only correctly-specified `unsafe` libraries)
performs any of:

1. use-after-free; 2. double free; 3. dereference of a dangling reference;
4. out-of-bounds access; 5. read of uninitialised memory; 6. type-confused
access; 7. invalid ownership transfer; 8. unsynchronised concurrent access where
at least one access is a write (data race; only once threads exist, M-later).

**Not** included in memory safety, but each with defined behaviour:

| Condition | Defined behaviour |
|---|---|
| Integer overflow (`+ - *`, negation, `abs`) | **Panic** in all profiles (OD-6). `wrapping*`, `saturating*`, `checked*` methods available |
| Division / remainder by zero, `MIN / -1` | Panic |
| Shift by ≥ bit width | Panic (`wrappingShl` masks) |
| Out-of-bounds index | Panic (`get(i)` returns `T?`) |
| Narrowing numeric cast | `as` is **only** allowed where lossless; `truncate<T>(x)`, `checkedCast<T>(x)` explicit |
| `T?` unwrap of `null` | Panic (`!` postfix operator) |
| Allocation failure | **Value** (`Result<_, AllocError>`); panicking convenience APIs exist only in the userland profile |
| Stack overflow | Profile-defined: userland relies on guard page → process termination; kernel profile uses static stack-depth checking (see `cosmoos/profiles.md`) |
| Resource exhaustion, deadlock, logic errors, leaks via `forget` | Not prevented |
| Panic | **Abort**, no unwinding (OD-5). Destructors do not run on panic. Userland: print + `abort()`; kernel: CosmoOS `panic()` |

Leaks are memory-safe (as in Rust), but the safe standard library must not leak
in ordinary use, and no Nova feature may use leaking as a substitute for
lifetime soundness (master prompt §5.6).

## 1. Vocabulary

- **Place**: a storage location expression: a local, a field of a place
  (`p.f`), an element of a place (`p[i]`), or the target of a reference
  (`*r`, written implicitly in surface syntax).
- **Copy type**: a type whose values may be duplicated bitwise: primitives,
  `borrow T` (shared), raw pointers, and `struct`/`enum` types all of whose
  fields are Copy and which declare no `drop` (and are not marked `@noncopy`).
  Everything else is a **move type** (affine).
- **Loan**: the creation of a reference to a place. Kinds: `shared`
  (`borrow T`) and `mut` (`borrow mut T`).
- **Region**: a set of points in a function's control-flow graph (NIR)
  during which a loan must remain valid.
- **Scoped type**: any type that contains a reference (`borrow T`,
  `borrow mut T`, `Slice<T>`, `StringView`, arena handles, guard-protected
  handles), transitively, including through generic arguments. A non-scoped type is
  **free-standing**: it may outlive any particular stack frame.
- **Owner**: the place that holds a value of a move type. Destroying the owner
  destroys the value.

## 2. Invariants

The checker establishes these properties on the NIR (control-flow IR) of every
function. "At point p" means on entry to the NIR statement at p.

### Ownership

- **O1 — Unique owner.** Every live value of a move type is held by exactly one
  initialised place.
- **O2 — Move de-initialises.** Moving from a place makes it *uninitialised*.
  A move out of a place is permitted only if the place is a local, or a field
  path rooted at a local, whose type has no `drop` (partial move). It is
  never permitted through a reference, an index, or out of a `drop`-implementing
  struct.
- **O3 — Definite initialisation.** A place may be read, borrowed, or have a
  field assigned only if it is *definitely initialised* on every path reaching
  that point. (Forward must-dataflow over NIR. The same analysis implements
  use-after-move detection.)
- **O4 — Exactly-once destruction.** At every point where a place goes out of
  scope (`StorageDead`), and before every assignment that overwrites an
  initialised place, the compiler inserts `drop(place)` if the place is
  definitely initialised, or a guarded drop if it is *maybe* initialised (a drop
  flag). A definitely uninitialised place gets no drop. Drop order: locals
  in reverse declaration order; struct fields in declaration order; vector
  elements in index order.
- **O5 — Partial initialisation.** Struct literals initialise all fields
  atomically from the program's point of view. Their C lowering constructs
  in place, and no partially built struct is observable. A partially *moved*
  struct (O2) drops only its remaining initialised fields.

### Borrowing

- **B1 — XOR aliasing.** At any point, for any place P, the live loans that
  overlap P are either all shared, or exactly one is `mut`. Overlap is
  path-prefix based: `a` overlaps `a.f`; `a.f` does not overlap `a.g`;
  `a[i]` overlaps `a[j]` (no index disjointness in v0).
- **B2 — Loan validity.** While a loan L of P is live (inside its region),
  P must not be moved, dropped, go out of scope, or be assigned. If L is
  shared, P must also not be mutated or mutably borrowed. If L is `mut`,
  P must not be accessed at all except through L.
- **B3 — Liveness regions (NLL).** A loan's region is the smallest set of
  CFG points containing every point at which some reference derived from the
  loan (transitively, through reborrows and copies of shared refs) may still be
  used. It is computed by backward liveness over NIR, then by solving outlives
  constraints. A shared reference copied into another variable propagates the loan.
- **B4 — Reborrowing.** `borrow mut T` passed to a `borrow mut T` parameter
  is implicitly reborrowed. The original is unusable until the reborrow dies
  and becomes usable again afterwards (no move).
- **B5 — Two-phase borrows.** For method calls `recv.m(args)` with a
  `borrow mut this` receiver, the mutable loan of `recv` is *reserved* while
  `args` are evaluated (permitting shared reads) and *activated* at the call.
  Required for `v.push(v.length)`. This is the only two-phase site in v0.

### Escape (lifetime) rules

- **E1 — No escape beyond the owner.** A value of scoped type may flow only to:
  (a) locals of the same function; (b) callee arguments; (c) the function's
  return value, if the elision rule E2 links it to a parameter; (d) fields of
  scoped *local* aggregates (tuples/anonymous temporaries), which then become
  scoped.
- **E2 — Return elision.** A function returning a scoped type must have exactly one
  `borrow`/`borrow mut` parameter (including `this`), or must annotate the
  source: `fn pick(a: borrow T, b: borrow T) -> borrow(a) T`. The returned
  value's region is constrained to be within the named parameter's loan at the call
  site. A function with no borrow parameters cannot return a scoped type, except
  `static` data (string literals, `static let`), which have the `static` region.
- **E3 — No scoped fields (v0).** User `struct`/`enum` declarations may not
  contain scoped types in v0. Lifetime-parameterised aggregates
  (`struct Parser<'src> { input: StringView<'src> }` or similar syntax) are a
  *future* extension (OD-3).
- **E4 — No scoped globals or heap.** Scoped values may not be stored in
  `static`s, `Box`, `Vector`, or any free-standing container. In v0 this follows
  from E3 and from generic containers requiring free-standing element types.

E1–E4 deliberately trade expressiveness for an implementation of tractable size
(see "Why not full Rust lifetimes" below).

### Regions (arenas) and guards

- **R1 — Allocation borrows the region.** `arena.alloc(...)` takes `borrow this`
  and returns a scoped value carrying a *shared loan of the arena*. Therefore:
  - the arena cannot be dropped, moved, reset, or mutably borrowed while any
    allocation from it is live (B2);
  - arena allocations cannot escape the arena's scope (E1–E4).
- **R2 — Trivially-destructible contents (v0).** Arena allocation is
  permitted only for types with no `drop` (transitively). Bulk release is
  then semantically equivalent to individual destruction. Arena-with-drop-list is
  *future* (OD-8).
- **R3 — Pinned scoped bindings.** `using x = e;` declares a binding that cannot
  be moved, reassigned, or returned; it is dropped exactly at the end of the
  enclosing block. Arenas, lock guards and quiescence guards are normally bound
  with `using`. (Types may require `using` by declaration: `@scopedOnly`.)
- **R4 — Guard-protected borrows.** An API may return values scoped to a
  guard (`table.lookup(borrow guard, key) -> Obj?` where `Obj` is scoped to
  `guard`). By B2/E1 these cannot outlive the guard. This is the mechanism for
  epoch/quiescence-protected kernel lookups (`cosmoos/kernel-memory-and-lifetimes.md`).

### Heap

- **H1 — Box ownership.** `Box<T, A>` is a move type that owns exactly one
  allocation of `T` obtained from allocator `A`. Its drop runs `T`'s drop, then
  `A.free`. `A` must be a free-standing *allocator type* (§3 of
  `memory-model.md`). Allocations from non-free-standing allocators are scoped
  values (R1).
- **H2 — Collections.** `Vector<T, A>` owns its buffer. Element access returns
  loans of the vector (`borrow this`/`borrow mut this`), so growth (which takes
  `borrow mut this`) is rejected while element borrows are live (B1/B2).
  Stable references into collections use `SlotMap<T>`/`Handle<T>` with
  generation validation (library type, M6).
- **H3 — No owning cycles.** Only `Box` and owning collections own, and their
  ownership graph is a forest by construction (O1). Back-references and
  graphs use indices, handles, or arenas.

### Unsafe

- **U1 — Capabilities.** Raw pointer dereference, pointer arithmetic, calls to
  `extern` or `unsafe fn`, `transmute`, access to `static mut`, MMIO and inline
  assembly are allowed only inside `unsafe { }` or an `unsafe fn`.
- **U2 — Contract.** Every `unsafe` block must preserve O1–O5, B1–B5, E1–E4,
  R1–R4 and H1–H3 *as observed by safe code*. Every `unsafe fn` and every safe
  function containing `unsafe` must state its contract in a `/// Safety:` doc
  comment. A lint enforces that the comment exists.
- **U3 — Raw pointers are inert.** Raw pointers (`*T`, `*mut T`) are Copy,
  carry no loan, and are ignored by the borrow checker. Converting a raw pointer
  to a reference (`unsafe { borrowFromRaw(p) }`) creates a reference with an
  *unconstrained* region, which must be immediately bounded by a safe wrapper's
  signature (as with Rust's unbounded lifetimes; this is the main hazard in
  unsafe code).

## 3. How the compiler rejects invalid programs

Pass order (see `architecture.md`):

1. **Type check (HIR)**: kinds (Copy / move / scoped), E2 annotations,
   E3/E4 structural checks, `unsafe` context (U1), `using` restrictions (R3),
   arena element constraint (R2).
2. **NIR construction**: explicit `StorageLive/StorageDead`, `Move`, `Copy`,
   `Borrow(kind, place)`, two-phase reservation markers, and the `drop` sites
   still unelaborated.
3. **Definite-initialisation / move analysis** (O2–O3): forward dataflow, with a
   per-place bitset of {init, uninit, maybe}.
4. **Borrow check** (B1–B5, E1–E2, R1, R4):
   a. compute liveness of every reference-typed local;
   b. generate loans at `Borrow` statements and region variables for every
      reference type occurrence;
   c. generate outlives constraints from assignments, calls (signature
      instantiation), and returns (E2);
   d. solve regions as sets of points (NLL-style fixpoint);
   e. for every access, report a conflict if any live loan in the solved region at
      that point conflicts (B1/B2).
5. **Drop elaboration** (O4): inserts static drops and drop flags. Runs after the
   borrow check, so drops are checked as accesses in step 4 using the
   *unelaborated* drop sites.

Diagnostics name the loan's creation point, the conflicting access, and the
later use that keeps the loan alive, as three labelled spans. This is the
single most important usability feature of the checker (see `diagnostics.md`).

Example compile-fail cases (each becomes a test in `tests/corpus/compile-fail/`):

```nova
fn dangling() -> borrow i32 {         // error E2: no borrow parameter to derive from
    let x = 5;
    return borrow x;
}

fn useAfterMove(b: Box<Node>) -> void {
    consume(b);
    print(b.value);                    // error O3: `b` used after move at line 2
}

fn invalidate(v: borrow mut Vector<i32>) -> void {
    let first = v[0];                  // Copy of i32: fine
    let r = borrow v[0];               // shared loan of *v
    v.push(1);                         // error B1: mutable loan while `r` live
    print(r);                          //   ... `r` used here
}

fn arenaEscape() -> Slice<u8> {        // error E2: no borrow parameter
    using arena = Arena.create(SystemHeap)!;
    return arena.allocArray<u8>(16)!;
}
```

## 4. Why not full Rust lifetimes in v0

Named lifetime parameters on aggregates, higher-ranked bounds and variance are
where most of Rust's borrow-checker complexity and annotation burden live.
E1–E4 keep the analysis entirely **intraprocedural** (signatures summarise
callees completely), with no lifetime parameters on types. That is enough for:
borrowing parameters, slices, string views, iterators over a borrowed collection
(as a local), arena-scoped computations, and guard-scoped kernel lookups. It is
not enough for: long-lived parsers holding `StringView`s, structs of
references, or zero-copy data structures. Those are OD-3 (target M6), designed
so that v0 programs remain valid.

## 5. Behaviours requiring runtime checks

| Check | Elidable when | Pass |
|---|---|---|
| Array/slice bounds | Index proven `< len` by range analysis (M9) | Lowering emits `nova_check_index` |
| Integer overflow | Value ranges proven (M9) | `nova_add_i32` etc. |
| Division by zero / `MIN/-1` | Divisor is a non-zero, non-minus-one constant | — |
| Shift width | Constant shift amount | — |
| Optional unwrap `!` | Flow-typing after `!= null` | — |
| Drop flags | Static init state known (most cases) | Drop elaboration |
| Allocation failure | Never elided (it is a value) | — |
| Handle generation check (`SlotMap`) | Never in v0 | Library |
| Kernel: stack depth | Static call-graph bound (kernel profile) | Link-time report |

## 6. Testing obligations (M2 acceptance)

- Every invariant above has at least two compile-fail tests and one compile-pass
  test that is "close" to failing (for example NLL cases where a loan ends
  before the conflicting access).
- Mutation suite: for each compile-pass test, automated mutants (swap statement
  order, remove a scope, duplicate a move) whose expected verdict is computed
  independently, by hand-labelling or a reference interpreter, not by the
  checker itself.
- Generated C for compile-pass tests is run under ASan + UBSan in CI
  (supporting evidence only).
