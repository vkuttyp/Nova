# CosmoOS kernel audit for Nova (Profile B: kernel)

- Repository: `~/dev/CosmoOS` (read-only audit; nothing built or modified)
- Branch read: `b2-cosmofs-defects`, HEAD `30d34b29da1f913716cca57004a966954477aca3`
- `main`: `4527d16d2394de30b1d5b0241618bf08eb78f034` ("Merge pull request #432 ... notes-v1.0.9")
- The branch differs from main under `kernel/include`, `kernel/memory`, `kernel/core`, `kernel/module`,
  `kernel/object`, `kernel/scheduler`, `build`, `docs/kernel` only in `cosmofs.h`, `faultinject.{c,h}`,
  `selftest.{c,h}` (`git diff --stat main HEAD`), so every fact below holds for main as well.
- Brief read: `/Users/kutty/Nova/prompts/cosmoos-native.md` §3 (104-161), §4 (163-204), §5 (206-235), §6 (237-257), §8 (283-300).
- Governing prompt read: `prompts/CosmoOS Prompt #3 — Critical Fix Pass + Kernel Object Lifetime & Quiescence.md` §5-§20 (lines 424-889).
- All paths below are relative to `~/dev/CosmoOS`. "(inferred)" marks conclusions not stated in code or docs.

Conventions worth knowing (AGENTS.md): every subsystem has `docs/kernel/<sub>/{api,architecture,design,invariants,testing}.md`;
invariants have IDs (memory `M1..M51`, quiesce `Q1..Q20`, object `OB1..OB12`, module `M1..M26` -- note the
memory and module ID spaces collide, lockdep `L1..L21`, scheduler `S1..S34`). Instrumentation must live under
`CONFIG_DEBUG`/`CONFIG_SELFTEST`; "unarmed hot-path hooks must take no lock and perform no atomic RMW" (AGENTS.md
"Project conventions").

---

## 1. Memory allocators

### 1.1 General heap: `kmalloc` / `kzalloc` / `krealloc` / `kfree`

Header `kernel/include/kernel/kmalloc.h`, implementation `kernel/memory/kmalloc.c`.

| Function | Exact signature | Cite |
|---|---|---|
| kmalloc | `void *kmalloc(size_t size, unsigned flags);` | kmalloc.h:61 |
| kzalloc | `void *kzalloc(size_t size);` (= `kmalloc(size, KMEM_ZERO)`) | kmalloc.h:62, kmalloc.c:93-96 |
| krealloc | `void *krealloc(void *ptr, size_t new_size, unsigned flags);` | kmalloc.h:70 |
| kfree | `void kfree(void *ptr);` | kmalloc.h:71 |
| kmalloc_size | `size_t kmalloc_size(const void *ptr);` (usable size; 0 if not heap) | kmalloc.h:74, kmalloc.c:98-107 |

- **Flags.** Exactly one flag exists: `KMEM_ZERO (1u << 0)` (kmalloc.h:19). There is **no GFP-like context flag**
  (no GFP_ATOMIC/GFP_KERNEL distinction) because no allocator path sleeps (kmalloc.h:4-5; memory invariant M23,
  `docs/kernel/memory/invariants.md:178-181`).
- **Contexts.** "All functions are non-blocking and interrupt-safe (cache spinlock, irqsave)" (kmalloc.h:4-5).
  Every lock is taken with `spin_lock_irqsave` (slab.c:204, kmalloc.c:86). `docs/kernel/lockdep/locking-rules.md:68`:
  "`kmalloc` currently does not sleep. That does not justify invoking arbitrary allocation APIs under spinlocks."
  Lockdep L4 gap: "`kmalloc` does not sleep today and carries no annotation; if it ever can, it gets one"
  (`docs/kernel/lockdep/invariants.md:38-44`). See §11 for the contradiction with `interrupt.h`.
- **Alignment.** Results are at least 16-byte aligned: `KMALLOC_MIN_ALIGN 16u` (kmalloc.h:21; invariant M27).
  Large (page-path) results are page aligned (they are `page_to_virt` of a buddy block, kmalloc.c:90).
- **Size limits.** `KMALLOC_MAX_SLAB 8192u` (larger goes to whole pages) and `KMALLOC_MAX_SIZE 4 MiB` (= PMM order 10)
  (kmalloc.h:22-23). `size == 0 || size > KMALLOC_MAX_SIZE` returns NULL (kmalloc.c:72-73).
  Size classes: 16, 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 2048, 4096, 8192 (kmalloc.c:20-22).
  Large path: `pmm_alloc_pages(order, KMEM_ZERO ? PMM_FLAGS_ZERO : 0)` with `PG_KMALLOC_LARGE` set on the head page
  and order kept in `page->order` (kmalloc.c:80-90).
- **Failure semantics.** Allocation failure returns NULL (kmalloc.h:5). Debug builds can inject failure
  (`faultinject_should_fail(FI_KMALLOC)`, kmalloc.c:78, slab.c:202). `krealloc`: NULL ptr = kmalloc, `new_size 0`
  frees and returns NULL, on failure the original block is untouched and NULL is returned; a shrink that stays
  within `(old/2, old]` returns `ptr` unchanged (kmalloc.h:64-70, kmalloc.c:138-161). With `KMEM_ZERO`, only bytes
  beyond the *old usable* size are zeroed (kmalloc.h:64-67).
- **Panics instead of errors (misuse, every build).** `kfree` of a non-heap pointer, of a non-start of a large
  allocation, double free (kmalloc.c:115-116, 119-120, 133-134; slab.c:251-263); `krealloc` of non-heap memory
  (kmalloc.c:148-149); `kmalloc_init` failure (kmalloc.c:44-45); `kmalloc` before init is a `KASSERT`
  (kmalloc.c:71), and `KASSERT` is active in every build (`kernel/include/kernel/panic.h:12-14, 54-58`).
- `kfree` needs no size: the frame descriptor says slab vs. large (kmalloc.c:109-136; M26).
- Module ABI exports: `kmalloc`, `kzalloc`, `krealloc`, `kfree` (kmalloc.c:180-185).

### 1.2 Slab / kmem caches

Header kmalloc.h:25-56, implementation `kernel/memory/slab.c`, private `kernel/memory/slab.h`.

```c
struct kmem_cache *kmem_cache_create(const char *name, size_t object_size, size_t align); // kmalloc.h:47
void kmem_cache_destroy(struct kmem_cache *cache);                                          // kmalloc.h:50
void *kmem_cache_alloc(struct kmem_cache *cache, unsigned flags);                           // kmalloc.h:52
void  kmem_cache_free(struct kmem_cache *cache, void *obj);                                 // kmalloc.h:53
size_t kmem_cache_pages(const struct kmem_cache *cache);                                    // kmalloc.h:56
```

- **No constructors / destructors.** The API takes only name, size, align (kmalloc.h:47). Objects are uninitialised
  unless `KMEM_ZERO` (kmalloc.h:9; slab.c:235-236).
- `align` 0 means 16; otherwise must be a power of two >= 16, `object_size > 0` (slab.c:83-86). NULL if one object
  does not fit an order-3 (32 KiB) slab (`SLAB_MAX_ORDER 3u`, slab.c:23, 46-65; api.md:555-559).
- Name must be immortal (api.md:559). The spinlock of each cache is named after the cache (slab.c:78), so the
  cache name is also its lockdep class.
- Design: on-slab header + free bitmap (1 = free) so double frees are detected and freed memory is never written
  (slab.c:1-10, 260-263; M25). At most two empty slabs retained (`SLAB_KEEP_EMPTY 2u`, slab.c:24; M29).
- `kmem_cache_destroy` panics if objects are live (slab.c:180-182). `kmem_cache_free` panics on wrong cache /
  non-object-start / double free (slab.c:250-263).
- Lock order: `vm_space.lock -> kmem_cache.lock -> pmm_zone.lock` (M22, invariants.md:170-176).
- Exported: `kmem_cache_create/destroy/alloc/free` (slab.c:326-331).

### 1.3 Physical page (buddy) allocator

Header `kernel/include/kernel/pmm.h`, `kernel/include/kernel/page.h`; implementation `kernel/memory/pmm.c`,
`kernel/memory/buddy.c` (private `buddy.h`).

```c
struct page *pmm_alloc_pages(unsigned order, unsigned flags);   // pmm.h:72
void pmm_free_pages(struct page *page, unsigned order);         // pmm.h:73
struct page *pmm_alloc_page(unsigned flags);                     // pmm.h:75-78 (order 0)
void pmm_free_page(struct page *page);                           // pmm.h:80-83
void pmm_page_get(struct page *page);                            // pmm.h:87  shared-frame refcount
void pmm_page_put(struct page *page);                            // pmm.h:88  frees at zero with stored order
```

- Orders `0 .. PMM_MAX_ORDER-1` = 4 KiB .. 4 MiB, `PMM_MAX_ORDER 11u` (pmm.h:20-21); `order >= 11` returns NULL
  (pmm.c:284-285).
- Zones: `PMM_ZONE_DMA [0,16 MiB)`, `PMM_ZONE_DMA32 [16 MiB,4 GiB)`, `PMM_ZONE_NORMAL [4 GiB,..)` (pmm.h:23-31).
  Flags: `PMM_FLAGS_ZONE_NORMAL 0` (falls back downward), `PMM_FLAGS_ZONE_DMA32`, `PMM_FLAGS_ZONE_DMA`,
  `PMM_FLAGS_ZERO` (pmm.h:34-38). Fallback loop from the top zone downward, each zone under its irqsave spinlock
  (pmm.c:287-299).
- Contracts: non-blocking, interrupt-safe (zone spinlock, IRQs off); failure returns NULL; misuse (double free,
  wrong order, freeing reserved memory, nonzero refcount) panics; blocks naturally aligned to their order
  (pmm.h:4-9; pmm.c:313-327; M6).
- `PMM_FLAGS_ZERO` requires the block to be in the direct map (`KASSERT`, pmm.c:306; M12).
- Debug builds poison free frames and check on allocation; a write to a free frame panics "use after free of pfn"
  (pmm.c:240-263; `PG_POISONED`, page.h:26; M37).
- **`struct page`** (page.h:29-40), exactly 32 bytes (STATIC_ASSERT page.h:42; M1):
  `uint32_t flags; uint32_t refcount /* 0 when free or reserved; atomic once allocated */; uint8_t order; uint8_t zone;
  uint16_t reserved0; uint32_t reserved1; union { struct list_node buddy; struct slab *slab; };`
  Flags: `PG_RESERVED, PG_BUDDY, PG_SLAB, PG_KMALLOC_LARGE, PG_PAGETABLE, PG_DEFERRED, PG_POISONED, PG_PAGECACHE`
  (page.h:20-27). One descriptor per 4 KiB frame (page.h:4-6).
- Conversions (page.h:48-111): `pfn_valid`, `page_to_pfn`, `pfn_to_page`, `page_to_phys`, `phys_to_page` (NULL beyond
  RAM), `phys_to_virt`, `virt_to_phys`, `page_to_virt`, `virt_to_page`, `phys_in_direct_map`, `virt_is_direct_map`.
  Direct map = `pmm_hhdm_base + pa` (page.h:82-90).
- **Not exported to modules** (no `EXPORT_SYMBOL` in pmm.c; module api.md:103-120 lists no pmm symbol).

### 1.4 Virtual memory mapping APIs (kernel side)

Header `kernel/include/kernel/vmm.h`, implementation `kernel/memory/vmm.c` (3236 lines).

```c
#define VM_KALLOC_GUARD       (1u << 0)  // unmapped page below and above      vmm.h:428
#define VM_KALLOC_POPULATE    (1u << 1)  // map zeroed frames now              vmm.h:429
#define VM_KALLOC_NEAR_KERNEL (1u << 2)  // near arena, -mcmodel=kernel reach vmm.h:430
vaddr_t vm_kernel_alloc(size_t size, unsigned flags, vm_prot_t prot);          // vmm.h:434, 0 on failure
void    vm_kernel_free(vaddr_t base);                                          // vmm.h:438, panics if not live
int     vm_kernel_protect(vaddr_t base, vm_prot_t prot);                       // vmm.h:445
vaddr_t vm_map_phys(paddr_t pa, size_t size, vm_prot_t prot, vm_cache_t cache);// vmm.h:449, MMIO, 0 on failure
vaddr_t vm_map_direct_ranges(void *const *va, const size_t *size, unsigned n,
                             vm_prot_t prot, vm_cache_t cache);                // vmm.h:456 (<=16 pieces)
void    vm_unmap_phys(vaddr_t base);                                           // vmm.h:457
const struct vm_region *vm_find_region(struct vm_space *space, vaddr_t va);    // vmm.h:462
bool    vm_query(vaddr_t va, paddr_t *pa, vm_prot_t *prot, vm_cache_t *cache, ...); // vmm.h:465
```

- `vm_prot_t` is `unsigned` with `VM_PROT_{NONE,READ,WRITE,EXEC,RW,RX,USER}` (`kernel/include/arch/mmu.h:20-27`);
  `vm_cache_t` enum `VM_CACHE_WB / WT / UC` ("uncached: device MMIO") (arch/mmu.h:29-33).
- `vm_kernel_alloc` returns zeroed frames, `size` a non-zero page multiple, failure 0 with rollback; lock order
  `vm_space.lock -> kmem_cache.lock -> pmm_zone.lock` (`docs/kernel/memory/api.md:316-330`). In code it takes
  `kernel_space.lock` with irqsave and contains no `might_sleep` (vmm.c:287-314). Kernel thread stacks use it with
  `VM_KALLOC_GUARD | VM_KALLOC_POPULATE` (`kernel/scheduler/thread.c:260`).
- `vm_kernel_protect` runs a TLB shootdown, "needs interrupts enabled and may not be called with kernel_space.lock
  held", thread context only, refuses W+X (vmm.h:440-445; api.md:338-354). `vm_kernel_free` also shoots down
  (vmm.c:424, `arch_mmu_shootdown`), so it too needs IRQs enabled (inferred from the shootdown).
- W^X for every kernel allocation (M31), guard pages unmapped (M14), the arena is the only place for dynamic kernel
  mappings (M16), mapping over an existing mapping refused (M17).
- Module-visible subset: only `vm_map_direct_ranges` and `vm_unmap_phys` are exported (vmm.c:3235-3236; module
  api.md:107). Drivers map MMIO through `pci_map_bar` (`drivers/include/drivers/pci.h:127`) or
  `device_map_mmio(struct device *, const struct resource *)` / `device_unmap_mmio(vaddr_t)`
  (`kernel/include/kernel/device.h:155-156`), both returning a plain `vaddr_t`.

### 1.5 DMA-capable memory

Header `kernel/include/kernel/dma.h`, implementation `kernel/device/dma.c`.

```c
typedef uint64_t dma_addr_t;                                                   // dma.h:20
enum dma_dir { DMA_TO_DEVICE, DMA_FROM_DEVICE, DMA_BIDIRECTIONAL };            // dma.h:22-26
#define DMA_ZERO (1u << 0)                                                     // dma.h:28
void *dma_alloc(struct device *dev, size_t size, dma_addr_t *dma_out, unsigned flags); // dma.h:34
void  dma_free(struct device *dev, size_t size, void *va, dma_addr_t dma);     // dma.h:35
dma_addr_t dma_map(struct device *dev, const void *va, size_t len, enum dma_dir dir);   // dma.h:41
void  dma_unmap(struct device *dev, dma_addr_t dma, size_t len, enum dma_dir dir);      // dma.h:42
bool  dma_mappable(struct device *dev, const void *va, size_t len);            // dma.h:45
void  dma_sync_for_device(struct device *, dma_addr_t, size_t, enum dma_dir);  // dma.h:48
void  dma_sync_for_cpu(struct device *, dma_addr_t, size_t, enum dma_dir);     // dma.h:49
int   dma_set_mask(struct device *dev, unsigned bits);                         // dma.h:52 (24..64, else -EINVAL)
```

- `dma_alloc`: coherent, page-granular, physically contiguous, inside `dev->dma_mask` (NULL dev: 32-bit); returns
  KVA + bus address; "Thread context (the PMM does not sleep, but the allocation is not for interrupt handlers)";
  NULL on failure (dma.h:30-34). Backed by `pmm_alloc_pages` and, behind an IOMMU, `iommu_dma_map` (dma.c:50, 60).
- `dma_map`: only direct-map addresses (kmalloc, dma_alloc, page frames), physically contiguous, within mask;
  anything else (kernel arena, stack, user) returns **0** as the failure value; any context (dma.h:37-41).
- `dma_free` must be given the original size, VA and bus address (dma.h:35). If the IOMMU fails to revoke, the
  frames are deliberately leaked (dma.c:83-95); `dma_unmap` **panics** if the IOMMU does not revoke (dma.c:142-149).
- Coherency: "Both targets are cache coherent for DMA on the supported machines" -- `dma_sync_for_device` is just
  `arch_dma_barrier()`, `dma_sync_for_cpu` a `seq_cst` fence (dma.c:157-177). Bus address == physical address
  without an IOMMU, IOVA with one; "Callers must not rely on either" (dma.h:8-10).
- All exported to modules (module api.md:113). No streaming scatter-gather API, no non-coherent cache maintenance.

### 1.6 Per-CPU storage

`kernel/include/kernel/percpu.h`, `kernel/include/arch/percpu.h`, `kernel/core/percpu.c`.

- There is **no generic per-CPU variable facility** (no DEFINE_PER_CPU / percpu allocator; grep of
  `kernel/include` finds none). Instead: one fixed `struct percpu` per CPU (percpu.h:37-73) reached via the arch
  fast pointer (GS base on x86-64, TPIDR_EL1 on AArch64; percpu.h:4-6, 38). "Architecture-specific per-CPU data
  lives in the arch layer's own arrays indexed by cpu_id" (percpu.h:11-12). Subsystems that need per-CPU data use
  static arrays indexed by CPU id, e.g. quiesce's `struct quiesce_cpu cpus[QUIESCE_MAX_CPUS]` cache-line aligned
  (`docs/kernel/quiesce/design.md:6-15`).
- `CONFIG_MAX_CPUS 64u`, `cpumask_t` is `uint64_t` (percpu.h:23-29).
- Fields relevant to context: `int preempt_count` (>0: preemption disabled), `unsigned irq_depth` (>0: in an
  interrupt handler), `bool need_resched` (percpu.h:44-47). Offsets 0/8/16 are ABI for assembly (percpu.h:75-78).
- **Access rule S25** (percpu.h:86-111; `docs/kernel/scheduler/invariants.md:69`): "A per-CPU answer may be kept
  only while the thread cannot move: preemption disabled, interrupts off, interrupt context, or an affinity of one
  CPU. `preempt_disable()` is the migration barrier." Checked forms `this_cpu()` / `arch_cpu_id()` panic in debug
  builds when the rule does not hold (percpu.h:95-97, 122-138; percpu.c:115-133); `raw_this_cpu()` / `raw_cpu_id()`
  are unchecked and every use must say why in a comment (percpu.h:97-106). `PERCPU_WARN=1` turns the panic into a
  once-per-site warning (build/config.mk:108-112).
- Keeping a per-CPU answer across a sleep requires a one-CPU-affinity thread (`thread_create_on`, percpu.h:108-110;
  `kernel/include/kernel/thread.h:178`).

---

## 2. Address types and MMIO

- **All address kinds are plain integer typedefs**, not distinct types:
  `typedef uint64_t paddr_t; typedef uintptr_t vaddr_t; typedef uint64_t pfn_t;` (`kernel/include/kernel/types.h:16-18`),
  `typedef uint64_t dma_addr_t;` (dma.h:20). The header claims "Physical and virtual addresses are different things
  and the type system says so. Conversions go through the named functions in page.h, never through casts in
  callers" (types.h:4-6) -- in C these typedefs are freely interconvertible; the separation is by convention and
  review only (see §11).
- Kernel pointers are `void *` for direct-map memory (kmalloc, `phys_to_virt`), but mapping APIs return `vaddr_t`
  integers (vm_kernel_alloc, vm_map_phys, pci_map_bar, device_map_mmio). `dma_map` uses 0 as the error value
  (dma.h:40).
- Capability handles are `int` indices into a per-process `struct handle_table` (handle.h:86-104); rights are
  `unsigned` bitmasks. No generation counter (`docs/kernel/object/design.md:214-216`).
- **No shared MMIO accessor layer** (no readl/writel, ioread32 or `mmio_*` in `kernel/include` or `drivers/include`).
  Each driver casts: e.g. NVMe `rd32/wr32` as `*(volatile uint32_t *)(c->bar + off)` (`drivers/nvme/nvme.c:136-137`),
  doorbells stored as `volatile uint32_t *` (nvme.c:83, 200-201). x86 port I/O lives in
  `kernel/arch/x86_64/include/x86/io.h`; PCI I/O-BAR access is `pci_io_read/pci_io_write`, bounds-checked
  (`drivers/include/drivers/pci.h:128-131`).
- MMIO mapping attributes: `VM_CACHE_UC` -> AArch64 `MAIR_IDX_DEVICE`; `VM_CACHE_WT` -> AArch64 **Normal
  non-cacheable** (not write-through) (`kernel/arch/aarch64/mmu.c:108-111`).
- Barriers: `barrier()` = compiler-only (`kernel/include/kernel/compiler.h:37-39`); `arch_dma_barrier()` "Order this
  CPU's memory writes before a device observes them (a DMA descriptor before its doorbell)" = `sfence` on x86-64,
  `dsb sy` on AArch64 (`kernel/include/arch/cpu.h:46-48`; `kernel/arch/x86_64/cpu.c:166-169`;
  `kernel/arch/aarch64/cpu.c:153-156`). Otherwise code uses `__atomic_*` builtins and `__atomic_thread_fence`
  directly (e.g. object.c:76, quiesce_core.h:102-107). There is no `atomic_t` wrapper header.

---

## 3. Kernel object lifetime

### 3.1 kobject refcounting (`kernel/include/kernel/object.h`, `kernel/object/object.c`)

```c
struct kobject_type { const char *name; void (*release)(struct kobject *obj); unsigned flags; }; // object.h:28-32
struct kobject { const struct kobject_type *type; uint32_t refcount; uint32_t handles; uint8_t watched;
                 struct module *owner; struct epoll_item *watchers; };                          // object.h:39-63
void kobject_init(struct kobject *obj, const struct kobject_type *type);   // object.h:67  ref 1 to caller
void kobject_get(struct kobject *obj);                                      // object.h:68  panics from 0
void kobject_get_n(struct kobject *obj, uint32_t n);                        // object.h:73
bool kobject_tryget(struct kobject *obj);                                   // object.h:77  false at 0 (CAS)
void kobject_put(struct kobject *obj);                                      // object.h:78
bool kobject_put_and_lock(struct kobject *obj, spinlock_t *lock, arch_irq_state_t *state); // object.h:93
void kobject_release_final(struct kobject *obj);                            // object.h:96
uint32_t kobject_refcount(const struct kobject *obj);                       // object.h:97
void kobject_track_code(struct kobject *obj, uintptr_t code);               // object.h:102
```

- Embed and recover with `container_of` (object.h:4-5). get/put are atomic; "the type's release runs from the last
  put, outside any spinlock the caller holds (callers must not hold one), and may block" (object.h:5-7).
  `kobject_put` = `__atomic_fetch_sub(..., ACQ_REL)`, release on old==1 (object.c:74-81). **No runtime context check**
  on the final put (object.c:61-81): the "no spinlock held" rule is unchecked (inferred from code).
- Rules (object.h:9-17): every type has a release; the release frees the memory and nothing else does; creator holds
  the first reference; a registry holds its own and drops it on unregister; "a lookup returns a referenced pointer,
  never a borrowed one"; the owner module of the release code is recorded.
- `kobject_init` asserts a non-NULL release (object.c:14) and sets `owner = module_owner_of(type->release)`
  (object.c:23). `kobject_release_final` reads `owner` first, calls release, then `module_object_released(owner)`
  so module text stays mapped while release runs (object.c:61-72; Q16).
- `kobject_put_and_lock`: drops without the lock unless the count is 1; the last drop happens under `lock` and returns
  true **with the lock held**; caller unlinks, unlocks, then calls `kobject_release_final` (object.h:79-96,
  object.c:83-104). This is the table-removal pattern (OB3).
- Weak references: none as a type. The weak-upgrade idiom is `kobject_tryget` on a table entry whose release path
  clears it under a lock the looker holds (object.h:74-77; OB3 lists `process_lookup`, TCP `sock_ref`, `udp_input`,
  `unix.c`, `pagecache.c`). Handles are the other "weak-ish" name: an int that may later name a different object.
- `kobject_io_type` (object.h:113-162) is the vtable for I/O objects (read/write/stat/ready/set_nonblock/flush/
  poll_wq/read_undo/read_filled); `ready` "Any thread context; never blocks" (object.h:118-121).
- Object invariants `docs/kernel/object/invariants.md`: **OB1** count never rises from zero or goes below it (all
  panics, every build, :26-33); **OB2** release runs exactly once, from the drop that reached zero, on the dropping
  thread (:35-42); **OB3** a table-findable object reaches zero under the table lock or behind `kobject_tryget`
  (:44-56); **OB4** `handles` equals slot count; **OB6-OB8** handle-table count/exiting/slot-empty-before-close;
  **OB9** per-type rights tested only after type check; **OB12** console object never released (:58-134).
- Exported: `kobject_init/get/tryget/put/put_and_lock/release_final/refcount` (object.c:113-119). `kobject_get_n`
  and `kobject_track_code` are not exported (inferred from the grep of EXPORT_SYMBOL).

### 3.2 Capability handles (`kernel/include/kernel/handle.h`)

- Per-process `struct handle_table { spinlock_t lock; struct handle_entry entries[HANDLE_TABLE_SIZE]; unsigned count;
  unsigned limit; ... bool exiting; }`, `HANDLE_TABLE_SIZE 64` (handle.h:17, 86-95); entry = `{kobject *obj; unsigned
  rights; unsigned flags}` (handle.h:76-80).
- "A handle is a capability ... Rights only ever shrink" (handle.h:19-23). Generic rights `READ, WRITE, DUP,
  TRANSFER, MANAGE` in bits 0..15, per-type bits 16..31 (handle.h:25-32, 44-75).
- API: `handle_install[_flags|_at|_at_flags]`, `handle_lookup(t, h, rights_needed)`, `handle_lookup_rights`,
  `handle_get`, `handle_get_flags`, `handle_close`, `handle_transfer_check`, `handle_table_destroy`,
  `handle_close_on_exec` (handle.h:97-142). "A lookup returns a referenced object; the caller owns that reference
  until kobject_put. All functions are non-blocking" (handle.h:5-7).
- **Q17**: the table hands out referenced objects under its lock and drops references outside it
  (`docs/kernel/quiesce/invariants.md:181-185`). Handle numbers are reused lowest-first with **no generation**
  (`docs/kernel/object/design.md:214-216`).

### 3.3 Epoch / quiescence (`kernel/include/kernel/quiesce.h`, `kernel/core/quiesce.c`, `quiesce_core.h`)

```c
static inline void quiesce_read_lock(void);    // preempt_disable() + debug depth++      quiesce.h:45-49
static inline void quiesce_read_unlock(void);  // debug depth-- + preempt_enable()        quiesce.h:50-54
void synchronize_quiesce(void);                // sleeps; never with a spinlock held      quiesce.h:77
struct quiesce_head { struct quiesce_head *next; void (*fn)(struct quiesce_head *h); bool pending; }; // :31-35
void call_quiesce(struct quiesce_head *h, void (*fn)(struct quiesce_head *h)); // any context  quiesce.h:84
void synchronize_irq(unsigned vector);                                   // interrupt.h:60
int  interrupt_unregister_sync(unsigned vector, interrupt_handler_fn fn);// interrupt.h:65
bool timer_cancel_sync(struct timer *t);                                 // timer.h:271
```

- Key invariant: "an object cannot be reclaimed until every CPU that could still have a reference to it has passed
  through a provably safe quiescent state" (quiesce.h:5-7; Prompt #3 :460-462).
- **Read side** = any preemption-disabled region: every spinlock, every interrupt handler, and explicit
  `quiesce_read_lock` sections; "Must not block"; nestable (quiesce.h:9-11, 43-44; design.md:29-39).
  `sched_block_current`, `waitqueue_prepare`, `mutex_lock`, `semaphore_down`, `wait_for_completion` already panic
  when `preempt_count != 0` (design.md:37-39). Inside a read section: no faulting user copies, no blocking
  allocation (locking-rules.md:15); `kmalloc` and `call_quiesce` are permitted (non-sleeping); `synchronize_quiesce`
  is forbidden (Q4).
- **Quiescent points (exactly five)** (design.md:41-69; Q2): (1) interrupt return to a context with
  `irq_depth == 0 && preempt_count == 0 &&` IRQs enabled in the interrupted frame (x86 `kernel/arch/x86_64/trap.c:102`);
  (2) `schedule()`; (3) the idle loop before hlt/wfi; (4) `synchronize_quiesce` itself; (5) a CPU coming online.
  The x86 paranoid path (NMI/#MC) never records.
- **Grace period**: `synchronize_quiesce` advances a global epoch (seq_cst RMW) and waits for every online CPU's
  published `seen_epoch`; blocks on a wait queue with a `TICK_NS/2` deadline and is woken on publish (Q18,
  invariants.md:187-190); stragglers are kicked by IPI after two ticks (Q6). Memory ordering is specified and
  litmus/TSan-checked (Q3, invariants.md:28-41). Cost: one to two ticks (design.md:334-341). Cannot fail
  (design.md:330-332).
- **Deferred free**: `call_quiesce` pushes onto a global irqsave-locked list; a single `quiesce` kernel thread
  (priority `SCHED_PRIO_DEFAULT - 4`) takes the batch, runs one `synchronize_quiesce`, then runs callbacks in thread
  context, in submission order; callbacks may free memory and take mutexes; the head is caller-owned until the
  callback runs, `h` must be zero-initialised or previously run, resubmitting a pending head panics; the worker never
  allocates (quiesce.h:79-84; design.md:26-27, 192-206, 307-311; Q5).
- **Reclaim protocol**: unlink -> `synchronize_quiesce()` or `call_quiesce()` -> free (quiesce.h:15-18; Q1).
  Kobject rule: "Memory reachable from an interrupt handler, a timer callback or another CPU without a lock is freed
  only from a kobject release or a `call_quiesce` callback, never directly by the code that unlinked it"
  (design.md:239-243). Every quiescence user must document what is protected, who reads/unlinks/synchronises/frees,
  and why a refcount alone is not enough (quiesce.h:20-22; Prompt #3 §20 :880-887).
- **IRQ handler lifetime** (Q7): `interrupt_unregister` only unpublishes; `arg` may be freed only after
  `synchronize_irq` or `interrupt_unregister_sync` (interrupt.h:24-27, 47-66). Thread context, no spinlock held.
- **Timer lifetime** (Q8): timer memory freed only after `timer_cancel_sync`, which spins (does not sleep), defeats
  re-arming callbacks, panics if called from the timer's own callback, and must not be called holding a lock the
  callback takes (timer.h:255-271; design.md:219-237; L15, L19).
- **Wait-queue callback lifetime**: callback entries run under the queue's spinlock in the waker's context
  (possibly IRQ), must not sleep or touch their own queue; `waitqueue_remove_callback` guarantees no wake is running
  it on return; a dying queue calls `waitqueue_detach_callbacks`, delivering `WAIT_CB_FREED` (POLLFREE analogue)
  (`kernel/include/kernel/wait.h:25-83`).
- **irq_poll lifetime**: `irq_poll_disable` is "the deferred half of synchronize_irq": call after the interrupt is
  released and before freeing what `poll` reads (`kernel/include/kernel/irqpoll.h:33-36`).
- **Completion lifetime**: after `wait_for_completion` returns, `complete()` has finished touching it, so it may be
  freed; polling `completion_done()` then freeing races (`kernel/include/kernel/completion.h:4-12`; S30).
- Quiescence invariants list (`docs/kernel/quiesce/invariants.md`): Q1 no free while a read-side reference may exist;
  Q2 five quiescent points; Q3 documented barriers; Q4 no synchronize from IRQ/spinlock; Q5 call_quiesce ordering;
  Q6 straggler kick; Q7 IRQ arg; Q8 timer; Q9 every kobject type has a release; Q9a refused registration leaves no
  kobject; Q10 registry vs creator reference; Q11/Q11b block unregister and IRQ-before-teardown ordering; Q12 netif
  unregister (`ops->transmit` must not sleep); Q13/Q14 TCP/socket; Q15 module free conditions; Q16 kobject owner;
  Q17 handle table; Q18 waiter wake; Q19 kick attribution; Q20 publish names its CPU.
- Exported: `synchronize_quiesce`, `call_quiesce`, `quiesce_read_lock_debug`, `quiesce_read_unlock_debug`
  (`kernel/core/quiesce.c:405-408`), `synchronize_irq`, `interrupt_unregister_sync`, timer and completion APIs.
  **`preempt_enable` is not exported** (defined `kernel/core/percpu.c:151`, no EXPORT_SYMBOL), yet the inline
  `quiesce_read_unlock` calls it -- see §11.

### 3.4 Module unload lifetime: see §6.5.

---

## 4. Synchronization

Canonical table: `docs/kernel/lockdep/locking-rules.md:8-20`. Summary with code cites:

### 4.1 Spinlocks (`kernel/include/kernel/spinlock.h`, `kernel/core/spinlock.c`)

```c
typedef struct spinlock { uint32_t locked; uint32_t owner_cpu; const char *name; uint16_t class; uint16_t pad[3]; } spinlock_t; // :31-37
#define SPINLOCK_INIT(lockname) {...}                                    // :39
void spinlock_init(spinlock_t *lock, const char *name);                  // :41
void spin_lock(spinlock_t *lock);  void spin_unlock(spinlock_t *lock);   // :45-46  never-from-IRQ locks only
void spin_lock_nested(spinlock_t *lock, unsigned subclass);              // :51 subclass 1..4
arch_irq_state_t spin_lock_irqsave(spinlock_t *lock);                    // :70
void spin_unlock_irqrestore(spinlock_t *lock, arch_irq_state_t state);   // :71
arch_irq_state_t spin_lock_irqsave_nested(spinlock_t *lock, unsigned subclass); // :57
bool spin_trylock(spinlock_t *lock);                                     // :60
bool spin_trylock_panic(spinlock_t *); void spin_unlock_panic(spinlock_t *); // :66-67 panic path only
bool spin_is_held(const spinlock_t *lock);                               // :74
```

- Rules (spinlock.h:4-13): a lock shared with interrupt context must use irqsave on every path; never sleep, block-
  allocate, or take a sleeping lock while holding one; not recursive -- re-acquire on the same CPU panics
  (spinlock.c:90). Holding a spinlock disables preemption (S7; spinlock.c:44, 57, 124).
- `arch_irq_state_t` is `unsigned long` (`kernel/include/arch/irq.h:15`). Plain `spin_lock` preserves IRQ state.
- No rwlock exists (locking-rules.md:22-23). No seqlock found (inferred from header list).
- Note: `struct spinlock` layout is part of module ABI v3+ (class field), and "Public lock layouts do not depend on
  LOCKDEP" (locking-rules.md:56).

### 4.2 Mutexes, semaphores, wait queues, completions

- `struct mutex` (spinlock + owner + waitqueue + name + class + PI fields + handoff) (`kernel/include/kernel/mutex.h:20-35`);
  `mutex_init(m, name)`, `mutex_lock`, `mutex_lock_nested(m, subclass)`, `mutex_trylock`, `mutex_unlock`,
  `mutex_is_locked` (mutex.h:37-44). Owner-tracked, not recursive, priority inheritance; "Must not be used from
  interrupt context or with preemption disabled (asserted). Unlock by a non-owner panics" (mutex.h:4-8);
  `might_sleep()` at `kernel/scheduler/mutex.c:137`; "may sleep even if presently uncontended" (locking-rules.md:12).
  Waiter overtaken at most once (S33).
- Semaphore: `semaphore_init(s, count, name)`, `semaphore_down` (blocks; panics in IRQ, scheduler/semaphore.c:30-32),
  `semaphore_trydown`, `semaphore_up` (interrupt-safe) (`kernel/include/kernel/semaphore.h:4-24`).
- Wait queues (`kernel/include/kernel/wait.h`): `struct waitqueue { spinlock_t lock; list waiters; list callbacks; }`
  (:52-56); `wait_event(wq, cond)`, `wait_event_killable` (-EINTR), `wait_event_timeout(wq, cond, ns)` macros
  (:117-188); Mesa semantics, lost-wakeup free; "Not usable from interrupt context or with preemption disabled
  (asserted). Wakers are interrupt-safe" (:4-11); `waitqueue_prepare` panics in IRQ (scheduler/wait.c:25-28).
- Completions: `completion_init`, `complete` (IRQ-safe), `wait_for_completion`, `wait_for_completion_timeout`,
  `completion_done` (`kernel/include/kernel/completion.h:4-40`).
- Thread sleep: `thread_sleep_ns` (exported, scheduler/wait.c:267). Kernel threads:
  `struct thread *thread_create(void (*entry)(void *), void *arg, const char *name, int priority)` (thread.h:163),
  `thread_create_on` with affinity (thread.h:178), `thread_exit(int) __noreturn` (thread.h:182).

### 4.3 Preemption and interrupt masking

- `preempt_disable()` inline: `raw_this_cpu()->preempt_count++` + compiler barrier (percpu.h:151-155);
  `preempt_enable()` asserts count>0, decrements, reschedules if count 0, `need_resched`, `irq_depth == 0`, IRQs
  enabled (percpu.c:151-159). Nestable. `preemptible()` = `preempt_count == 0 && irq_depth == 0` (percpu.h:181-187).
- Preemption happens at only four points (S8, scheduler/invariants.md:153-168): IRQ-return tail, `preempt_enable`
  reaching zero, `arch_irq_restore` enabling IRQs (`preempt_point`), explicit schedule/yield/block.
- Interrupt masking: `arch_irq_save()/arch_irq_restore(state)` nest; `arch_irq_enable/disable` are for boot code
  only; with LOCKDEP the save/restore pairing is checked (innermost-save rule, L20) (arch/irq.h:4-47).
  "IRQ masking alone does not make blocking safe ... Does not by itself increment preempt_count"
  (locking-rules.md:19).

### 4.4 Lock ordering rules

- Canonical: `docs/kernel/lockdep/locking-rules.md`; detailed orders are in subsystem invariants: scheduler S24/S25,
  VFS V7/V32, networking N-L1..N-L4/N22, quiescence Q1-Q19, module M18/M22-M24 (locking-rules.md:4-6).
- Memory: `vm_space.lock -> kmem_cache.lock -> pmm_zone.lock` (memory M22).
- Runqueues: increasing CPU-id order, separate `runqueueN` classes (S24; locking-rules.md:42-43). Vnode
  parent/second-parent/child subclasses 0/1/2; futex buckets by address with subclass 1 (locking-rules.md:39-45; L5,
  L7: `g_mounts_lock -> rename_lock -> vnode->lock`).
- Module loader mutex `g_modules_lock`/`g_lock` nests inside nothing; module `init`/`shutdown` must not re-enter the
  loader (M18; module design.md:291-295; locking-rules.md:65-66).
- `synchronize_quiesce` may be called with mutexes held but never a spinlock (quiesce design.md:317-322).
- Callbacks inherit their caller's context and held locks; IRQ and timer callbacks must not sleep
  (locking-rules.md:60-66).
- Unregister order: remove new access first, then drain readers/callbacks, then drop the final reference; "Never
  replace this lifetime protocol with a lockdep suppression" (locking-rules.md:74-78).

### 4.5 Lockdep (`kernel/include/kernel/lockdep.h`, `kernel/core/lockdep.c`, `docs/kernel/lockdep/`)

- Enabled by `LOCKDEP=1` (default in `BUILD=debug`, `0` in release) -> `-DCONFIG_LOCKDEP=...`
  (build/config.mk:14-23; build/toolchain.mk:42; lockdep.h:30-32). Use separate `OUT` trees when toggling
  (locking-rules.md:82-83).
- What it checks (lockdep.h:5-16; invariants L1-L4, L14, L15, L19-L21): lock-order inversion over a "taken while held"
  graph (spinlocks and mutexes); same-class re-acquisition without a nesting annotation; IRQ-safety (a class used in
  IRQ context also held with IRQs enabled, L3); `might_sleep` with a spinlock held or in IRQ; release of an unheld
  lock; thread exiting with a mutex held; held-stack overflow; raw IRQ save/restore pairing; callback classes
  (timer callbacks, irq_poll, wait callbacks: a synchronous wait for a callback is recorded as acquiring the
  callback's pseudo-class); completion classes (wait vs signal cycles through mutexes). Report kinds enum
  `LOCKDEP_R_{INVERSION,RECURSION,IRQ,SLEEP,OVERFLOW,UNHELD,EXIT_HELD,IRQ_STATE,CALLBACK,COMPLETION}` (lockdep.h:40-52).
- **How classes are declared**: implicitly, by the lock's name string at initialisation -- "the name is also the
  lock's class ... one initialisation site, one class" (spinlock.h:15-19). Classes keyed by name contents + kind
  (L10); names NUL-terminated, <= 63 chars, valid and unchanged for the lock's lifetime; validator copies them so
  history survives module unload (locking-rules.md:49-56). Nesting: `*_lock_nested(lock, subclass)`, with every use
  listed in L5. Held stacks per CPU for spinlocks, per thread for mutexes (L11).
- Limits stated: "an order the boot tests never exercise is never recorded" (L1 gap); "Lockdep does not yet model all
  callback waits or all transitive IRQ conflicts; a silent graph is not proof" (locking-rules.md:76-78); NMI/#MC
  outside the model (L3).
- Assertions: `lockdep_assert_held(lock, kind)` / `lockdep_assert_not_held` (lockdep.h:182-183; no-ops without
  LOCKDEP, :228-229). Not used in any `.c` file (grep count 0).

### 4.6 `might_sleep`

- Macro in lockdep.h:233-249: if `preempt_count != 0 || irq_depth != 0` -> lockdep report (LOCKDEP=1) or `panic`
  (LOCKDEP=0). **Always on, release builds included** (lockdep.h:18-19, 234-237; L4).
- Placed in: mutex_lock, semaphore_down, wait queue prepare, completion wait, `synchronize_quiesce`, `irqpoll`,
  `copy_from_user` / `copy_to_user` / `strncpy_from_user` (`kernel/syscall/uaccess.c:45, 55, 65`), VM file faults
  and `vm_space_fork` (vmm.c:784, 1642). Files containing `might_sleep()`: core/quiesce.c, core/irqpoll.c,
  memory/vmm.c, scheduler/{semaphore,completion,mutex,wait}.c, syscall/uaccess.c.
- Not in: kmalloc/kmem_cache_alloc/pmm/dma_alloc/vm_kernel_alloc (they do not sleep).

### 4.7 Interrupt-context rules

- Handler type: `typedef void (*interrupt_handler_fn)(unsigned vector, struct arch_trap_frame *frame, void *arg);`
  registration `interrupt_register(vector, fn, arg, name)` -> 0/-EINVAL/-EBUSY (interrupt.h:36-45).
- "Handlers run in interrupt context on the interrupted stack with interrupts disabled. They must not sleep,
  allocate, or take sleeping locks" (interrupt.h:10-13). Handlers are read-side sections (interrupt.h:17-19).
  `irq_depth` is incremented around dispatch (x86 trap.c:80-88).
- Allowed in IRQ: spinlocks (irqsave-consistent), `complete`, `semaphore_up`, wait-queue wakes, `call_quiesce`,
  `kobject_get/put` (but see release-may-block caveat), `timer_start/cancel`, `irq_poll_sched`, panic/KASSERT/WARN
  (panic.h:19). Not allowed: any `might_sleep` primitive, user copies, `synchronize_*`, `dma_alloc` (dma.h:32-33),
  `vm_kernel_protect` (thread context only).
- Timer callbacks run in interrupt context on the arming CPU (timer.h:4-6).
- Deferred work: there is **no general workqueue/softirq/tasklet** (no such API in `kernel/include`; interrupt.h:12-13
  still says "the deferred-work mechanism arrives with the scheduler in Phase 3"). The mechanisms that exist:
  `irq_poll` (budgeted completion consumption with per-CPU `irqpoll/N` highest-priority workers;
  `IRQ_POLL_BUDGET 32`, `IRQ_POLL_HOLD_NS 10 ms`; irqpoll.h:1-66, 91-110), `call_quiesce` worker, dedicated kernel
  threads, and subsystem workers (netif).

---

## 5. Execution-context model and static analysis

- **Formal model is runtime state, not types**: per-CPU `preempt_count` and `irq_depth` (percpu.h:44, 47) define
  atomic vs sleepable; `preemptible()` is the predicate (percpu.h:181-187). Sleepable = thread context with
  `preempt_count == 0 && irq_depth == 0` (and IRQs enabled for preemption points). IRQ masking is a separate axis
  that "does not by itself increment preempt_count" (locking-rules.md:19).
- **Checks are runtime**: `might_sleep()` (always-on), `schedule()` panics on nonzero counts (S6), mutex/semaphore/
  completion/wait entry checks (S6), `synchronize_quiesce` asserts (Q4), `KASSERT` everywhere (always-on), debug S25
  per-CPU claim check, lockdep (debug), stack painting high-water check (S32).
- **Static checks that exist** (all build-time, none semantic about context):
  - `-Werror` with `-Wall -Wextra -Wshadow -Wvla -Wundef -Wpointer-arith -Wwrite-strings -Wstrict-prototypes
    -Wmissing-prototypes -Wmissing-declarations -Wimplicit-fallthrough -Wcast-qual -Wformat=2 -Wdate-time`
    (build/toolchain.mk:27-31).
  - `-Wframe-larger-than=2048` as an error for kernel and modules, exemptions via
    `FRAME_EXEMPT_BEGIN/END` + `scripts/frame-baseline.txt` checked at link (toolchain.mk:64-71;
    compiler.h:65-78; `scripts/check-frame-baseline.py`). The baseline file is currently empty (0 non-comment lines).
  - Clang static analyzer gate: `make analyze` runs `clang --analyze` over every kernel, loader, module and pkg TU,
    compares plist diagnostics to a per-arch checked-in baseline `tools/analysis/<arch>.json`, fails on any
    unreviewed diagnostic; no checker disabled (Makefile:518-527; build/rules.mk:21-24;
    `docs/audit/2026-10-09-analyzer-gate-report.md:30-47`).
  - `scripts/check-fpregs.sh`: disassembles the kernel ELF and fails on any FP/SIMD register outside an allowlist
    (Makefile:525-527; script lines 1-30).
  - `scripts/check-kernel-elf.sh` (kernel link; also checks `page_local_*` functions don't span a page,
    compiler.h:54-63) and `scripts/check-module-elf.py` (module ELF64/ET_REL/machine/W^X/240-byte metadata,
    build/module.mk:76).
  - `make litmus` (RC11 litmus tests for quiesce/epoll), host tests under ASan/UBSan/TSan (tests/host/host.mk:10,19,294).
- **Annotations**: `__must_check` is defined (compiler.h:23) but **used nowhere** in kernel or drivers. Others:
  `__noreturn`, `__printf`, `__packed`, `__aligned`, `__section`, `__force_inline`, `__noinline`,
  `__page_local(n)` (compiler.h:16-63). No sparse-like address-space attributes (`__iomem`, `__user`, `__percpu`),
  no context annotations, no `__acquires/__releases`. User pointers are `uint64_t` (e.g. `copy_from_user(void *dst,
  uint64_t user_src, size_t len)`, uaccess.c:43).

---

## 6. Kernel module system

### 6.1 Format

- **ELF64 little-endian `ET_REL`**, native `e_machine` (x86-64 or AArch64), produced by compiling with the kernel
  flags plus `-DCOSMO_MODULE_BUILD=1` and merging with `ld.lld -r --no-dynamic-linker -z noexecstack
  --build-id=none` (build/module.mk:1-15, 74; `kernel/module/modelf.c:80-89`).
- Validation (modelf.c:80-209): ELF magic/class/endianness/version; `ET_REL`; native machine; header sizes;
  `e_shoff` 8-byte aligned; at most `MODELF_MAX_SECTIONS` alloc sections; only PROGBITS/NOBITS allocatable
  sections; **no W+X section**; no executable NOBITS; alignment power of two and <= page; **no `SHT_REL` (RELA only)**,
  no `SHT_DYNSYM`, no `SHN_COMMON` ("build with -fno-common"), no `SHN_XINDEX`; RELA must target an allocatable
  non-NOBITS section.
- Required sections: exactly one `.cosmo.module` -- PROGBITS, allocatable, not writable, exactly
  `sizeof(struct cosmo_module_info)` = 240 bytes, ends up in the rodata group (modelf.c:125-131, 192);
  optional `.ksymtab` (allocatable PROGBITS, not in text group, size multiple of `struct ksym`) (modelf.c:134-137, 195).
- Memory: up to three regions (text RX, rodata R, data RW), each `vm_kernel_alloc(VM_KALLOC_POPULATE |
  VM_KALLOC_GUARD | VM_KALLOC_NEAR_KERNEL, RW)`, then `vm_kernel_protect` to RX/R (module design.md:189-220; M6, M7).

### 6.2 Metadata, init/exit

```c
#define COSMO_MODULE_ABI_VERSION 11u            // module.h:23 (compared exactly, M4)
struct cosmo_module_info { char magic[8]; uint32_t abi_version; uint32_t capabilities; char name[32];
    char version[16]; char deps[128]; int (*init)(void); void (*shutdown)(void); uint64_t reserved[4]; }; // module.h:43-53
COSMO_MODULE(mname, mversion, minit, mshutdown, mdeps, mcaps)  // module.h:60-71, places it in .cosmo.module
```

- `init` returns 0 or a negative errno; `shutdown` is `void(void)`; exactly one `COSMO_MODULE` per module
  (module.h:57-59). Both must be non-NULL and point into the module after relocation (design.md:203-205).
- `init` and `shutdown` run in thread context with interrupts enabled, holding the loader mutex; they must not call
  `module_load`/`module_unload` (design.md:221-223, 300-302; M18).
- Dependencies: comma-separated names, at most `MODULE_MAX_DEPS 8`, each must be LIVE (module.h:29-30;
  design.md:186-188). Capabilities `MODULE_CAP_{DRIVER,FS,NET,TEST}` are recorded, not enforced (module.h:32-38;
  module invariants "Gaps").

### 6.3 Signing

- Ed25519 signature **trailer** appended to the file (`struct modsig_trailer`: magic, version, algo, key_id, sig);
  verified over every preceding byte against the kernel key ring (`kernel/module/modsig.c:18-66`; M1-M3).
  `-ENOKEY` no trailer/unknown key; `-EKEYREJECTED` bad version/algorithm/signature.
- `CONFIG_MODULE_SIG_ENFORCE` default 1 (modsig.c:9-11; build/config.mk:40). With enforcement off, an *unsigned*
  module loads with `MODULE_FLAG_UNSIGNED` and taints the kernel, but a bad signature is refused in every build
  (design.md:163-170; M2; panic.h:42).
- Signing tool `scripts/modsign.py sign --key $(MODSIGN_KEY)`; dev key auto-generated in
  `$(HOME)/.config/cosmoos/keys`, or `SIGNING=release` with explicit `MODSIGN_KEY`/`KEYRING_PUBS`
  (build/module.mk:18-25, 75; build/config.mk:52-77).

### 6.4 Relocations and symbols

- `int arch_module_reloc(vaddr_t target, size_t target_size, const struct elf64_rela *rela, size_t count,
  const uintptr_t *sym_addr, size_t nr_syms, const char **why);` (`kernel/include/arch/module.h:17-18`). Errors:
  -EINVAL (offset/symbol out of range), -ERANGE (overflow), -ENOEXEC (unsupported type).
- **x86-64** (`kernel/arch/x86_64/modreloc.c:1-10, 42-76`): `R_X86_64_NONE, _64, _PC32, _PLT32` (treated as PC32,
  no PLT), `_32, _32S`; GOT forms and everything else -> -ENOEXEC (M26). Modules compiled `-mcmodel=kernel`,
  non-PIC, placed in the near arena `[0xFFFFFFFF88000000, 0xFFFFFFFFFF000000)` (design.md:191-194; M7).
- **AArch64** (`kernel/arch/aarch64/modreloc.c:1-7, 60-175`): `NONE (+ NONE_ALT), ABS64, ABS32, ABS16, PREL64,
  PREL32, PREL16, ADR_PREL_LO21, ADR_PREL_PG_HI21, ADD_ABS_LO12_NC, LDST{8,16,32,64,128}_ABS_LO12_NC,
  LD_PREL_LO19, TSTBR14, CONDBR19, JUMP26, CALL26`. Modules compiled `-mcmodel=small`, placed within +-128 MiB of
  the kernel so CALL26/JUMP26 reach every export (no veneers/PLT) (modreloc.c:4-6). No GOT relocations
  (`ADR_GOT_PAGE`, `LD64_GOT_LO12_NC`) and no TLS relocations are accepted.
- **Exports**: `EXPORT_SYMBOL(sym)` emits `static const struct ksym { const char *name; uintptr_t addr; }` into
  `.ksymtab` (module.h:73-89). Kernel's index is sorted once (`module_init`) and immutable (M19); `ksym_lookup` is
  lock-free (design.md:296-299). There are 222 `EXPORT_SYMBOL` sites in `kernel/` and `drivers/`.
- **Imports**: undefined globals resolve against the kernel export table, then the export tables of the *declared*
  dependencies only; unresolved -> `-ENOENT` "unresolved symbol"; undefined weak -> 0 (M16); flat namespace: a module
  export may not repeat or shadow a kernel export (`-EEXIST`) (design.md:196-209; M12, M13).
- Exported surface relevant to Nova (module api.md:103-120): heap (`kmalloc` family + kmem_cache), strings
  (`memcpy, memmove, memset, memcmp, ...`), locks (spinlock + mutex), scheduling/time (`thread_create`,
  `thread_sleep_ns`, `sched_block_current` + `waitqueue_*`, `arch_percpu_get`, `arch_cpu_id`, timers, completions),
  lifetime (`synchronize_quiesce`, `call_quiesce`, `quiesce_read_{lock,unlock}_debug`, `synchronize_irq`,
  `interrupt_unregister_sync`, kobject API), device model, DMA, PCI/MSI-X, block, netif, mbuf, console, entropy,
  `panic`, `kprintf`. **Not exported**: `pmm_*`, `vm_kernel_alloc/free/protect`, `vm_map_phys`, `preempt_enable`,
  `percpu_checked`, `interrupt_register` (drivers get vectors via `pci_msix_request` etc.).
- ABI policy: any incompatible change to the exported set or to a structure an exported function takes bumps
  `COSMO_MODULE_ABI_VERSION` (module.h:4-11); history v2..v11 in module api.md:72-96 (e.g. v3 `spinlock_t`/`struct
  mutex` gained `class`, v10 `struct mutex` gained `handoff`). Embedded struct layouts are ABI.

### 6.5 Unload protocol (code: `kernel/module/module.c:569-670`)

```text
module_unload(name)  [holds g_lock mutex throughout; sleeps]
 0. reap collectable zombies (after resolving the name); -EBUSY if refs (live dependants) != 0      (M14)
 1. state = GOING; unpublish from g_live[]; unlink                                                 module.c:615-620
 2. info->shutdown()  -- module must synchronously unregister its handlers, timers, callbacks       module.c:622-625
 3. synchronize_quiesce()  -- no CPU still in a section that saw the module                         module.c:627-631
 4. wait up to MODULE_UNLOAD_TIMEOUT_MS_DEFAULT (5000 ms) for live_objects == 0, sleeping 1 ms      module.c:633-636
    else: zombie (memory mapped, name reusable, deps pinned), return -EBUSY                         module.c:637-654
 5. drop_deps, free regions and record, reap other zombies                                          module.c:656-669
```

- `live_objects` counts only **kobjects** whose release code (or tracked callback, `kobject_track_code`) lies in
  the module; raised by `module_owner_of` inside a quiesce read section (module.h:138-143; object.c:20-28; Q15, Q16,
  M22-M24). Timers, IRQ handlers, wait-queue callbacks, irq_poll, threads and `call_quiesce` heads owned by the
  module are **not** tracked: correctness depends on `shutdown()` tearing them down synchronously (module.c:622-624).
- There is no `call_quiesce` barrier (no `rcu_barrier` analogue; grep finds none): a `call_quiesce` callback in
  module text submitted before unload is not waited for by step 3 (inferred risk; see §11).
- Exported addresses from `module_symbol_lookup` are valid only while the owner is LIVE (design.md:267-269).

---

## 7. Compiler flags (build/toolchain.mk, build/arch/*.mk, build/module.mk)

Toolchain: clang + ld.lld only ("The kernel is built with clang only", compiler.h:4-6; toolchain.mk:1-20). Targets
`x86_64-unknown-none-elf`, `aarch64-unknown-none-elf` (build/arch/x86_64.mk:3, aarch64.mk:3). GNU make >= 4.3.

| Flag | Value | Cite |
|---|---|---|
| Language | `-std=c11` | toolchain.mk:35 |
| Freestanding | `-ffreestanding -nostdlib -nostdinc`, only clang's resource headers (`stdint.h, stddef.h, stdbool.h, stdarg.h`) via `-isystem` | toolchain.mk:35, 58-61 |
| Stack protector | `-fno-stack-protector` | toolchain.mk:36 |
| Frame pointers | `-fno-omit-frame-pointer` (+ AArch64 `-mno-omit-leaf-frame-pointer`) | toolchain.mk:36; aarch64.mk:13-20 |
| Aliasing | `-fno-strict-aliasing` | toolchain.mk:36 |
| Unwind | `-fno-asynchronous-unwind-tables -fno-unwind-tables` | toolchain.mk:37 |
| Warnings | `-Wall -Wextra -Werror` + list in §5 | toolchain.mk:27-31 |
| Optimisation | debug `-O1 -g -DCONFIG_DEBUG=1`; release `-O2 -g -DCONFIG_DEBUG=0` | toolchain.mk:52-56 |
| Config macros | `CONFIG_SELFTEST, CONFIG_LOCKDEP, CONFIG_CRASH_TEST, CONFIG_COSMOFS_STOP, CONFIG_PERCPU_WARN, CONFIG_HARNESS_BREAK, CONFIG_SCHED_BALANCE, CONFIG_SCHED_CHAOS, CONFIG_MODULE_SIG_ENFORCE` all always defined (0/1) | toolchain.mk:41-49 |
| Frame limit | `-Wframe-larger-than=2048` (error) | toolchain.mk:64-71 |
| x86-64 | `-mcmodel=kernel -mno-red-zone -mgeneral-regs-only -fno-pic -fno-pie -DARCH_X86_64=1` | x86_64.mk:9-11 |
| AArch64 | `-march=armv8-a -mcmodel=small -mgeneral-regs-only -mno-outline-atomics -mno-omit-leaf-frame-pointer -ffixed-x18 -fno-pic -fno-pie -DARCH_AARCH64=1` | aarch64.mk:18-21 |
| Link (kernel) | `-nostdlib -static --no-dynamic-linker -z max-page-size=0x1000 -z noexecstack -z separate-code --build-id=sha1 --gc-sections` + linker script | toolchain.mk:76-80; kernel.mk:234-236 |
| Modules | `MODULE_CFLAGS := $(KERNEL_CFLAGS) -DCOSMO_MODULE_BUILD=1`; `ld.lld -r --no-dynamic-linker -z noexecstack --build-id=none` | module.mk:14-15 |
| Sanitizers | **none in the kernel** (no `-fsanitize` in Makefile/build/kernel.mk); host tests use `-fsanitize=address,undefined` and TSan | tests/host/host.mk:10, 19, 294 |
| Include rule | generic code gets only generic include paths; arch headers only for arch sources ("Invariant 1") | kernel.mk:3-6, 229-230 |
| Reproducibility | `SOURCE_DATE_EPOCH=0`, `-ffile-prefix-map=$(ROOT)=/cosmo` | config.mk:164-165; toolchain.mk:38 |

Notes:
- `-fno-common` is not passed explicitly; the module validator rejects COMMON symbols (modelf.c:206). Clang's default
  since 11 is `-fno-common` (inferred; external fact).
- No `-fno-builtin`: clang may emit calls to `memcpy/memset/memmove/memcmp`, which the kernel provides and exports
  (module api.md:108) (inferred).
- No `-fno-delete-null-pointer-checks`, no `-fwrapv`/`-fno-strict-overflow`, no `-ftrivial-auto-var-init`, no CFI,
  no shadow call stack, no `-mbranch-protection`, no retpoline flags (absence in toolchain.mk/arch .mk files).
- AArch64 x18 is reserved (`-ffixed-x18`); generated code must not use it.
- Debug and release differ in `CONFIG_DEBUG`, default `LOCKDEP`, `SELFTEST`; inline functions in headers change
  behaviour with these (e.g. `this_cpu()` percpu.h:122-138, `arch_irq_save` arch/irq.h:35-43). A separately compiled
  object must use the **same** config macros as the kernel it targets (inferred).

---

## 8. Stack and floating point

- Kernel thread stack: `THREAD_STACK_SIZE (16u * 1024u)` (`kernel/include/kernel/thread.h:30`), allocated with
  guard pages (`thread.c:259-260`). Interrupt handlers run "on the interrupted stack" (interrupt.h:10), so IRQ frames
  consume the same 16 KiB (x86-64 uses IST stacks only for #DF, NMI, #MC, #DB: `kernel/arch/x86_64/idt.c:56-71`).
- Limits: per-function frame <= 2048 bytes as a compile error (S32; toolchain.mk:64-71); "No kernel thread uses
  more than 75 % of its 16 KiB stack" measured by stack painting in every debug boot, warning at >75%
  (`thread.c:169-241`; S32 `docs/kernel/scheduler/invariants.md:371-384`). No `-fstack-size-section` or
  static whole-call-graph depth analysis exists (inferred from flags).
- **Floating point/SIMD is not usable in the kernel**: `-mgeneral-regs-only` on both arches; "The kernel ... never
  touches x87, SSE, AVX or NEON registers itself" and the rule is checked on the built image by
  `scripts/check-fpregs.sh` with a short allowlist (`docs/kernel/arch/design.md:59-80`; Prompt #3 §3.3 "Kernel rule").
  There is no `kernel_fpu_begin/end` API; kernel threads own no FPU state (arch/design.md:93-95). Eager FPU switching
  for user threads (arch/design.md:96-100). `check-fpregs.sh` runs on `$(KERNEL_ELF)` only (Makefile:525-527), not
  on module `.ko`s (inferred from the Makefile line).

---

## Implications for Nova (constraints a Nova kernel profile must respect)

Memory
1. Model at least five distinct allocation capabilities: `KernelHeap` (kmalloc/kfree, <= 4 MiB, 16-byte aligned,
   NULL on failure), `ObjectCache<T>` (kmem_cache, fixed size, no constructor, alignment pow2 >= 16, name immortal),
   `PhysicalPages` (pmm order 0..10, zone flags, naturally aligned; kernel-internal only, not exported to modules),
   `KernelVirtual` (vm_kernel_alloc, page multiple, guard/populate/near, returns 0 on failure; not exported), and
   `Dma` (dma_alloc coherent / dma_map streaming; distinct bus address; failure 0/NULL). Do not unify them.
2. All of `kmalloc`, `kmem_cache_alloc`, `pmm_alloc_pages` are non-sleeping and IRQ-safe today, so Nova need not
   split heap capabilities by GFP context -- but `interrupt.h:10-11` forbids allocation in handlers by policy, and
   `dma_alloc` is thread-context only. A Nova `@noalloc` should be honoured in IRQ handlers even though the C
   allocator would technically work (see §11 for the conflict; ask the owner).
3. Allocation failure must surface as `Result`/`Option` (NULL or 0 in C). Misuse is a kernel panic in every build
   (double free, wrong cache, wrong order) -- Nova's ownership must make these unreachable; there is no recoverable
   error to map them to.
4. Freeing requires the right partner: `kfree` (no size), `kmem_cache_free(cache, obj)` (cache must match),
   `pmm_free_pages(page, order)` (order must match), `dma_free(dev, size, va, dma)` (all four must be remembered),
   `vm_kernel_free(base)`. A Nova `Box` must carry the allocator identity and any needed size/order/bus address.
5. `kmalloc` zeroing is opt-in (`KMEM_ZERO`) and `krealloc` zeroes only beyond the old usable size -- Nova must not
   assume zeroed memory from `krealloc` growth without explicitly zeroing the tail of the requested size.

Addresses
6. CosmoOS's `paddr_t`, `vaddr_t`, `pfn_t`, `dma_addr_t` are interchangeable `uint64_t`/`uintptr_t` in C; Nova must
   supply distinct nominal types itself and lower them to these typedefs at the C boundary. Conversions are only
   via `phys_to_virt/virt_to_phys/page_to_*` (direct map only; `phys_in_direct_map` guards it) and `dma_map`.
7. MMIO: no kernel accessor layer exists; Nova must emit its own volatile loads/stores (or a small C shim) of the
   exact width, plus `arch_dma_barrier()` (sfence / dsb sy) before doorbells and `__atomic_thread_fence` where
   needed. MMIO bases arrive as `vaddr_t` from `pci_map_bar`/`device_map_mmio`/`vm_map_phys` and should become a
   typed `MmioRegion` with bounds. Cache attribute names differ in meaning per arch (`VM_CACHE_WT` = Normal-NC on
   AArch64).
8. DMA is coherent on supported machines; `dma_sync_*` are barriers only. Nova should still require sync calls in
   its ownership-transfer API (CPU-owned vs device-owned buffer states) so a non-coherent port works later.

Lifetime
9. Kernel objects are `struct kobject` embedded by value with a mandatory `kobject_type.release`; Nova must not
   auto-`free` them. A Nova `Arc`-like handle should map to `kobject_get/put`, with `tryget` returning
   `Option` for table lookups. The final `put` may run a release that **may block** and must run with **no spinlock
   held** -- this is not checked at runtime, so Nova's effect system should treat `kobject_put` as potentially
   sleeping unless the type is statically known to have a non-blocking release.
10. Lookups return referenced pointers, never borrowed ones (object.h:15). Nova borrows from a lookup are therefore
    owned references with a scoped drop; the only legitimate borrowed-without-refcount access is inside a quiesce
    read section, bounded by it.
11. Epoch guard: `quiesce_read_lock` = `preempt_disable` (nestable); any spinlock or IRQ handler is also a read
    section. A Nova `QuiesceGuard` must be `!Send` (pins the CPU), non-escaping, and forbid any sleeping effect in
    its scope. `synchronize_quiesce` is a sleeping effect forbidden under any guard or spinlock; `call_quiesce` is
    allowed anywhere but requires an embedded `quiesce_head` that is zeroed/idle, owned by the subsystem until the
    callback runs, and never resubmitted while pending.
12. Modules cannot currently call `quiesce_read_unlock`/`preempt_enable` (not exported). A Nova module would need a
    new export or must not use explicit read sections -- this needs a CosmoOS change (ABI addition is compatible).
13. Reclaim protocol to encode: unlink -> grace period (synchronize/call_quiesce, synchronize_irq,
    timer_cancel_sync, irq_poll_disable, waitqueue_remove_callback) -> free. Each callback registration kind needs
    its own "proof of quiescence" token before the owning memory can be dropped.
14. Handles are `int` slots without generation: Nova must not treat a stored handle number as identity.

Synchronization and context
15. Atomic vs sleepable is a runtime property (`preempt_count`, `irq_depth`). Nova's compile-time effects
    (`may_sleep`, `may_alloc`, `irq_safe`, `holds<SpinLock>`) must be conservative approximations; keep the C
    `might_sleep()` runtime checks in generated code at sleeping-call boundaries rather than claiming to replace them.
16. Locks: spinlock (non-recursive, disables preemption; irqsave variant mandatory if the lock is ever used in IRQ
    context), mutex (sleeping, owner-tracked, PI, not from IRQ or preempt-disabled), semaphore, completion, wait
    queue. No rwlock. Nova guards should be distinct types for `SpinGuard` vs `IrqSpinGuard` (holding the saved
    `arch_irq_state_t`) vs `MutexGuard`; restore order must follow L20 (innermost save).
17. Lockdep classes are the lock's init name string (<= 63 chars, immortal). Nova must generate a stable, unique,
    human-meaningful name per lock family (not per instance) and expose `*_lock_nested(subclass)` only with a
    documented ordering justification. Nova's own lock-order checking (if any) should feed, not replace, lockdep.
18. Per-CPU: no generic per-CPU variables; access via `struct percpu`/arch arrays is valid only while
    preemption/IRQs are off or in IRQ context (S25). A Nova `PerCpu<T>` borrow must require a `NoMigrate` token
    (preempt guard, spin guard, IRQ context) and must not cross a sleep.
19. IRQ handlers: no sleep, no allocation (policy), no sleeping lock, run on the interrupted 16 KiB stack;
    long work goes to `irq_poll` or a kernel thread. Timer callbacks run in IRQ context on the arming CPU.

Codegen
20. Generated C must compile with exactly the kernel's flags: clang only, `-std=c11 -ffreestanding -nostdinc`,
    `-Werror` with the listed warnings (generated code must be warning-clean under `-Wshadow -Wcast-qual
    -Wmissing-prototypes -Wstrict-prototypes -Wvla -Wundef -Wimplicit-fallthrough` etc.), `-Wframe-larger-than=2048`,
    `-fno-strict-aliasing`, non-PIC; x86-64 `-mcmodel=kernel -mno-red-zone -mgeneral-regs-only`; AArch64
    `-mcmodel=small -mgeneral-regs-only -mno-outline-atomics -ffixed-x18`. No VLAs, no FP/SIMD (no `float`/`double`
    at all), no TLS, no C++/unwind tables, no libc beyond the exported string functions.
21. Nova must statically bound stack frames (2 KiB per function, error) and should track whole-call-chain depth
    against the 16 KiB stack shared with interrupts; large locals must become heap allocations.
22. Config macros (`CONFIG_DEBUG`, `CONFIG_LOCKDEP`, `CONFIG_SELFTEST`, ...) change inline header behaviour; Nova
    builds must be produced per kernel configuration and arch, never shared across debug/release.

Modules
23. A Nova module is viable through the C backend without loader changes iff it is an ELF64 ET_REL produced by
    `ld.lld -r` from objects compiled with `MODULE_CFLAGS`, containing exactly one 240-byte `.cosmo.module` (via the
    `COSMO_MODULE` macro, ABI 11), optional `.ksymtab`, no W+X sections, no COMMON, RELA only, only the listed
    relocation types (no GOT, no TLS, no PLT/veneers; AArch64 calls within +-128 MiB), and is signed with
    `scripts/modsign.py`. Only exported kernel symbols and declared dependencies' exports can be imported.
24. A module's `shutdown()` must synchronously tear down every IRQ, timer, wait callback, irq_poll and thread it
    created, because only kobjects are tracked by `live_objects`. Nova should generate unload code from ownership
    information so that no callback registration outlives `shutdown()`, and must avoid pending `call_quiesce`
    callbacks in module text at unload (no barrier exists).
25. Kobjects whose release is in Nova module code pin the module (zombie after 5 s). Nova must ensure every
    release path for module-defined types is in the module's own text (so `module_owner_of` attributes it) and
    that release code only calls into declared dependencies.
26. Struct layouts of exported types (`spinlock_t`, `struct mutex`, `struct kobject`, `struct waitqueue`,
    `struct device`, ...) are module ABI; Nova must import them from the C headers of the exact ABI version (11),
    not re-declare them.

---

## Open questions / doc-code disagreements

Doc vs code
1. `kernel/include/kernel/types.h:4-6` says "the type system says so" for physical vs virtual addresses, but
   `paddr_t`/`vaddr_t`/`pfn_t`/`dma_addr_t` are plain integer typedefs (types.h:16-18, dma.h:20); C does not
   distinguish them.
2. `kernel/include/kernel/spinlock.h:9-10` says "There are no sleeping locks yet" -- mutexes, semaphores, wait
   queues and completions exist (mutex.h, semaphore.h, wait.h, completion.h).
3. `kernel/include/kernel/interrupt.h:10-13` says handlers "must not ... allocate" and that deferred work "arrives
   with the scheduler in Phase 3"; `kmalloc.h:4-5` and memory M23 say every heap/PMM function is interrupt-safe;
   there is still no general deferred-work API (irq_poll is the closest). Which rule governs allocation in IRQ
   handlers is ambiguous.
4. `docs/kernel/memory/invariants.md:170-181`: M22 "Checked by: review. A lock-order checker is planned" (lockdep
   exists, L1); M23 "there is no sleeping primitive to call" (there are several; and `vm_space.replace_lock` is a
   mutex, vmm.h:99-107).
5. `docs/kernel/lockdep/locking-rules.md:68-70` says `vm_kernel_alloc` has a `might_sleep` boundary; the code
   (vmm.c:287-314) contains none and takes only an irqsave spinlock. (`vm_kernel_protect`/`vm_kernel_free` do a TLB
   shootdown requiring IRQs enabled, which is a different constraint.)
6. `docs/kernel/module/design.md:28` shows `COSMO_MODULE_ABI_VERSION 1`; code is 11 (module.h:23). design.md step 2
   (:171-173) says `EM_X86_64` and step 6 (:210-214) lists only x86 relocations; code is per-arch
   (`ELF_MACHINE_NATIVE`, modelf.c:85; AArch64 modreloc.c). design.md:234-236's unload summary omits the grace
   period and live-object wait that the later section and code have (module.c:615-654). module api.md heading says
   "Module ABI v3" while the text says 11 (api.md:70-72).
7. `docs/kernel/quiesce/design.md:294-297` says `module_owner_of` "walks the module list"; module design.md:283-287
   and code use the fixed `g_live[]` array. design.md:339-340 says `synchronize_quiesce` uses "no IPIs"; Q6 and the
   stats (`straggler_ipis`) say stragglers are kicked by IPI.
8. `kernel/include/kernel/compiler.h:65-75` and `docs/build/design.md:194-196` describe "33" baseline frame
   exemptions; `scripts/frame-baseline.txt` has none and S32 says "there are none since roadmap M6".
9. `object.h:5-7` says get/put are "usable in any context" but the release "may block" and callers "must not hold"
   a spinlock; no runtime check enforces the latter on the final put (object.c:61-81).

Open questions for the CosmoOS owner
10. Should `preempt_enable` (or a non-inline `quiesce_read_lock/unlock`) be exported so modules can use explicit
    read sections? Currently a module using the inline `quiesce_read_unlock` would fail with an unresolved symbol
    (inferred: preempt_enable has no EXPORT_SYMBOL; `percpu_checked`, used by `this_cpu()` in debug builds, is also
    unexported).
11. Is there a plan for a `call_quiesce` barrier at module unload (rcu_barrier analogue)? Today a pending callback in
    module text is not waited for (module.c:627-631 only synchronizes).
12. Are module `.ko` files meant to be FP-register-checked like the kernel (check-fpregs.sh runs only on the kernel
    ELF)? Nova modules will rely on `-mgeneral-regs-only` alone otherwise.
13. Would the owner accept a typed address layer (wrapper structs) or sparse-style attributes in C headers, or
    should Nova keep all address typing on its side of the boundary?
14. Should Nova kernel code be allowed to call non-exported internals (pmm, vm_kernel_alloc) only when linked into
    the kernel image (Profile B built-in) versus only exports when built as a module? The two surfaces differ widely.
15. Is `kobject_put` from IRQ context permitted when the release is known not to block (several network paths take
    references in softer contexts)? The header permits "any context" for put but the release contract conflicts.
16. Allocation under a quiesce read section or spinlock is legal today (kmalloc never sleeps); should Nova's
    `@nosleep` permit `may_alloc` there, given L4's note that kmalloc may gain a `might_sleep` annotation if it ever
    can sleep?
