# ADR 0009 — C7: context effects, and C state embedded by value

- Status: **Accepted** (project owner, 2026-10-24, choosing "embed by value" for kernel locks and objects
  over out-of-line C state). The rest is the agent's design under that choice, following
  `cosmoos/kernel-memory-and-lifetimes.md` §4–§7; the owner may revise it.
- Scope: C7 (`cosmoos/c7-report.md`). It amends ADR 0008 point 4 (imported types only behind pointers).

## Context

C7 wraps CosmoOS's locks, kernel objects and quiescence (audit in `cosmoos/c7-report.md`). Three facts from
the code decide the design:

1. The kernel's synchronisation objects are C state that lives **inside** the data it protects. `spinlock_t` is 24
   bytes (`kernel/include/kernel/spinlock.h:31-37`). `struct mutex` embeds a spinlock, a wait queue and list
   links (`mutex.h:20-35`). A `struct kobject` is embedded in its owner and found again with `container_of`.
2. Once initialised, some of this state **points into itself**. `mutex_init` runs `list_init`, which sets
   `head->next = head` (`kernel/scheduler/mutex.c:21-22`, `list.h:24-28`). Moving such a value bitwise
   corrupts it.
3. Atomic versus sleepable is a runtime property: `might_sleep()` (`lockdep.h:239`) panics on sleeping with
   preemption disabled. Nothing checks it statically, and nothing checks at all that a final `kobject_put`,
   whose release may block, is not made under a spinlock (`object.c:74-81` has no `might_sleep`).

## Decision

### 1. Context effects (C7a)
- Two effects: **`sleep`** (may block) and **`alloc`** (may allocate). A function's effects are those of
  everything it calls and every value it drops, inferred over the whole package. Built-in operations
  declare theirs (heap allocation is `alloc`; hosted I/O is `sleep`).
- `@effects(sleep, alloc)` (any subset; `@effects()` for none) **declares** them: on an `extern` function it
  is what Nova assumes, and on a Nova function it is an upper bound that the inferred set must fit. An extern
  function without `@effects` is assumed to have both, the most conservative choice. A call through a C
  function pointer has both.
- `@context(atomic)` (no `sleep`) and `@context(irq)` (no `sleep`, no `alloc`; KQ-1's Nova default) on a
  function forbid effects in its body. On a **struct**, `@context(atomic)` makes every point where a value
  of it is live (maybe initialised and not yet dropped) atomic: holding a spin-lock guard is an atomic
  context, with no annotation at the use.
- Effects are checked in **every profile**. They create obligations only where a context or an `@effects`
  bound is declared, so hosted programs without them are unaffected.
- The checker proves only that Nova code respects the declared effects of what it calls. CosmoOS's runtime
  checks (`might_sleep`, lockdep) stay the backstop. Nova generates no `might_sleep()` calls: the debug half of
  the macro calls `lockdep_might_sleep`, which modules cannot link to (it is not exported), and every sleeping
  primitive Nova calls has its own.

### 2. C state embedded by value (C7b)
- A Nova struct may hold an imported C type by value, and so may an imported struct (an embedded member).
  Such a type, and every type that contains one by value, is **C-laid-out**. Like an imported type, it has no
  Nova layout. Generated C defines it with the real C member types, so the C compiler lays it out, and it
  gets no layout assertions. The interpreter faults on it.
- C-laid-out values are **immovable**. They are made **in place** and never moved:
  - the only way to make one is a struct literal given directly to `Box.createIn` (or nested in such a
    literal);
  - imported fields are left out of the literal and **zero-filled** (C's `{0}`), which is the only imported
    value Nova can make;
  - the contents of such a box are never moved out;
  - a C-laid-out type cannot be a local, parameter, result, array element, enum payload, optional or
    type argument (a reference to one can be).
  `static` items come later (§4).
- **`@placed` methods** (`fn m(borrow mut this)`) run C's initialisers at the final address. After
  `Box.createIn` places the value, the compiler calls the `@placed` methods of its fields' types, innermost
  first in field order, then the type's own. They run before anything else can borrow the value, so code
  never sees uninitialised C state. A lock's `@placed` method calls `spinlock_init` with its name.
- `Ptr.containerOf` (unsafe) recovers a struct from a pointer to one of its fields, using C's `offsetof`.
  Kernel callbacks need it, since they receive the embedded `struct kobject *` or `struct quiesce_head *`.

### 3. CosmoOS bindings (C7c–C7e, `integrations/cosmoos/c7/`)
- `SpinLock<T>`, `IrqSpinLock<T>` and `Mutex<T>` hold their C lock and their data. `lock()` returns a guard
  that borrows the lock and gives `borrow mut T` access. Spin guards are `@context(atomic)`. `Mutex.lock` is
  `sleep`. Dropping a guard unlocks. Data is reachable only through a guard.
- `KRef<T>`, a counted reference to a Nova-defined kernel object (it embeds `kobject`). Its drop is
  `kobject_put`, with the `sleep` effect unless the type is `@nonblockingRelease`. A final put under a spin
  guard is then a compile error, the hole of fact 3.
- `Quiesce.synchronize()` (`sleep`). `Quiesce.deferDrop(box)` uses `call_quiesce` with an embedded
  `quiesce_head`. It is `unsafe` in modules, because module unload does not wait for pending callbacks
  (`module.c:627-631`; KQ-3). Its contract is that the module stays loaded until the callback has run.
- Not in C7: `QuiesceGuard` read sections in modules (`quiesce_read_unlock` inlines the unexported
  `preempt_enable`, `quiesce.h`; KQ-2; spin guards are read sections already), `PerCpu<T>` (CosmoOS exports
  no general per-CPU allocation), and statics (§4).

### 4. Deferred
`static` items (locks and caches with a lifetime of the module), and with them `ObjectCache<T>` (C5). Lock-order
checking stays lockdep's job.

## Consequences
- ADR 0008 point 4 now reads: imported types appear by value only in C-laid-out types, which are immovable and
  made in place. Everywhere else they appear behind pointers and references.
- `type-system.md` gains §32 (effects) and §33 (embedded C state). `profiles.md` §1 changes the effects row to
  checked everywhere. `kernel-memory-and-lifetimes.md` §5–§6 point to them.
- The proposals of `kernel-memory-and-lifetimes.md` that C7 leaves out stay proposals: `PerCpu`, statics,
  `Unlinked<T>`, `@scopedOnly`, and the irq-state ordering of nested `IrqSpinGuard`s beyond `using`'s LIFO.
