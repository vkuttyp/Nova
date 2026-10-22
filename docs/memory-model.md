# Nova Memory Model and Allocation Architecture (v0)

Status: Milestone 0 proposal. Safety rules referenced as O*/B*/E*/R*/H*/U* are
defined in `ownership.md`. Goose findings are in `research/goose.md` (commit
`f605b5a`). Other prior art is in `research/comparative.md`. The CosmoOS kernel
mapping is in `cosmoos/kernel-memory-and-lifetimes.md`.

## 1. Candidate architectures

### A. Ownership-based stack + heap (Rust-like)

Values live on the native stack by default. Long-lived and dynamically sized data
live in owned heap objects (`Box`, `Vector`, `String`). Safety comes entirely
from ownership and borrowing. Arenas are ordinary library types built with
`unsafe` plus lifetime-carrying APIs.

### B. Region-based + owned heap (Cyclone/Tofte–Talpin-like)

Every allocation is placed in a *region*. Regions are lexically scoped (stack frames,
dynamic regions) or the heap region. Pointer types carry a region, and an
outlives/effect system prevents dangling. Owned heap objects exist as unique
pointers in the heap region. Region inference reduces annotations.

### C. Hybrid: ownership + explicit regions + conservative escape analysis

Architecture A's ownership/borrow core is *the only* safety mechanism. Regions
(arenas, scratch stacks, fixed buffers) are **explicit library values whose
allocations are loans of the region** (R1), so the borrow checker covers them
with no separate region type system. A conservative escape/placement analysis
(M5) may move allocations to cheaper storage only when the move is
**unobservable** (§5). Goose-derived techniques (data stacks for scoped
temporaries, construction in place, grow-only containers, typed pools, BCE)
are adopted as **implementation strategies and library types**, not as the
safety argument.

## 2. Evaluation

| Criterion | A: Ownership | B: Regions | C: Hybrid |
|---|---|---|---|
| Safety argument | One mechanism (affine + XOR borrows). Proven for the λRust core | Region type system + uniqueness. Proven for core calculi, but **two** interacting mechanisms in practice | Same as A (one mechanism). Regions reduce to loans. Escape analysis is outside the trusted base |
| Kernel fit (no hidden allocation, failure values) | Good | Good if regions are explicit. Poor with inference (hidden placement) | Good: placement changes require the profile to permit them (§5), and the kernel profile forbids implicit ones |
| Ergonomics for C#/TS users | Annotation burden for structs of references. v0 limits mitigate (E3) | Many annotations, or inference with unpredictable lifetimes (MLKit space leaks) | As A, plus `using arena = ...` for batch lifetimes. Diagnostics explain placement |
| Implementation complexity | High (NLL borrow checker) | High (region inference + effects) **plus** uniqueness | A's complexity + an *optional* M5 pass. Region support costs nearly nothing extra |
| Runtime cost | `malloc`/`free` per heap object unless the user picks an arena | Cheap bump allocation everywhere; risk of space leaks | Same as A by default. Arena and scratch where the user or proven-safe analysis selects them |
| Predictability | High | Low with inference | High: placement is either explicit or reported by `--explain-allocations` |
| Goose techniques usable | Some (in-place construction, BCE) | Data stacks map naturally to lexical regions | All the "cheap, local" ones (goose.md §9 key cautions) |
| Failure mode if the analysis is wrong | Unsound borrow check → safety bug | Unsound region inference → safety bug | Unsound borrow check → safety bug. Unsound placement → **caught**, because placement output is re-validated by the checker (`architecture.md` §1) |

### Recommendation: **C — Hybrid**

1. It keeps **one** safety mechanism to specify, test and eventually verify. Goose's
   own history shows the cost of a second, fragile mechanism: its lifetime checker is graded
   "C/D, structurally fragile" by its own inventory, and roughly half of its
   recent commits fixed holes (goose.md §9.4). Option B would add a region type
   system *alongside* uniqueness. Option C reduces regions to borrows.
2. Arenas, scratch regions and fixed buffers are first-class from M4 with no
   new type-system concept: "an allocation borrows its region" is checked by
   the same B1/B2/E1 rules as `borrow x`.
3. Escape analysis stays outside the safety argument (master prompt rules 10–11),
   and its effects are visible and reportable.
4. It fits CosmoOS: every allocation in the kernel profile names its allocator
   type or region value, so nothing allocates implicitly.

Cost: the v0 restriction E3 (no references in user structs) is felt most
for arena-heavy code (e.g. AST nodes pointing to other arena nodes). The v0
answer is indices or `Span` offsets (Ex16), and the long-term answer is OD-3.

## 3. Storage categories

### 3.1 Native stack (C automatic storage)
- Scalars, fixed-size structs/enums/arrays, references, `Box` pointers, temporaries.
- Size limit per value: `maxStackValue` per profile (userland default 64 KiB,
  kernel default 512 B). Larger fixed-size locals are a compile error suggesting
  `Box` or a region. This avoids unbounded native-stack consumption.
- No variable-length arrays (C VLAs and `alloca` are never emitted).
- Kernel profile: per-function frame-size estimates are reported, and the deepest
  call chain is checked against the kernel stack budget (C4/C7; see
  `cosmoos/profiles.md`).

### 3.2 Heap (owned)

```nova
// core library (sketch)
public struct Layout { size: usize; align: usize; }      // align: power of two

public interface Allocator {
    // `this` is a free-standing allocator value: ZST token or a 'static handle.
    unsafe fn allocate(borrow this, layout: Layout) -> Result<*mut u8, AllocError>;
    unsafe fn deallocate(borrow this, ptr: *mut u8, layout: Layout) -> void;
    unsafe fn reallocate(borrow this, ptr: *mut u8, old: Layout, newSize: usize)
        -> Result<*mut u8, AllocError>;                 // default: alloc+copy+free
}
```

- *Free-standing allocator types* (`SystemHeap`, `KernelHeap<Flags>`,
  `SlabCache<T>`-backed allocator singletons) are zero-sized or `'static`.
  Only these may parameterise `Box<T, A>`, `Vector<T, A>`, `String<A>` (H1).
  This keeps `Box` one pointer wide and makes "Box outlives allocator"
  trivially true.
- *Instance allocators* (arenas, pools, fixed buffers) are values. Their
  allocations are scoped values carrying a loan of the instance (R1). A
  `Pool<T>` hands out `PoolBox<T>` (scoped, individually freeable). An `Arena`
  hands out `borrow mut T` / `SliceMut<T>` (freed in bulk).
- **Allocator lifetime**: free-standing allocators live for the whole program
  (or kernel lifetime). Instance allocators cannot be dropped while allocations
  are live (B2). Outstanding allocations therefore can never outlive their
  allocator (master prompt §5.5).
- **Ownership of allocations**: always exactly one owner (O1). Freeing with the wrong
  allocator is impossible because the allocator is part of the type (`A`) or of the loan.
- **Thread safety**: an allocator type declares `Sync` if it may be used
  concurrently (`SystemHeap`, `KernelHeap`: yes; `Arena`, `FixedBuffer`: no).
  Enforced once threads exist (M-later).
- **Failure**: every allocating API returns `Result<_, AllocError>`.
  Convenience `...OrPanic` / `!` usage is allowed in userland and **rejected by
  a lint (error) in the kernel profile**.
- **Testability**: `FailingAllocator<A>(failAfter: n)` and a fault-injection
  hook in `SystemHeap` (env var in hosted test builds) make every failure path
  testable (M3 acceptance).
  - *As implemented in M3b (ADR 0003):* `FailingAllocator` is a built-in, zero-sized allocator
    type over the platform heap, steered by `FailingAllocator.failAfter(n)` and `.reset()`
    (a process-wide countdown).
  - The runtime counts every allocation attempt. `NOVA_FAIL_ALLOC_AT=k` fails attempt *k*,
    and `NOVA_ALLOC_REPORT=1` prints `nova alloc: attempts=<n> live=<m>` when `main` returns.
  - The test runner's sweep uses both (`testing.md`).

### 3.3 Arenas (regions)

- `Arena.create(parent: A, chunkSize)`, an instance allocator over a free-standing
  parent. Bump allocation in chunks, with all chunks released at drop.
- v0 contents must have no `drop` (R2), so bulk release equals destruction.
  Arena-with-drop-list is OD-8.
- `reset(borrow mut this)` requires no live loans (B2), so it is safe by
  construction.
- An arena is **not** the native stack: it is heap-backed (master prompt rule 9).

### 3.4 Scratch regions (Goose-style data stacks)

From goose.md §9.1/§9.2: a per-thread, compiler-known **scratch stack** used
for dynamically sized temporaries with nested lifetimes:

```nova
fn render(n: usize) -> Result<void, AllocError> {
    using frame = Scratch.enter();                 // records watermark
    let buf = frame.allocArray<u8>(n)?;            // bump; scoped to `frame`
    ...
}                                                  // watermark restored: one store
```

- Semantically an arena with LIFO discipline. Safety follows from R1 + R3
  (`using` frames cannot be moved, and their allocations cannot escape).
- Storage: hosted/userland uses a reserved virtual region with guard pages,
  plus an explicit capacity check. Exhaustion returns `AllocError`
  **unless** the frame was created with `Scratch.enterOrHeap()`, in which case it falls back
  to the parent heap. Kernel: a fixed per-thread buffer of profile-defined size
  with explicit checks. No reliance on page faults.
- Nested frames must be strictly LIFO. *Corrected by ADR 0004:* `using` alone does not ensure
  this. Nested frames come from `parent.enter()`, which mutably borrows the parent, and only
  one root frame per thread may be live (a second `Scratch.enter()` panics).
  Goose's "one growable on top" rule is **not** adopted in v0. The scratch region
  hands out fixed-size allocations only. Growable scratch arrays are a future
  extension (OD-12).

### 3.5 Fixed-buffer and pool allocators
- `FixedBufferAllocator.over(mut storage)` borrows its backing storage mutably
  for its whole lifetime, so the backing array cannot be touched directly while it is used.
- *As implemented in M4c (ADR 0004):* `Pool<T>` is a fixed-capacity pool whose `take(v)` returns a
  scoped `PoolBox<T>`: a box that holds a loan of the pool and returns its slot when destroyed
  (`type-system.md` §19.6). `SlotMap` with generation-checked handles is still M6.
- `Pool<T>` / `SlotMap<T>`: typed slots with generation counters (goose.md §9.8
  plus Vale). Handles are free-standing `Handle<T>` values (index + generation)
  validated on access (`get(h) -> borrow T?`).

### 3.6 Static data
String literals and `static let` constants (immutable). `static var` is
`unsafe` to access in v0 (data races); safe statics with synchronisation come with
concurrency.

## 4. Collections and invalidation

| Type | Ownership | Growth | Element references | Notes |
|---|---|---|---|---|
| `[T; N]` | value | — | loans of the array | Copy iff T Copy |
| `Slice<T>` / `SliceMut<T>` | scoped borrow | — | reborrows | bounds-checked |
| `Vector<T, A>` | owns buffer | reallocates (amortised ×2) | loans of the vector. Growth requires `borrow mut` → **rejected while element loans are live** | contiguous; **no address stability promise** |
| `StableVector<T, A>` (M6) | owns chunks | appends a chunk, never moves | loans with *shared* access permitted during `push` (grow-only: goose.md §9.3) | push takes `borrow this` with interior bump. Not `Sync` |
| `SlotMap<T, A>` (M6) | owns slots | may reallocate | `Handle<T>` (not a loan) validated by generation | stable *identity*, not stable addresses |
| `String<A>` / `StringView` | owns / scoped | as Vector | as Vector | UTF-8 invariant |
| `Box<T, A>` | owns one object | — | loans of the box | `T?` niche |

Iterator invalidation is impossible in safe code: iterators are scoped values
holding a loan of the collection.

## 5. Placement analysis (M5) — rules

The analysis may change *where* a value lives only under these conditions:

1. **Box → native stack / scratch**: allowed iff (a) the Box is created and
   dropped in the same function on all paths, (b) it is never moved into a
   callee or returned, (c) its address is not exposed to `unsafe`/FFI, (d)
   the profile marks `A` as *placement-transparent* (`SystemHeap` in userland: yes;
   **`KernelHeap`: no**, because kernel allocation is observable through
   accounting, and failure injection must remain meaningful), and (e) the size is
   below the profile's stack limit. Failure behaviour: the original program could fail
   with `AllocError`, and the optimised one cannot. This is permitted only because the
   placement-transparent contract declares allocation failure of that allocator
   "may spuriously succeed".
2. **Never** the reverse: a scoped/borrowed value is never promoted to the heap to
   make a program type-check (master prompt §7 and rule 11). There is no such
   code path, because placement runs after the borrow check on accepted programs only.
3. Destruction order and timing are preserved (master prompt rule 12): drops of
   demoted Boxes run at exactly the same NIR points.
4. Every decision is reported by `--explain-allocations`:
   `src/a.nova:12:9: Box<Node> placed on stack (does not escape; SystemHeap is placement-transparent)`
   `src/a.nova:20:9: Box<Node> stays on heap: moved into call to 'insert' at 21:5`

## 6. No-GC baseline and cycles

No tracing GC, no implicit RC. Ownership is a forest (H3). Graph-shaped data
uses one of: `SlotMap` handles, arena + indices, or (future, OD-13) an explicit
`Rc<T>`/`Weak<T>` library type with documented cycle-leak behaviour. In
the kernel, explicit refcounted *kernel objects* wrap CosmoOS's own refcount
protocol (`cosmoos/kernel-memory-and-lifetimes.md`). That is a different
type (`KRef<T>`) with type-specific release, not a generic `free`.

## 7. Goose technique adoption summary

| Goose technique (goose.md §9) | Nova adoption | Milestone |
|---|---|---|
| Data stacks for scoped temporaries | `Scratch` regions (§3.4), with heap fallback and a no-MMU kernel mode | M4/M5 |
| One-growable-on-top non-moving growth | Not adopted; `StableVector` instead | M6 |
| Grow-only vs grow-shrink split | `StableVector`, monotone lengths feed BCE | M6/M9 |
| Root inference by monomorphisation | **Rejected** (whole-program, fragile). NLL + E1–E4 instead | — |
| Construction into destination / guaranteed NRVO | NIR destination places + guaranteed RVO for single-named-result functions | M2 (RVO), M7 |
| Inline variable-size layouts | Future opt-in `@flat` types (OD-14) | post-M9 |
| Relative references + verifier | Future library feature for zero-copy formats | post-M9 |
| Type-safe pools | `Pool<T>`/`SlotMap<T>` with generations | M6 |
| BCE with index types, 2^48 size cap, `checkfree` | Adopt the size cap in the spec now (all `len ≤ 2^48`), BCE + `@checkfree` in M9 | M0 (spec) / M9 |
| `return … from` | Not adopted; `Result` + `?` | — |
| Share-nothing workers | Possible library model later | post-M9 |

**Spec rule adopted now (from Goose BCE):** every object, slice and collection
length is `≤ 2^48 − 1` elements. Allocators reject larger requests with `AllocError`. This makes
`len * sizeof(T)` overflow-free for `sizeof(T) < 2^15` and lets index arithmetic
on `usize` be proven non-overflowing.
