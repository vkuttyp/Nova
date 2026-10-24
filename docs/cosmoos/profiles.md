# Nova Compilation Profiles for CosmoOS (+ Freestanding Runtime Design)

Status: C0 proposal (deliverables 3 and 7); §4, the freestanding profile, is normative from M8c. Facts are cited from
`docs/research/cosmoos-platform-audit.md` **[PA §n]** and
`docs/research/cosmoos-kernel-audit.md` **[KA §n]**.

A *profile* fixes three things: (1) the language features permitted, (2) the
runtime library linked, (3) the C flags and link recipe. Profiles are selected
with `novac build --profile <p> --target <t>`. A profile is **not** "userland
with the standard library switched off" (prompt §2). Each feature is either
allowed or not, and the list below is normative.

## 1. Profile matrix

| Feature | `hosted` (Linux/macOS dev) | **A: `cosmoos-user`** | **B: `cosmoos-kernel`** | `freestanding` (generic; M8c, §4) |
|---|---|---|---|---|
| Libraries | core + std | core + std(cosmoos) | core + kernel | core |
| Default allocator type param | `SystemHeap` (malloc) | `SystemHeap` (CosmoOS libc malloc) | **none**: must be explicit | none |
| Panicking allocation helpers (`!` on `AllocError`, `create().orPanic`) | allowed | allowed | **lint error** | lint error |
| `f32`/`f64` | yes | yes (no `<math.h>` in libc: Nova's `Math` implemented in Nova/C runtime) | **forbidden** (`-mgeneral-regs-only`) [KA §7–8] | target-defined |
| Threads, `Send`/`Sync` | later | via `cosmo_thread_*` only [PA impl 4] | kernel threads via KThread wrapper (C7) | no |
| Native stack value limit | 64 KiB | 64 KiB | **512 B** per value; **2 KiB per frame** (CosmoOS `-Wframe-larger-than=2048`, error) [KA impl 21] | configurable |
| Whole-call-chain stack budget | — | — | report + error against **16 KiB** (shared with IRQs) [KA §8] | configurable |
| Scratch regions | reserved VM | reserved VM via `mmap` | fixed per-thread buffer, explicit checks | fixed buffer |
| Panic | stderr + `abort()` | stderr + `abort()` via libc | CosmoOS `panic()` | user-provided `nova_panic` |
| Context effects (`@context`, `sleep`/`alloc`) | ignored | ignored | **enforced** (C7) | optional |
| `extern "C"` imports | any | libc public headers only (L1, OD-15) | bindings file (exports only for modules) | any |
| `static var`, raw pointers, MMIO | unsafe | unsafe | unsafe (+ MMIO types) | unsafe |
| TLS (`@threadLocal`) | yes | yes (`__thread` works) [PA §4] | **no** (no TLS in the kernel) | no |
| Recursion | yes | yes | allowed, but makes the stack bound unprovable → the function must be `@unboundedStack` and is excluded from IRQ/atomic contexts | yes |

## 2. Profile A — `cosmoos-user`

### 2.1 Target and code generation [PA §3, impl 1]
- Triples: `x86_64-unknown-none-elf`, `aarch64-unknown-none-elf`. LP64, max
  alignment 16, `long double` = 16 bytes (unused by Nova), `char` signedness
  differs (irrelevant: Nova never uses `char`).
- C flags: CosmoOS `USER_CFLAGS` exactly (`build/toolchain.mk:27-61`,
  `libc/libc.mk:12-18`): `-std=c11 -ffreestanding -nostdinc -fno-pic -fno-pie
  -fno-stack-protector`, no unwind tables, CosmoOS warning set with `-Werror`.
  Nova does **not** maintain its own copy of these flags. It reads them from the CosmoOS
  build (`make print-USER_CFLAGS`-style query or `compile_commands.json`; see
  `build-integration.md`).
- Link: static ET_EXEC at 0x400000 with `userland/user.ld`, `crt0.o`, `libc.a`.
  No `.init_array` support exists, so **Nova must never emit constructors**. The
  `.note.cosmo` note from `crt0` must be present; without it the binary silently
  runs as a Linux binary [PA impl 6]. The Nova build checks the note with `llvm-readelf -n`.

### 2.2 Runtime: `runtime/cosmoos/user/`
- `main(int argc, char** argv, char** envp)` is generated. It wraps argv into
  `Slice<StringView>` without copying, calls Nova `main`, runs no `atexit`, and returns the
  exit code. Deterministic cleanup has completed when Nova `main` returns.
- `SystemHeap` = `malloc`/`free`/`realloc`. Over-aligned (`> 16`) layouts are
  served by over-allocating and storing the original pointer in a header (no
  `aligned_alloc` in libc) [PA impl 3].
- Arenas and scratch regions take chunks from `mmap(MAP_ANONYMOUS)` (a public libc
  function, consistent with L1).
- Console/IO: `write`/`read` on fds. Files: `open/read/write/close/stat/dirent`.
  Processes: `spawn*` + `waitpid` (no fork/exec). **Implemented in C3** (type-system.md §26,
  `runtime/hosted/nova_rt_io.c`): files (`open/read/write/close/unlink`), TCP over IPv4, `spawnvp` +
  `waitpid`, and argv; `stat`/`dirent` and time are not yet. Time: `clock_gettime`
  (monotonic + realtime). Sockets: CosmoOS-shaped socket API. Absent in libc:
  math, poll/select, DNS, `setjmp`, sigaltstack [PA impl 2]. Nova's std
  omits them in v0.
- **L1 policy** (OD-15): the Nova runtime calls **only public libc functions**, never
  raw syscalls, until the CosmoOS owner rules on whether Nova's runtime counts as "library".

### 2.3 Alternative A-musl (not recommended for C1)
CosmoOS already builds musl 1.2.5 + compiler-rt and ships musl programs under
the Linux personality [PA impl 7]. Nova could target it for richer POSIX
coverage, but it is not "native CosmoOS" (Invariant 7) and is kept as a
later option (OD-16). The C backend needs no changes for it, only a different
profile runtime.

## 3. Profile B — `cosmoos-kernel`

### 3.1 Permitted language features (normative)
- All of core: structs, enums, generics, slices, `Option`/`Result`, borrowing,
  `Box<T, A>` with an **explicit** kernel allocator type, `using`, `defer`.
- No floats, no TLS, no implicit allocation (no default `A`; no allocating
  operators; string formatting writes into caller-provided buffers or explicit
  `KernelHeap` strings).
- No panicking allocation. Panics in general are **discouraged**: the kernel
  profile lint `kernel-panic-sites` reports every panic site (bounds, overflow,
  unwrap). `@noPanic` on a function makes any remaining panic site a compile
  error. That is the kernel analogue of Goose's `checkfree` (goose.md §9.9).
- Context effects enforced (`kernel-memory-and-lifetimes.md` §6).
- Two sub-modes: `--kernel-link=builtin` (internal APIs allowed, KQ-5) and
  `--kernel-link=module` (exported symbols only; generates `COSMO_MODULE`
  metadata, `init/shutdown`) [KA impl 23].

### 3.2 Code generation [KA §7, impl 20–22]
- clang only. `-std=c11 -ffreestanding -nostdinc`, `-Werror` with the kernel
  warning set, `-Wframe-larger-than=2048`, `-fno-strict-aliasing`, non-PIC.
  - x86-64: `-mcmodel=kernel -mno-red-zone -mgeneral-regs-only`.
  - AArch64: `-mcmodel=small -mgeneral-regs-only -mno-outline-atomics -ffixed-x18`.
- Flags are taken from CosmoOS's build per **configuration and arch**.
  `CONFIG_*` macros change inline header behaviour, so Nova objects are never shared between debug
  and release builds [KA impl 22].
- Kernel struct layouts (`spinlock_t`, `struct kobject`, `struct mutex`, …) are
  **never** re-declared by Nova. Generated C `#include`s the CosmoOS headers of
  the exact module ABI (currently 11), and Nova sees those types as opaque `@extern`
  structs with size and alignment queried at build time [KA impl 26].

### 3.3 Freestanding runtime: `runtime/core/` + `runtime/cosmoos/kernel/`

| Runtime need | Freestanding core (no libc) | CosmoOS kernel binding |
|---|---|---|
| `nova_panic(kind, src)` | plain `extern` (no weak symbols), which the profile must define | calls CosmoOS `panic("nova: %s at %s:%u")` |
| `nova_mem_copy/move/set/cmp` | `__builtin_mem*` (may lower to calls) | kernel exports `memcpy/memmove/memset/memcmp` (string functions exported [KA impl 20]) |
| Checked arithmetic | `static inline` in `rt.h`, no libcalls | same |
| Allocation | none (allocators are library types over externs) | `kmalloc/kfree`, `kmem_cache_*`, … via bindings |
| Source locations | static `const` tables | same (in `.rodata`; no relocations beyond the allowed set) |
| Stack metadata | `-fstack-usage` output parsed by `novac` | call-graph depth check against 16 KiB |

The core runtime is ~300 lines of header plus a few C functions. It has **no global
mutable state** (so no TLS or per-CPU questions) and **no constructors**.

Acceptance (C4): `llvm-nm -u` on a kernel-profile object lists only symbols
from the profile's allowlist (`memcpy`, `memset`, `memmove`, `memcmp`,
`panic`, plus bound kernel APIs); `llvm-readelf -r` shows only relocation types
accepted by the module loader [KA §6.4]; there are no `.init_array`, `.tdata`/`.tbss`,
`.eh_frame` or COMMON symbols.

## 4. The `freestanding` profile (M8c; normative)

`novac build --profile freestanding` compiles a package for an environment with no C library and
no operating system: a kernel, firmware, or a module that another runtime links. `--profile hosted`
is the default and is what every earlier milestone built. (`cosmoos-user` is hosted with CosmoOS's
flags, C1–C3; `cosmoos-kernel` is C4–C8 and builds on this profile.)

### 4.1 What freestanding code may use
Everything in the language that needs no runtime service: integers, `bool`, structs, enums,
arrays, slices, `StringView`, optionals, `Result` and `AllocError`, references, generics and
interfaces, `unsafe` and raw pointers (type-system §27), the C FFI (§28), `FixedBufferAllocator`
over caller-provided storage, `Ptr`, `Slice.fromRaw`, and `truncate`.

These need a hosted runtime and are rejected in freestanding code (N0293), wherever they are
named (a type, a call, a struct literal):

| Name | Why |
|---|---|
| `Console`, `Process` | standard streams, exit, arguments and processes are operating-system services |
| `File`, `TcpListener`, `TcpStream`, `IoError` | files and sockets |
| `SystemHeap`, `FailingAllocator` | there is no system heap; `FailingAllocator` wraps it |
| `Box`, `PoolBox`, `Pool`, `Arena`, `Scratch`, `Vector`, `String`, `SlotMap`, `StableVector` | each allocates from one of the two allocators above (`Scratch` from the runtime's heap-backed stack) |

A `main` function is N0293 too: there is no C `main` to call it. A freestanding package is entered
through its `@export` functions (§28.4; custom entry points are M8d). Allocators that a freestanding
environment provides (CosmoOS's `KernelHeap`, C5) will come with user-defined allocators.

### 4.2 Runtime and the panic hook
- The program is the generated `main.c`, `nova/rt.h`, and the core runtime `nova_rt_core.c` compiled
  with `NV_FREESTANDING` defined, which leaves out everything that needs a platform service (output,
  exit, heap accounting, arenas, scratch frames, pools and collections). What remains has no
  mutable global state and no constructors: the fixed-buffer allocator, UTF-8 validation, and the
  checked arithmetic in `rt.h`.
- **`nova_panic` is the environment's.** Generated code calls
  `void nova_panic(nv_panic_kind kind, nv_loc loc)` (declared in `nova/rt.h`) for every panic:
  overflow, bounds, unwrap, and the rest. The freestanding runtime does not define it; the code that
  links the object must, and it must not return (panic = abort, OD-5). `kind` is an
  `nv_panic_kind`; `loc.file` is the Nova source path (a zero-terminated string), `loc.line` and
  `loc.column` 1-based.
- The C compiler may itself emit calls to `memcpy`, `memmove`, `memset` and `memcmp` (struct copies,
  zeroing), as it may in any freestanding C (C11 §4 and the compilers' documentation); the
  environment provides them.

### 4.3 `--emit obj`
`novac build --emit obj -o out.o` compiles each C file with `-c` (and the flags given with
`--cflag`) and joins the objects into one relocatable object: with `<linker> -r` when `--ld <linker>`
is given (`ld.lld` for the ELF cross builds, as CosmoOS links), otherwise with the C compiler's `-r`
(passing it the `--target=…` flag). It works in
both profiles; with `--profile freestanding` it is the normal output.

### 4.4 The symbol allowlist (acceptance, M8 criterion 1)
The undefined symbols of a freestanding object (`llvm-nm -u`) are only:
`nova_panic`, `memcpy`, `memmove`, `memset`, `memcmp`, and the functions the package declares in
`extern "C"` blocks. Checked by `FreestandingTests` for the host and for both CosmoOS triples
(`x86_64-unknown-none-elf`, `aarch64-unknown-none-elf`).
