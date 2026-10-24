# C7 — Kernel concurrency integration (plan and report)

Status: **in progress** (plan accepted 2026-10-24; ADR 0009). Roadmap acceptance: *effects, `SpinLock<T>`/`Mutex<T>`
guards, `KRef`, `QuiesceGuard`, `PerCpu`, deferred reclamation; negative effect tests; lockdep clean under QEMU
stress; real concurrency rules exercised* (`roadmap.md`). Prompt (`cosmoos-native.md` C7): investigate and introduce
context effects, kernel object borrowing, lock guards, epoch guards, and deferred reclamation compatibility; test
against the kernel's real concurrency rules. Stage S0: out of tree, CosmoOS read-only.

## Audit (CosmoOS `5cfd1ffe`, v1.0.13; read, not copied)

The audits of C4–C6 were at `98040b9b`; this one re-reads every interface C7 touches.

| Interface | Where | What it says |
|---|---|---|
| Spinlocks | `kernel/include/kernel/spinlock.h:31-60`, `kernel/core/spinlock.c:30-120,211-220` | `spinlock_t` is C state (`locked`, `owner_cpu`, `name`, lockdep `class`; 24 bytes) set up by `spinlock_init(lock, name)`; taking one disables preemption first; lockdep keys the class on the name; re-acquiring on the same CPU panics. `spin_lock`, `spin_unlock`, `spin_lock_irqsave`, `spin_unlock_irqrestore` (`arch_irq_state_t` = `unsigned long`), `_nested` forms and `spin_trylock` are exported |
| Mutexes | `kernel/include/kernel/mutex.h:20-44`, `kernel/scheduler/mutex.c:13-23,243-247` | `struct mutex` embeds a spinlock, a wait queue and a list node; after `mutex_init` the list heads point to themselves, so an initialised mutex cannot move. `mutex_lock` sleeps; init, lock, trylock and unlock are exported |
| Sleeping check | `kernel/include/kernel/lockdep.h:233-248` | `might_sleep()` is a macro: it panics when `preempt_count` or `irq_depth` is non-zero; with `CONFIG_LOCKDEP` it reports through `lockdep_might_sleep`, which is not exported |
| Preemption | `kernel/include/kernel/percpu.h:148-187` | `preempt_disable` is inline; `preempt_enable` is out of line and **not exported**; `arch_percpu_get` and `arch_cpu_id` are exported (`kernel/arch/*/percpu.c`, `kernel/core/percpu.c:214-215`) |
| Quiescence | `kernel/include/kernel/quiesce.h`, `kernel/core/quiesce.c:405-408` | read sections are preemption-disabled regions (every spinlock is one); `quiesce_read_lock/unlock` are inline and call `preempt_enable`, so a module cannot use them (KQ-2 still open); `synchronize_quiesce` sleeps; `call_quiesce(head, fn)` runs `fn` in thread context after a grace period, with the embedded `quiesce_head` owned by the subsystem until then; all of these and the `_debug` halves are exported |
| Module unload | `kernel/module/module.c:614-660` | `shutdown()`, then one `synchronize_quiesce()`, then a wait for objects whose release is in the module; pending `call_quiesce` callbacks are not waited for (KQ-3 still open) |
| Kernel objects | `kernel/include/kernel/object.h`, `kernel/object/object.c:10-119` | `struct kobject` is embedded in its owner; `kobject_init(obj, type)` with a mandatory `release`; `get`, `tryget`, `put`, exported; the final put runs the release, which may block, with **no** `might_sleep` |
| Threads | `kernel/include/kernel/thread.h:163-185`, `wait.h:243-246` | `thread_create(entry, arg, name, priority)`, `thread_join`, `thread_sleep_ns`: exported |
| Debug build | `build/config.mk:10-17`, `build/toolchain.mk:42,52-55` | `BUILD=debug` (the default) sets `CONFIG_DEBUG=1` and `LOCKDEP=1`; lockdep reports `lockdep: <kind>` and panics (`kernel/core/lockdep.c:313-328`) |

## Plan

Decisions are in ADR 0009. The increments, each with its own tests and commit:

| Increment | Content | Where |
|---|---|---|
| **C7a** context effects | `sleep`/`alloc` inferred over the package; `@effects` bounds and extern declarations; `@context(atomic|irq)` on functions and on guard types; drops count; checked in every profile; negative corpus | `type-system.md` §32 |
| **C7b** embedded C state | imported types by value in Nova structs and embedded in imported structs; C-laid-out, immovable types made in place by `Box.createIn`; zero-filled imported fields; `@placed` methods; `Ptr.containerOf` | `type-system.md` §33 |
| **C7c** lock guards | `SpinLock<T>`, `IrqSpinLock<T>`, `Mutex<T>` and their guards over CosmoOS's locks | `integrations/cosmoos/c7/sync.nova` |
| **C7d** kernel objects and quiescence | `KRef<T>` over an embedded `kobject`, `Quiesce.synchronize`, `Quiesce.deferDrop` (unsafe in modules) | `integrations/cosmoos/c7/` |
| **C7e** the kernel's rules, in the kernel | an out-of-tree module (the C5 pipeline) in CosmoOS's **debug** build (lockdep on), on both arches in QEMU: kernel threads contending on Nova locks, counted references across threads, grace periods and deferred frees; the results must be exact and lockdep silent; runtime negative controls in separate boots | `integrations/cosmoos/c7.py` |

Out of C7 (ADR 0009 §3–§4): `static` items, `ObjectCache<T>`, `PerCpu<T>`, `QuiesceGuard` in modules (blocked on
KQ-2), Nova lock-order checking.

## Results

### C7c — lock guards (2026-10-24)

`integrations/cosmoos/c7/sync.nova`, over CosmoOS's own `kernel/spinlock.h` and `kernel/mutex.h` (imported, §31):

| Type | Holds | `lock()` | Guard |
|---|---|---|---|
| `SpinLock<T>` | `spinlock_t` by value, the lockdep name, `T` | `spin_lock` | `SpinGuard<T>`: `@context(atomic)`; drop `spin_unlock` |
| `IrqSpinLock<T>` | the same | `spin_lock_irqsave` | `IrqSpinGuard<T>`: `@context(atomic)`, keeps the IRQ state; drop `spin_unlock_irqrestore` |
| `Mutex<T>` | `struct mutex` by value (self-pointing once initialised), the name, `T` | `mutex_lock` (`@effects(sleep)`) | `MutexGuard<T>`: drop `mutex_unlock` |

- Each lock is made by `create(name, data)` in a `KernelHeap` box. Its `@placed` method runs `spinlock_init` or
  `mutex_init` at the final address (§33).
- `guard.data()` is the only way to the data, and its `borrow mut T` borrows the guard. Every field is `private`, a
  rule new in C7c (type-system §12: file-private, as `language-spec.md` §5 defines `private`; N0296), so no other file
  can reach the data, forge a guard, or skip the lock.
- C5's binding now declares `kmalloc` `@effects(alloc)` (it never sleeps; KQ-4) and `kfree` `@effects()`, so kernel
  allocation is allowed under a spin guard and forbidden in `@context(irq)`.
- Doc comments now attach to `drop` blocks, so a `drop` that uses `unsafe` can carry its `Safety:` line.

`integrations/cosmoos/c7.py` (run 2026-10-24, CosmoOS `5cfd1ffe`, macOS host):

| Check | Result |
|---|---|
| 6 negative cases (`c7/negative/`): a mutex under a spin guard and under an irq guard, a sleeping callee under a spin guard (the message names the chain down to `mutex_lock`), making a lock in `@context(irq)` (allocation), reaching `data` or building a guard from another file, moving a guard while its data is borrowed | each gives exactly its annotated error (N0295 ×4, N0296 ×2, N0240) |
| Host test (`c7/host_test.c`) with CosmoOS's `HOST_CFLAGS` (ASan, UBSan, `-Werror`), its headers, `HOST_COMMON_SRCS` (harness, spinlock shim) and `c7/host_kernel.c` (a mutex shim that panics on sleeping with a spinlock held, as `might_sleep` does, and on a mutex that moved): 100 spin-locked updates with one lock seen held under each guard and none after; two nested irqsave locks; a spinlock under a mutex 50 times | PASS |
| Negative control: a `SpinGuard` drop that does not unlock | caught: `host spinlock 'nova.c7.stats' re-acquired (deadlock)` |
| AArch64 object under `MODULE_CFLAGS`, with `c4/nova_rt_kernel.c` and `c7/kernel_glue.c` (`c7_locks_held` from `preempt_count`): C4's checks | PASS; undefined only kernel exports (`spin_lock`, `spin_lock_irqsave`, `spin_unlock`, `spin_unlock_irqrestore`, `spinlock_init`, `mutex_init`, `mutex_lock`, `mutex_unlock`, `kmalloc`, `kfree`, `arch_percpu_get`, `panic`); largest frame 64 B |
| x86-64 object, the same | PASS; largest frame 56 B |
| CosmoOS working tree unchanged | PASS |

Not shown here, and left to C7e: the locks under real concurrency (several CPUs and threads) and lockdep, in the
kernel. Moving a guard is allowed (it is an ordinary value); nested irqsave guards rely on locals being dropped in
reverse order, which a moved guard could break (the C0 design's note); C7e exercises the ordinary use.

### C7b — C state embedded by value (2026-10-24)

`type-system.md` §33 (ADR 0009 §2). Imported types are now allowed by value as struct fields, as embedded members of
imported structs (`struct emb_dev { lock: emb_lock; link: emb_list; }`, checked with `_Generic` only) and as a box's
contents. A type that holds one is C-laid-out:

- generated C defines it with the real member types and asserts nothing about its layout;
- every other by-value use of it is N0294, checked once struct fields are known;
- a new NIR pass, `Analysis/InPlaceChecker.cs`, run before move checking, accepts a value of it only on the way from
  its literal into `Box.create`/`createIn`, or into an enclosing literal;
- imported fields are left out of literals and zero-filled, through a new NIR operand, `NirZero`;
- `@placed` methods run in field order, innermost first, from a generated `nvPL_<T>` helper right after the value is
  stored in its box, and their effects count as the box's (§32);
- the interpreter and placement analysis refuse such types instead of guessing a layout.

`Ptr.containerOf` moved to C7d, whose generated callbacks are its only users. Doc comments before attributes now
attach to methods and extern functions too, as they did to top-level items.

Tests (run 2026-10-24, local suite, 1,967 tests):

- `ffi/embedded.nova` in every C configuration. Two queues with self-pointing C list heads and a lock are nested in a
  struct with a lock of its own; placement order is checked (1–4), and the rings are checked in C after linking one
  into the other. Also an all-C-state struct that stays zero-filled, and an imported struct's embedded members passed
  to C in place;
- `check-fail/c-state-types.nova` (8 forbidden positions and 6 misused `@placed`, plus the array case's existing
  N0259);
- `check-fail/c-state-moves.nova` (5 moves and misplaced literals, with valid in-place uses in the same file);
- `check-fail/effects.nova`, which now covers a sleeping `@placed` method under an atomic context;
- the interpreter fault.

The generated C of every existing program is unchanged (emit-c snapshots), so no guest runs were needed. Kernel flags
are exercised with C7c's real CosmoOS types.

### C7a — context effects (2026-10-24)

`type-system.md` §32. `@effects(sleep, alloc)` and `@context(atomic|irq)` on functions, methods and impl methods (which
may now carry attributes; grammar §2), and `@context(atomic)` on structs. `Analysis/EffectChecker.cs` runs on NIR after
drop elaboration:

- it infers each compiled function's effects to a fixed point over the package, counting calls, built-ins
  (table of §32.1) and drops (drop blocks, contents, user deallocators);
- it reports each operation whose effect its context forbids, with the chain of calls that leads to it (`'atomicBody'
  is @context(atomic), but this may sleep: it calls 'slower', which calls 'slow', which calls 'c_block', declared
  @effects(sleep)`);
- for `@context(atomic)` values, a forward may-analysis tracks where a holder local may be live. A guard moved into a
  callee makes the callee answer for it.

Tests (run 2026-10-24, local suite): `check-fail/effects.nova` (13 violations: atomic and irq functions, a live
guard, a guard live on one branch, a guard held by value in a struct, a guard passed to a callee, a sleeping drop
under a guard, `@effects` bounds, an extern without `@effects`, recursion, a generic function instantiated three times and reported once), with no other diagnostic and no false
positive on the guard-free paths in the same file; `check-fail/effect-attributes.nova` (9 misuses);
`run-pass/effects.nova` in every C configuration, the interpreter and the fault-injection sweep. The existing corpus
needed one change: the placeholder effect names of M8b (`may_block`, `nosleep, noalloc`) became `sleep` and `()`.
