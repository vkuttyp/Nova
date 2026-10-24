# CosmoOS Kernel Integration — Allocators, Addresses, Object Lifetimes, Quiescence, Synchronisation

Status: C0 design proposal (deliverables 5 and 6 of `prompts/cosmoos-native.md` §12). Since C5,
`KernelHeap` (§2) is implemented; the rest is not. **Nothing here changes CosmoOS.** Facts are taken from
`docs/research/cosmoos-kernel-audit.md` (CosmoOS HEAD `30d34b29`, main `4527d16d`),
cited as **[KA §n]** and **[KA impl n]** (its "Implications" list). API names below are
proposals; the prompt says they "do not establish final API names".

## 1. Principles

1. **Wrap, don't replace.** Every Nova kernel abstraction is a thin, typed
   wrapper over an existing CosmoOS C interface, with the same contracts. Nova never
   modifies a kernel allocator, the module loader, lockdep or quiescence.
2. **Distinct capabilities stay distinct.** Heap memory, cache objects,
   physical pages, kernel virtual ranges and DMA buffers are different types with
   different free partners [KA impl 1, 4].
3. **Compile-time effects approximate, runtime checks remain.** Atomic versus sleepable
   is a runtime property in CosmoOS (`preempt_count`, `irq_depth`) [KA §5].
   Nova's static effects reject programs that are certainly wrong. Generated code
   keeps CosmoOS's `might_sleep()` checks and lockdep annotations [KA impl 15, 17].
4. **Two kernel surfaces.** *Built-in* Nova code (linked into the kernel image)
   may use internal APIs (pmm, vm_kernel_*). *Module* Nova code may use only
   exported symbols [KA impl 1, 23]. The profile makes the difference a
   compile-time error, not a link error (open question KQ-5).

## 2. Allocation capabilities

| Nova type (proposed) | C interface | Free partner | Failure | Contexts today | Surface |
|---|---|---|---|---|---|
| `KernelHeap` (ZST allocator) — **implemented in C5** (`integrations/cosmoos/c5/kernel.nova`, `c5-report.md`) | `kmalloc(size, flags)` / `kfree` | `kfree(p)` | `NULL` → `AllocError` | non-sleeping, IRQ-safe [KA §1.1] | built-in + module |
| `ObjectCache<T>` declared as a `static` → allocator type `C` (not yet: needs statics or scoped cache boxes; `c5-report.md`) | `kmem_cache_create/alloc/free` | `kmem_cache_free(cache, obj)` (cache identity is in the type, so a mismatch is impossible) | `NULL` | non-sleeping | built-in + module (inferred; confirm exports) |
| `PageBlock` (owned) | `pmm_alloc_pages(order, zone)` | `pmm_free_pages(page, order)`: the order is stored in the value | `NULL` | non-sleeping | **built-in only** |
| `KernelVirtualRange` (owned) | `vm_kernel_alloc(...)` | `vm_kernel_free(base)` (needs IRQs enabled: TLB shootdown [KA open 5]) | `0` | thread context | **built-in only** |
| `DmaBuffer<T>` (owned, typestate) | `dma_alloc(dev, size, ...)` | `dma_free(dev, size, va, dma)`: all four fields stored | `NULL` | thread context only [KA impl 2] | per export list |

Notes:
- `kmalloc` limits: 16-byte alignment, maximum 4 MiB [KA §1.1]. `KernelHeap.allocate` with
  `layout.align > 16` or `size > 4 MiB` returns `AllocError` without calling C.
- `KMEM_ZERO` maps to `Box.createZeroed`. `krealloc` growth does **not** zero the tail beyond
  the old usable size [KA impl 5]. `Vector<T, KernelHeap>` growth never relies on zeroing.
- Misuse (double free, wrong cache, wrong order) panics CosmoOS in every build
  [KA impl 3]. Nova's ownership (O1/O4/H1) makes it unreachable from safe code.
  This is one of the clearest wins for the integration.
- No GFP-style context flags exist (allocation never sleeps). Nova still
  models allocation as an effect (`alloc`) so that `@noalloc` holds in IRQ handlers,
  as the `interrupt.h` policy requires, even though the C allocator technically permits it
  (KQ-1: which rule governs?).
- **DMA typestate.** `DmaBuffer<T, CpuOwned>` ⇄ `DmaBuffer<T, DeviceOwned>` via
  consuming `toDevice()`/`toCpu()`, which call `dma_sync_*`. CPU access (`view()`,
  `viewMut()`) exists only on `CpuOwned`. Today the sync calls are barriers only
  (coherent platforms), but the API is ready for non-coherent ports [KA impl 8].

`Box<T, KernelHeap>` lowers to a bare `T*` with `kfree` on drop. Drop is
non-sleeping (`kfree` never sleeps), so dropping a kernel Box under a spinlock
is permitted.

## 3. Address and MMIO types

CosmoOS uses plain integer typedefs (`paddr_t`, `vaddr_t`, `pfn_t`,
`dma_addr_t`) [KA §2, open 1]. Nova adds the distinction on its side only:

```nova
public struct PhysAddr { raw: u64; }      // lowers to paddr_t
public struct VirtAddr { raw: usize; }    // vaddr_t
public struct Pfn      { raw: u64; }      // pfn_t
public struct DmaAddr  { raw: u64; }      // dma_addr_t
public struct MmioRegion { base: VirtAddr; len: usize; }   // bounded, from pci_map_bar / device_map_mmio
```

- No `as` conversions between them, and no arithmetic except `offset(usize)` with
  overflow checks. Conversions only through wrappers of `phys_to_virt` /
  `virt_to_phys` (guarded by `phys_in_direct_map` → `VirtAddr?`) and `dma_map` [KA impl 6].
- MMIO: no kernel accessor layer exists [KA impl 7]. Nova's `MmioRegion.read32(off)` /
  `write32(off, v)` bounds-check `off + 4 <= len` and alignment, then emit an exact-width
  `volatile` access through `nova/rt_kernel.h`. `Mmio.doorbellBarrier()` wraps
  `arch_dma_barrier()`.
- Creating an `MmioRegion` from a raw address is `unsafe` (the region must be mapped and exclusively owned).

## 4. Kernel objects (`kobject`)

**Implemented in C7d** (`integrations/cosmoos/c7/kobject.nova`, `quiesce.nova`; type-system §34): `KRef<T>` and the release
callback as proposed, with the type embedded in each object (no statics) and no `@nonblockingRelease` yet; `Quiesce.synchronize`
and `deferDrop` (unsafe in modules, KQ-3) from §5; `QuiesceGuard` stays blocked on KQ-2.

Facts [KA §3.1, impl 9–10]: kobjects are embedded by value with a mandatory
`kobject_type.release`. Lookups return *counted* references. The final put may run a
release that **may block** and must not run under a spinlock, which nothing checks.

Nova design:

```nova
@kobject(release: Self.release)            // generates the kobject_type and embeds struct kobject
struct Pipe { obj: KObjectHeader; buf: Box<Ring, KernelHeap>; ... }

public struct KRef<T: KObject> { ... }     // owned counted reference; lowers to T*
impl<T: KObject> KRef<T> {
    fn clone(borrow this) -> KRef<T>;      // kobject_get
    // drop → kobject_put; effect: `sleep` unless T is declared @nonblockingRelease
}
fn tryGet<T: KObject>(r: borrow T) -> KRef<T>?;   // kobject_tryget, for table lookups
```

- `KRef<T>` is **not** `Box`: it is never `kfree`d by Nova. The release
  callback is the only destructor path, matching "do not implement generic
  automatic free for kernel objects" (prompt §4).
- The drop of a `KRef` carries the `sleep` effect by default, so dropping the last
  reference while holding a `SpinGuard` is a **compile error**. That closes a hole
  CosmoOS currently leaves to review [KA open 9]. Types whose release provably does not
  block opt out with `@nonblockingRelease` (the release body is effect-checked).
- Capability handles are `int` slots with **no generation** [KA impl 14]. Nova
  exposes them as `HandleSlot` values that are explicitly *not* identities. A future
  CosmoOS change could add generations, but that is not proposed here.
- Modules: every release function of a module-defined kobject type is emitted into
  the module's own text, so `module_owner_of` attributes it correctly [KA impl 25].

## 5. Quiescence (epoch) guards

Facts [KA §3.3, impl 11–13]: a read section is any preemption-disabled region,
which includes every spinlock and IRQ handler. `synchronize_quiesce` sleeps.
`call_quiesce` may be called anywhere and needs an embedded idle `quiesce_head` that the
subsystem owns until the callback runs.

```nova
using guard = Quiesce.read();                 // quiesce_read_lock (= preempt_disable, nestable)
let obj = table.lookup(guard, key);           // obj: borrow Obj scoped to `guard` (R4) — no refcount
if (obj != null) { obj!.inspect(); }
let keep = tryGet(obj!)?;                     // promote to KRef to outlive the guard
                                              // guard dropped: quiesce_read_unlock
```

Rules:
- `QuiesceGuard` is `@scopedOnly` (must be bound by `using`), not `Send`, and its scope
  forbids the `sleep` effect: no `Mutex.lock`, no `synchronize_quiesce`, no
  `KRef` drop with a blocking release, no `dma_alloc`.
- Borrows obtained under the guard are scoped to it (E1/R4), so **an epoch-protected
  borrow cannot escape the protection interval**. This is the property the prompt requires
  (§4, "Epoch example").
- Any `SpinGuard` or IRQ context also provides an implicit read-side token
  (`NoMigrate`), so lookup APIs accept `borrow impl ReadSide`.
- **Deferred reclamation**: `Quiesce.deferDrop(value: own Box<T, KernelHeap>)` moves
  ownership into a pending callback (an embedded `QuiesceHead` in `T`, or a
  separately allocated node). The moved value cannot be touched again (O2), so
  "never resubmitted while pending" holds by construction. Freeing after
  `synchronize_quiesce` is expressed as `Quiesce.synchronize()` (effect `sleep`)
  followed by an ordinary drop.
- Unlink-then-reclaim protocol [KA impl 13]: removal from an RCU-style table
  returns an `Unlinked<T>` token. The only ways to consume it are `deferDrop` or
  `synchronize` + drop. Dropping an `Unlinked<T>` any other way is a compile error
  (`@mustConsume`).
- **Blocker for modules**: `quiesce_read_unlock` inlines `preempt_enable`, which is
  not exported, so a module using explicit read sections would fail to load
  [KA impl 12, open 10]. A Nova module cannot use `Quiesce.read()` until CosmoOS
  exports a non-inline unlock (KQ-2). Built-in code is unaffected.

## 6. Locks, per-CPU data and context effects

```nova
static devices: SpinLock<DeviceTable> = SpinLock.new("nova.net.devices", ...);   // lockdep class name
{
    using g = devices.lock();          // SpinGuard<DeviceTable>: borrow mut DeviceTable
    g.insert(...)?;                    // `alloc` effect allowed (kmalloc never sleeps) — see KQ-4
}
```

**Implemented in C7c** (`integrations/cosmoos/c7/sync.nova`, `cosmoos/c7-report.md`) for `SpinLock`, `IrqSpinLock` and `Mutex`, each
made by `create(name, data)` in a `KernelHeap` box (no statics yet), with private fields (type-system §12). `PerCpu` is not.

| Nova | C | Effects / rules |
|---|---|---|
| `SpinLock<T>` → `SpinGuard<T>` | `spin_lock/unlock` | guard scope = atomic: forbids `sleep`. Not Send |
| `IrqSpinLock<T>` → `IrqSpinGuard<T>` | `spin_lock_irqsave/irqrestore` | stores `arch_irq_state_t`. Nested guards restore in reverse order, as `using` LIFO and L20 require |
| `Mutex<T>` → `MutexGuard<T>` | `mutex_lock/unlock` | `lock()` has the `sleep` effect, so it is illegal in atomic and IRQ contexts |
| `PerCpu<T>` | `struct percpu` fields / arch arrays | `get(borrow token: NoMigrate) -> borrow T` scoped to the token [KA impl 18] |

- Data lives *inside* the lock (Rust-style), so unsynchronised access to
  protected data is not expressible in safe code.
- Lockdep: each `static` lock declaration gets a stable class name (the declaration
  path, ≤ 63 chars, immortal string literal) [KA impl 17]. Nested-subclass locking
  exists only as `lockNested(subclass)` with a mandatory `/// Ordering:`
  justification. Nova performs **no** lock-order checking of its own in v0. Lockdep
  remains the authority.

### Context effects (C7)

**Implemented in C7a** as `type-system.md` §32 (ADR 0009), with three changes to the proposal below: effects are inferred
over the whole package, so annotations are required nowhere (only where a context or bound is wanted); the kernel lattice's
`process` is the absence of a context; and generated code calls no `might_sleep()` (its debug half is not exported to
modules; the primitives Nova calls have their own).

Effects: `sleep`, `alloc`, `irq` (callable from hard-IRQ context), `atomic`
(callable with preemption disabled). Lattice:
`process ⊒ atomic ⊒ irq`.

- `@context(irq)` on a function implies `@nosleep @noalloc` (policy [KA impl 19]).
  `@context(atomic)` implies `@nosleep`.
- **Checking:** effects are inferred bottom-up over the package call graph (SCCs
  conservatively joined). Annotations are **required** on `public` functions,
  `extern` imports and function-pointer types. Indirect calls use the pointer
  type's declared effects.
- **C imports** come from a reviewed binding file (`integrations/cosmoos/bindings/*.nova`)
  that declares each imported function's effects. An unannotated import
  defaults to `sleep + alloc` (most conservative).
- Generated code calls `might_sleep()` at the entry of every Nova function with the
  `sleep` effect (in `CONFIG_DEBUG` builds). That keeps the runtime check as the
  backstop [KA impl 15].
- Claim boundary: the effect checker proves only that *Nova code* respects the
  declared effects of what it calls. It does not prove kernel-wide properties such as
  deadlock freedom (prompt §3, "do not claim…").

## 7. Callback lifetimes and module unload

Facts [KA §6.5, impl 24–25]: unload calls `shutdown()`, waits one grace period,
then waits up to 5 s for kobjects whose release lives in the module (otherwise the
module becomes a "zombie"). Timers, IRQs, wait callbacks, `irq_poll` and threads are
**not** tracked. There is **no barrier for pending `call_quiesce`** callbacks.

Nova design:
- Every callback registration is an owned value whose drop performs synchronous
  teardown: `IrqRegistration` (free + `synchronize_irq`), `TimerRegistration`
  (`timer_cancel_sync`), `IrqPollRegistration`, `WaitCallback`, `KThread` (join).
  These drops have the `sleep` effect.
- A Nova module declares one `ModuleState` struct returned by `init()`. The
  generated `shutdown()` drops it, which tears down every registration it owns in
  reverse declaration order. Because registrations can only be owned (never
  leaked in safe code; `forget` is `unsafe` in the kernel profile), **no callback
  registration can outlive `shutdown()`**.
- Until CosmoOS has a `call_quiesce` barrier (KQ-3), the kernel profile **rejects**
  `Quiesce.deferDrop` in module builds when the callback would execute module text.
  Modules use `Quiesce.synchronize()` + drop instead.

## 8. Questions for the CosmoOS owner (feed into `docs/open-decisions.md`)

| ID | Question | Source |
|---|---|---|
| KQ-1 | May IRQ handlers allocate? `interrupt.h` policy says no, while `kmalloc.h` and M23 say it is IRQ-safe. Nova proposes "no" (`@noalloc` in IRQ). | KA open 3 |
| KQ-2 | Export `preempt_enable` (or out-of-line `quiesce_read_lock/unlock`) for modules? | KA open 10 |
| KQ-3 | Will a `call_quiesce` barrier at unload be added? | KA open 11 |
| KQ-4 | Should `@nosleep` scopes permit `alloc` (legal today), given that kmalloc may gain `might_sleep`? Nova proposes: permitted, and the decision lives in the binding file for kmalloc. | KA open 16 |
| KQ-5 | May built-in Nova code call non-exported internals (pmm, vm_kernel_*)? | KA open 14 |
| KQ-6 | `kobject_put` from IRQ context for non-blocking releases? | KA open 15 |
| KQ-7 | Should `.ko` files be FP-register-checked like the kernel ELF? | KA open 12 |
| KQ-8 | Would the owner accept typed address wrappers in C headers, or should typing stay Nova-side only? (Nova default: Nova-side only.) | KA open 13 |
