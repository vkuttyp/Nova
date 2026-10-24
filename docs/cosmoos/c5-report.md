# C5 — Kernel memory integration (plan and report)

Status: **complete 2026-10-24** (results below). Roadmap acceptance: *`KernelHeap`, `ObjectCache<T>`
bindings; an isolated test component; an in-kernel selftest of allocation, exhaustion (fault-injected),
ownership transfer and cleanup; no allocator modifications* (`roadmap.md`; prompt `cosmoos-native.md` C5:
a small, isolated adapter to the existing kernel allocation APIs, tested for normal allocation,
exhaustion, ownership transfer and cleanup, without modifying the kernel allocator). Stage S0: out of
tree, CosmoOS read-only. Script: `integrations/cosmoos/c5.py`.

## Audit (CosmoOS `98040b9b`; read, not copied)

| Interface | Where | What it says |
|---|---|---|
| Kernel heap | `kernel/include/kernel/kmalloc.h`; exports `kernel/memory/kmalloc.c:182-185` | `void *kmalloc(size_t size, unsigned flags)`, `void kfree(void *)`: non-blocking, interrupt-safe; NULL on failure; at least 16-byte aligned (`KMALLOC_MIN_ALIGN`); at most `KMALLOC_MAX_SIZE` = 4 MiB; misuse (double free, foreign pointer) panics; `KMEM_ZERO` zeroes |
| Object caches | `kmalloc.h`; exports `kernel/memory/slab.c:328-331` | `kmem_cache_create/destroy/alloc/free`, exported |
| Fault injection | `kernel/include/kernel/faultinject.h`, `kernel/core/faultinject.c` | `FI_KMALLOC` fails `kmalloc`/`kmem_cache_alloc` every n-th time, for one thread or all; **debug builds only** (`CONFIG_FAULTINJECT` = `CONFIG_DEBUG`); `faultinject_set` is not exported (only `faultinject_should_fail` is); the boot parameter applies to every thread from boot |
| Module loading at boot | `kernel/module/module.c:737-751` (`module_load_boot`) | every `modules/*.ko` entry of the boot archive is loaded, in archive order; a failed init is counted and logged ("N boot module(s) loaded, M failed") |
| Module ABI | `kernel/include/kernel/module.h`, `docs/kernel/module/api.md` | `COSMO_MODULE(name, version, init, shutdown, deps, caps)`; `init` runs in thread context, may allocate; nonzero aborts the load |
| Logging | `kernel/include/kernel/log.h:31,50-52`; export `kernel/core/log.c:138` | `klog(level, fmt, ...)`; `kinfo`/`kerror` macros |

Consequences: fault injection cannot be driven from a module in a release build without changing CosmoOS,
so injected exhaustion is Nova-side (below), and real exhaustion uses the kernel's own refusal of
oversized requests.

## What was built

1. **User-defined allocators** (language; `type-system.md` §30, `memory-model.md` §3.2). `lib/core/alloc.nova`
   declares `Layout` and `interface Allocator { fn allocate(…) -> Result<*mut u8, AllocError>; unsafe fn
   deallocate(…); }`. A non-generic struct with no fields that implements it can be the `A` of
   `Box<T, A>`: `Box.createIn` calls `allocate`, and the box's drop calls `deallocate`, in generated C and in
   the interpreter. Boxes stay one pointer. Interface methods may be `unsafe` now. `Box` with a user
   allocator is allowed in the freestanding profile. Collections keep the built-in allocators (their growth
   is in the C runtime).
2. **`KernelHeap`** (`integrations/cosmoos/c5/kernel.nova`): `Allocator` over `kmalloc`/`kfree`, refusing
   alignments over 16 (kmalloc gives 16), turning NULL into `AllocError`. The kernel allocator is used as it
   is.
3. **The test component** (`c5/selftest.nova`, `c5/module.c`): an out-of-tree module, `nova_c5`, whose
   `init` runs five Nova tests and logs each result. `TestHeap` wraps `KernelHeap` with a fault-injection
   point and a live-block count kept in `module.c` (an allocator has no fields, so its state is C's). The
   module is built with CosmoOS's own `MODULE_CFLAGS`, joined with its `ld.lld` and `MODULE_LDFLAGS`, signed
   with `scripts/modsign.py` and the developer key, checked with `scripts/check-module-elf.py` and C4's
   checks, and added to a copy of the boot archive as `modules/nova_c5.ko`. CosmoOS then boots in QEMU.

## The tests (in the kernel)

| Test | What it does | Pass condition |
|---|---|---|
| `allocation` | two `Box<_, KernelHeap>`: a `u64`, and a struct built from the first's contents | the values read back (detail: the value, 0x1235 = 4661) |
| `ownership` | a `Device` holding a `Box<[u8; 64], TestHeap>` is moved into a call and back, written through, moved into an optional and matched out | the data arrives, and the live-block count is back where it started |
| `exhaustion-kmalloc` | `KernelHeap.allocate` of 8 MiB (over `KMALLOC_MAX_SIZE`: `kmalloc` itself returns NULL) and of 64-byte alignment (refused by the binding) | both `Err` |
| `exhaustion-injected` | builds an 8-link `Box<Chain, TestHeap>` list with allocation k failing, for k = 1, 2, …, until a run succeeds (the M3 sweep, in the kernel) | runs 1–9 return `Err`, run 10 succeeds, and every run leaves nothing live (detail 910 = 9 failed, 10 clean) |
| `cleanup-deep` | builds and drops a 20,001-node list | nothing live afterwards; the drop is iterative (`type-system.md` §18.5), so 16 KiB kernel stacks suffice |

## Results (run 2026-10-24)

| Check | x86-64 | AArch64 |
|---|---|---|
| Compiles with `MODULE_CFLAGS`; `check-module-elf.py` (ET_REL, W^X, `.cosmo.module`, signature) | pass | pass |
| `modelf.c` rules; relocations accepted by `modreloc.c` (x86-64: `R_X86_64_32S`, `64`, `PC32`, `PLT32`; AArch64: `ABS64`, `ADD_ABS_LO12_NC`, `ADR_PREL_PG_HI21`, `CALL26`, `JUMP26`, `LDST64_ABS_LO12_NC`); exports (`kfree`, `klog`, `kmalloc`, `panic`); no constructor, TLS or unwind sections; frames (largest 136 / 96 bytes, `c5_selftest`); `check-fpregs.sh` | pass | pass |
| The module loads (`module: loaded nova_c5 1.0`) and all five tests pass; 0 `TestHeap` blocks live at the end | pass | pass |
| Boot modules: 15 loaded, 0 failed; clean shutdown (QEMU 33); CosmoOS tree unchanged | pass | pass |

Hosted tests of the language part: `run-pass/user-allocators.nova` (a `malloc`/`free` allocator, an
allocator that always fails, a 100,001-node list) in every C configuration and in the interpreter, which
now models `malloc` and `free`; `check-fail/user-allocators.nova`; `freestanding/allocator.nova` (an
environment-provided allocator in a freestanding object, allowlist on the host and both triples).

## Not done

- **`ObjectCache<T>`.** A cache is state, so it cannot be a field-less allocator type. It needs either
  static items (the cache's identity in a type, `kernel-memory-and-lifetimes.md` §2) or a scoped cache box
  (like `PoolBox`), and `sizeOf<T>()` for `kmem_cache_create`. None exists yet; the C functions are
  exported and ready. Recorded for C6, where a component will need it.
- **Collections over `KernelHeap`** (`Vector<T, KernelHeap>`): the runtime's growth code would have to call
  Nova methods, or be written in Nova.
- **The kernel's own fault injection**: debug-only and not exported (audit above); using it would mean a
  debug build and a CosmoOS change, which C5 rules out.
- The kernel profile's lints (no panicking allocation in kernel code, `kernel-panic-sites`) and allocation
  effects are C7.
