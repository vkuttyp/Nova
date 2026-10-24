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
