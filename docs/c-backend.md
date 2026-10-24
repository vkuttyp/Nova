# C Backend Design (v0)

Status: Milestone 0 proposal. Applies from M1. CosmoOS-specific flags are covered in
`cosmoos/abi.md` and `cosmoos/build-integration.md`.

## 1. Target dialect

- **C11** (ISO/IEC 9899:2011), compiled with `-std=c11` (or `gnu11` where the
  host project requires it). Generated code must also compile under C17.
- Headers used by generated code: **only** freestanding headers (`<stdint.h>`,
  `<stddef.h>`, `<stdbool.h>`, `<stdalign.h>`, `<limits.h>`, `<stdnoreturn.h>`)
  plus `nova/rt.h`. Everything hosted lives behind runtime functions.
- Compiler-specific features are allowed only through macros in `nova/rt.h`
  with a portable fallback: `__builtin_*_overflow`, `__builtin_expect`,
  `__attribute__((noreturn, cold))`, `_Static_assert`.
- Supported compilers: clang ≥ 15 (CosmoOS's floor; its CI uses Debian clang 19.1.7)
  and gcc ≥ 11 for hosted builds. **CosmoOS profiles are clang/lld only**
  (`research/cosmoos-platform-audit.md` §3). MSVC is best-effort for the hosted profile
  (C11 mode, VS 2022), without the kernel profile.
- The generated C must compile warning-free under
  `-Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion -Werror`
  **and** under CosmoOS's own warning set (`-Wcast-qual -Wmissing-prototypes
  -Wstrict-prototypes -Wvla -Wundef -Wimplicit-fallthrough`, `-Wframe-larger-than=2048`
  for the kernel; see `cosmoos/abi.md`). All non-exported functions are `static`, and every
  exported one has a prototype in the generated header.
- Correct both with and without `-fno-strict-aliasing`. CosmoOS compiles with it,
  but Nova does not rely on it.
- **No compiler-rt / libgcc.** CosmoOS links no builtins library, so generated code
  must never need one. That rules out `__int128` division, `long double` arithmetic,
  16-byte atomics and, in AArch64 userland, outline-atomic helpers (to be verified in C1;
  pass `-mno-outline-atomics` if needed). The runtime header supplies portable
  fallbacks instead of builtins when a builtin would lower to a libcall.
- `-nostdinc` builds: freestanding headers come from `-isystem $(clang
  -print-resource-dir)/include` (CosmoOS invariant B2). Generated code must not include libc headers
  directly; only `nova/rt*.h` does, and only in hosted and userland profiles.
- **Runtime per profile (M8c, `cosmoos/profiles.md` §4.2).** Hosted programs get `nova/rt.h`, the
  core runtime `nova_rt_core.c`, and the platform layer `nova_rt_hosted.c` / `nova_rt_io.c` (C library
  calls). Freestanding programs get `nova/rt.h` and `nova_rt_core.c` compiled with `NV_FREESTANDING`
  (a `#define` at its top), which leaves out everything that needs a platform service; the
  environment defines `nova_panic`. Freestanding builds add `-ffreestanding -fno-stack-protector
  -fno-asynchronous-unwind-tables -fno-unwind-tables` (the stack protector's `__stack_chk_*` symbols
  come from the C library; unwind tables are useless with panic = abort and have no unwinder to read
  them in a kernel).
- Plain C `char` is **never** used for Nova data (`char` is signed on x86-64 and
  unsigned on AArch64; CosmoOS's `limits.h` is also wrong about it on AArch64). Bytes are `uint8_t`.

## 1.1 MSVC, best effort (M7e)

The hosted profile also builds with Microsoft's C compiler: `cl /std:c11 /W3 /WX`, checked on
x64 (CI) and ARM64 (a Windows 11 VM). Programs are linked with an 8 MiB stack
(`/link /STACK:8388608`), as Linux and macOS give the main thread. Windows' default is 1 MiB, which
`run-pass/deep-recursion.nova` overflows. This is
**best effort**: no CosmoOS profile uses MSVC, and the strict warning set (§1) is checked with clang and
GCC only. What differs:

- **`rt.h`** uses compiler builtins only behind a test for GCC or clang, with portable code otherwise:
  - checked arithmetic falls back to the portable overflow checks, as with `NV_NO_BUILTIN_OVERFLOW`;
  - counting leading zeros (stable vectors) uses `nv_clz64`, a shift loop off GCC and clang;
  - `NV_UNUSED` and `NV_COLD` are empty;
  - `nv_max_align_t` stands for C11's `max_align_t`, which MSVC's `<stddef.h>` lacks in C mode.
    There it is `double`. The generated alignment assertions use it.
- **Process behaviour** (`nova_rt_hosted.c`, under `_WIN32`):
  - standard output and standard error are switched to binary mode at the first write, so
    `"\n"` stays one byte;
  - a panic ends with exit status 134 (`_exit(134)` after flushing), the status `abort()` gives
    on Linux and macOS. Windows' own `abort()` would give 3.
- **I/O** (`nova_rt_io.c`, under `_WIN32`):
  - program arguments work as elsewhere;
  - files use the C runtime's `_open`, `_read`, `_write`, `_close` and `_unlink` in binary mode;
  - `Process.run` uses `_spawnvp(_P_WAIT, …)`;
  - **TCP is not supported**: after the same address check as elsewhere, every socket call fails
    with `ENOSYS` (`IoError.Other`).
- **Test.** `MsvcTests` checks three things:
  - every run-pass and run-fail program builds with MSVC and gives the corpus expectations;
  - `nwc` reads files and standard input;
  - a program runs `cmd /c exit 7` with `Process.run`, sees a missing program fail, and sees TCP
    reported as unsupported.

  No corpus program needs an exception: `io-errors.nova` passes only invalid addresses, which fail
  the address check first. The `test-windows` CI job runs the tests on x64. Locally, a Windows 11
  ARM64 VM (MSVC 19.51) runs them. On other hosts they return early.

## 2. Lowering of types

| Nova | C |
|---|---|
| `i8..i64`, `u8..u64` | `int8_t..int64_t`, `uint8_t..uint64_t` |
| `isize`/`usize` | the fixed-width type of the target's pointer width (`int64_t`/`uint64_t` on LP64), plus `_Static_assert(sizeof(void *) == 8)`. `ptrdiff_t`/`size_t` are used at FFI boundaries (M8) |
| `bool` | `bool` (`_Bool`) |
| `char` | `uint32_t` |
| `f32`/`f64` | `float`/`double` (`_Static_assert(sizeof(double)==8)`); **kernel profile forbids floats** unless the target profile enables them (`cosmoos/profiles.md`) |
| `struct S` | `typedef struct nvT_S nvT_S;` then `struct nvT_S { … }` with fields `f_<name>` in declaration order, structs emitted after the structs they contain. Each struct is followed by `_Static_assert`s on `sizeof`, `_Alignof` and every field's `offsetof`, against `TypeTable.LayoutOf`. An empty struct gets a `uint8_t nv_empty` member (C has no empty structs). `@repr(C)` is the default in v0; field reordering is an M9 opt-in (`@repr(auto)`) |
| `Box<T, A>` with a user allocator (C5, type-system §30) | `T *` as below; `nvB_` calls `A`'s `allocate` method with `{ sizeof(T), _Alignof(T) }` (a `{ 0 }` value of the field-less allocator struct as `this`), and the drop glue and `BoxFree` call `nvBF_<box>`, which calls `deallocate` with the same layout |
| `Box<T, A>` (M3b) | `T *` (a shared reference to a box is `T *const *`). Drop glue `nvD_X<T><A>` destroys `*p`, then calls `nova_free(p, NV_ALLOC_SYSTEM \| NV_ALLOC_FAILING)`. `Box.create`/`createIn` call a per-type helper `nvB_X<T><A>(T v)`: `nova_alloc(sizeof(T), id)`; on NULL it destroys `v` and returns `Err(OutOfMemory)`, otherwise it stores `v` and returns `Ok(p)`. It asserts `_Alignof(T) <= _Alignof(max_align_t)` |
| Deep boxes (M3c) | If destroying `T` ends, as its last step, in a `Box<T>` (followed through struct fields and enum variants' last payloads), the glue of `Box<T>` is a loop: destroy the node except that tail (`next = tail pointer`), `nova_free` the node, continue with `next` (type-system.md §18.5). `Unpack` becomes one assignment per target; `BoxFree` is `nova_free` |
| `Arena` (M4a) | `nv_arena` from `nova/rt.h` (40 bytes, asserted there), created by `nova_arena_create(allocator, chunk)`, dropped by `nova_arena_drop`. A shared reference to a region is a plain `nv_arena *`: allocation mutates the arena through a shared borrow. `alloc`/`allocArray` call per-type helpers `nvAA_<T>`/`nvAR_<T>`, which bump-allocate with `nova_arena_alloc` (chunks come from `nova_alloc`, so they are counted and fault-injectable) and return the scoped result. Element names: `P`/`Q` + T for shared/mutable references, `U` + T for `[T]`, `G<len><Name>` for regions |
| `FixedBufferAllocator`, `ScratchFrame` (M4b) | `nv_fba` (24 bytes) and `nv_scratch` (32 bytes) from `nova/rt.h`. Allocation helpers take `(void *region, uint32_t kind, …)` and call `nova_region_alloc`, which dispatches to the arena, the fixed buffer, or the runtime's scratch stack (heap chunks via `nova_alloc`; a frame's drop frees the chunks newer than its entry state). `Scratch.enter()` is `nova_scratch_enter_root(loc)`, which panics if a frame is live; `frame.enter()` is `nova_scratch_enter_child`. A frame's borrow-checker parts (`memory`, `allocation`) are places of the frame itself in C |
| `Pool<T>`, `PoolBox<T>` (M4c) | `nv_pool` (48 bytes; `nova_pool_create`/`nova_pool_take`/`nova_pool_release`/`nova_pool_drop`; slots of one size, allocated at the first take, with a free list through them). `PoolBox<T>` is `nvPB_<T>` = `struct { T *ptr; nv_pool *pool; }`, and its contents are `(*b.ptr)`. Its drop glue `nvD_Y<T>` destroys `*ptr`, then releases the slot; `BoxFree` of a partially moved pool box releases only. `pool.take` calls `nvPT_<T>(pool, v)`, which destroys `v` when no slot is free |
| Interfaces (M6b) | No runtime representation: dispatch is static. An `impl` block's methods are methods of their type, named like other members (`impl Comparable<i32> for i32` → `nvM_3i32_compare`). Calls through a bound in generic code exist only in generic versions, which are not emitted; each instantiation calls the concrete method |
| Generic code (M6a) | Generic versions are type-, move- and borrow-checked but never emitted. Each instantiation is a separate C type or function. Struct and enum instances are `nvT_`/`nvE_` + `<len><Base><count><args>`, using the element codes (`Pair<i32>` → `nvT_4Pair1i32`; `S4Pair1i32` inside other names). Generic function instances are `nvG_<len><name><count><args>` (`id<Pair<i32>>` → `nvG_2id1S4Pair1i32`). Members of an instance are `nvM_<mangled owner>_<name>`. Every non-generic form starts with a letter or a length, so the forms cannot collide |
| `Result<T, E>` (M3a) | like an enum, named `nvR_<T><E>` with the element codes below (`Result<Res, Code>` → `nvR_S3ResE4Code`, `void` → `v`). Its drop glue is `nvD_R<T><E>`. `e!` on `Err` calls `nova_panic(NV_PANIC_UNWRAP, loc)` |
| `enum E { A, B(T) }` | `struct { uint32_t tag; union { T B; } u; }`. The tag width is the smallest unsigned that fits, unless `@repr(u8)` etc. |
| `T?` (M6c) | like an enum with variants `null` (tag 0) and `Some` (tag 1, field `f__0`), named `nvO_<T>` with the element codes (`i32?` → `nvO_i32`; `O<T>` inside other names); drop glue `nvD_O<T>`. `o!` on `null` calls `nova_panic(NV_PANIC_UNWRAP_NULL, loc)`; `o == null` compares the tag with 0. The niche layout for `Box` and references is future work (type-system.md §22.1) |
| `Vector<T, A>`, `String<A>` (M6d) | `nvV_<[T] or Z><A>` = `struct { T *ptr; uint64_t len; uint64_t cap; }` (24 bytes, asserted), element code `V<U<T> or Z><A>` (`Vector<i32>` → `nvV_Ui32S10SystemHeap`). An empty vector points to the runtime's `nova_vec_empty` (a `max_align_t` object, never dereferenced) with `cap == 0`, so slices of it never do arithmetic on NULL. Growth is `nova_vec_grow` (doubling, at least 4; the old bytes are copied, the old buffer freed; NULL with nothing changed on overflow or allocation failure). Per type: `nvVP_` push (destroys the value on failure), `nvVO_` pop, `nvVC_` clear, `nvVA_` append (strings), `nvVW_` withCapacity; drop glue `nvD_V…` destroys the elements in index order, then frees the buffer if `cap != 0`. The elements place `*v` is the vector itself, so elements are `v.ptr[i]` and views are rebuilt from `ptr` and `len` |
| `SlotMap<T, A>`, `Handle<T>` (M6e) | `nvK_<T><A>` = `struct { nvKS_… *ptr; uint64_t len, cap, count; uint32_t free; }` (40 bytes) with slots `nvKS_…` = `{ uint32_t gen; uint32_t next; T value; }`; element code `K<T><A>`. `Handle<T>` is `nv_handle` = `{ uint32_t index; uint32_t gen; }` from `nova/rt.h` (code `H<T>`), compared with `nova_handle_eq`. Generations are odd while a slot is live; a removal whose generation reaches `UINT32_MAX - 1` retires the slot (never put back on the free list), so generations never wrap. Helpers `nvKI_` insert (grows the slots with `nova_vec_grow`, at most `UINT32_MAX - 1` slots), `nvKV_` validity, `nvKG_`/`nvKM_` get (an optional pointer), `nvKR_` remove; drop glue destroys values in odd-generation slots |
| `StableVector<T, A>` (M6e) | `nvSV_<T><A>` = `struct { void *chunks[NV_SVEC_CHUNKS]; uint64_t len; }` (264 bytes); element code `W<T><A>`. Chunk `k` holds `8 << k` elements and is allocated by `nova_svec_slot` when its first element is pushed; chunks never move or shrink. Element `i` is `(*nvSVE_…(&sv, i))` (`nova_svec_at`: chunk `60 - clz(i + 8)`). A shared reference to a stable vector is a non-const pointer, since `push` mutates through it. Drop glue destroys the elements, then `nova_svec_free` |
| `File`, `TcpListener`, `TcpStream` (C3) | `int32_t` descriptors; drop glue is the runtime's `nova_fd_drop` (close). I/O built-ins call `nova_*` functions declared in `nova/rt.h` and implemented in `runtime/hosted/nova_rt_io.c`; each returns `nv_io_result { value, error }`, and a per-type helper `nvIOR_<Result>` turns it into `Result<X, IoError>` (the runtime maps errno to the variant; `Other` keeps the number). The generated `main` is `int main(int argc, char **argv)` — the one place plain `char` appears — and passes argv to `nova_rt_args` as bytes |
| `StringView` (M6c) | `nv_str` = `struct { const uint8_t *ptr; uint64_t len; }` from `nova/rt.h` (16 bytes); element code `PZ` (`Z` is the unsized `str`). A literal is `((nv_str){ .ptr = (const uint8_t *)"…", .len = n })`, pointing to static storage. `s[lo..hi]` calls `nv_check_str_range` (range, then character boundaries; `NV_PANIC_STR_BOUNDARY`); `==`/`!=` call `nova_str_eq`; `s.bytes()` rebuilds the struct as `nvSl_u8`; printing calls `nova_write_view` |
| `*T`, `*mut T` (M8a) | `T *` for both, `void *` for `*void`: read-only is a Nova rule, and casting `const` away would trip `-Wcast-qual`. Element codes `N<T>` / `M<T>`. Every conversion to or from a pointer goes through `uintptr_t` (`(U *)(uintptr_t)p`), and `add`/`sub`/`offset` compute the address in `uintptr_t`, so no C pointer arithmetic or pointer-to-pointer cast is ever emitted; `p.read()` / `p.write(v)` are the place `(*p)`; `s.ptr` is the slice's `ptr` field; `Slice.fromRaw` builds the slice struct (`unsafe.md` §4) |
| `borrow T` | `const T *`. `borrow mut T` → `T *` (`restrict` only where the borrow checker proves exclusivity *and* M9 enables it). `Ref(k, p)` is `&p`, a deref place is `(*r)`, and reference locals are zero-initialised to `NULL` (never read before assignment) |
| `Slice<T>` / `SliceMut<T>` | `nvSl_<elem>` = `struct { const T *ptr; uint64_t len; }` and `nvSm_<elem>` = `struct { T *ptr; uint64_t len; }` (M2d; size 16, align 8, asserted). Slices are only made from arrays (N ≥ 1), so `ptr` is never NULL; `a[n..]` is the one-past-the-end pointer with length 0. An element is `s.ptr[i]`; a reborrow or `SliceMut`→`Slice` coercion rebuilds the struct from `ptr` and `len` |
| `Box<T, A>` | `T*` when `A` is zero-sized; `struct { T* ptr; A a; }` otherwise |
| Arrays `[T; N]` | `nvA<N>_<elem>` = `struct { T e[N]; }`, so assignment, parameters and return by value work (M2d). An element is `a.e[i]`; `[v; N]` is a `for` loop over `e`; zero-initialised with `{0}`. Element names: integers and `bool` by name, `S<len><Name>` for structs, `E<len><Name>` for enums, `A<N>_<elem>` for arrays (`[[i32; 3]; 2]` → `nvA2_A3_i32`) |
| Function pointers | C function pointer types via typedef |

Names (as implemented in M2a):
- functions: `nv_<name>`;
- struct members: `nvM_<length><Struct>_<name>`, where the length prefix makes the boundary
  unambiguous;
- struct types: `nvT_<name>`;
- fields: `f_<name>`;
- locals: `v_<name>_<id>` and `t_<id>`.

The prefixes keep the namespaces apart and avoid C keywords. Planned: package/namespace
qualification, plus a stable hash of the type arguments for monomorphised instances (not .NET
`GetHashCode`). `@export("sym")` overrides the name.

Arrays and slices are defined with the structs, each after the types it contains by value; slices
come after their element types. Every array and slice type the program uses is emitted, in the
order the checker created it.

Bounds checks (M2d) call `nv_check_index(i, len, loc)` and `nv_check_range(lo, hi, len, loc)`
from `nova/rt.h`; they panic with "index out of bounds" and "slice range out of bounds". A
constant index below a constant length, and a constant range within a constant length, are not
checked.

Layout verification: for every emitted struct the backend emits
`_Static_assert(sizeof(S) == N && _Alignof(S) == A, "...")` and `offsetof`
assertions, where `N`/`A` come from `TargetInfo`. If the C compiler for the actual target
disagrees with Nova's model, the build fails, and nothing miscompiles silently.

## 2.1 C names (M7d)

Generated code and the runtime share one C namespace, so their names are kept disjoint by
prefix:

| Owner | Names |
|---|---|
| **Runtime** (`nova/rt.h`, `nova_rt_*.c`) | `nova_*`; `nv_` followed by a lowercase letter (`nv_add_i32`, `nv_check_index`, `nv_str`, `nv_loc`); `NV_*` |
| **Generated** | `nv` followed by a capital letter: `nvF_name` (top-level functions), `nvM_` (members), `nvG_` (generic instances), `nvT_`/`nvE_` (types), and the helper families (`nvD_`, `nvB_`, `nvVP_`, …); locals `v_<name>_<n>` and `t_<n>`; fields `f_<name>`; variants `v_<name>`; labels `bb<n>`; files `nv_file_<n>` |

Until M7d, top-level functions were `nv_<name>`. A Nova function named `add_i32` then collided with
the runtime's `nv_add_i32`, and the C failed to compile. The random program generator found this,
and `run-pass/runtime-names.nova` keeps it fixed.

## 2.2 Stack-placed boxes (M5; `--placement`)

When `CEmitOptions.Placement` is on, `PlacementAnalysis` decides each `Box.create` site
(`memory-model.md` §5.1).
- **Slot.** A stack-placed site has a slot `NV_UNUSED T nv_box_<n> = {0};` among the function's
  locals.
- **Creation.** `R = nvB_…(v)` becomes `nv_box_<n> = v;` followed by
  `R = ((Result){ .tag = 0, .u.v_Ok = { .f__0 = &nv_box_<n> } });`. Nothing is allocated, so
  nothing can fail.
- **Destruction.**
  - A `Drop` of a box local of the site calls the contents' drop glue `nvD_T(b)` if `T` needs drop,
    and nothing else. A drop flag still guards it.
  - A `BoxFree` of one emits nothing.

Everything else is unchanged. Heap boxes, and every other site, use the drop glue of §2.

## 2.3 C interoperability (M8b; type-system.md §28)

- **Extern functions.** Each `extern "C"` function gets a prototype with the C types of §28.3
  (`size_t strlen(const char *);`) and a `static` wrapper `nvC_<name>` with Nova's own C types,
  which converts the arguments (`(size_t)n`; a `T *` converts to `const T *` implicitly), calls the
  function and converts the result back (a `const T *` through `uintptr_t`; a `@repr` enum through
  a check that panics with `NV_PANIC_FFI_ENUM`). Nova code calls the wrapper like any function. The
  prototypes use C's own types so that they are compatible with the C library's declarations: the
  generated code includes no C library header (unless a `@header` names one), but clang and GCC know the library functions'
  types (`-Wincompatible-library-redeclaration`, `-Wbuiltin-declaration-mismatch`), and C11
  6.5.2.2 makes a call through an incompatible type undefined.
- **Exports.** Each `@export` function gets a non-`static` wrapper under its symbol, with the C
  types, that converts the arguments (checking `@repr` enums), calls `nvF_<name>`, and converts the
  result. Nothing else in the generated code has external linkage except `main`.
- **`@repr(C)` structs** are `struct S` with members named as in Nova (other structs are `struct
  nvT_S` with members `f_<name>`), and `typedef struct S nvT_S;`, so generated code uses them like
  any struct, and C code that includes `nova_exports.h` sees the same type (C11 6.2.7 makes struct
  types in two translation units compatible only if their tags and members match).
- **`@repr(uN)` enums** keep the internal form `struct { uintN_t tag; }` (the tag has the repr's
  width and signedness); wrappers convert to and from the integer.
- **`nova_exports.h`**: an include guard; `<stdbool.h>`, `<stddef.h>`, `<stdint.h>`; each `@repr(C)`
  struct (after the structs it contains); each `@repr` enum as `typedef <int> E;` and constants
  `E_Variant`; each export's prototype. It is generated only when there is something to declare.
- **Imported C types (C6b, type-system.md §31)**: `main.c` and `nova_exports.h` `#include "…"` each
  `@header` after `nova/rt.h`, once, in declaration order; they are the only headers generated code
  includes besides the runtime's. An imported type has no definition, typedef or layout assertion in
  generated code: it is its C spelling (`struct name` or the `@ctype` name), and a field place is a
  member access. Pointer, `usize` and `isize` fields convert through `uintptr_t` or a cast when read
  and written, as wrappers do. For each declared field, two `_Static_assert`s (on `_Generic` of the
  member, with the field's C type, and on its `sizeof`) make a header that disagrees a C compile error
  that names the struct, the field and the Nova declaration's file and line.
- **C sources** given to `novac build` are copied unchanged to `c/<file>` beside `main.c` and
  compiled with it (`-I` names the directory, so `#include "nova_exports.h"` works).
- **Function pointers (M8d, §29.5)** are `typedef R (*nvP_<code>)(A, …);` with the C types of
  §28.3 (element code `F<n><parameters><result>`), declared after the struct names and before the
  structs. A value is the extern function's name or the export's symbol (exports are declared
  before every function); a call converts its arguments and result like an extern wrapper.
  `nova_exports.h` declares the struct names first, then the function pointer types.
- **`lib/core` (M8d, §29.2)** is compiled with every package, but only the functions that emitted
  code calls, the types it mentions, and their files' names are emitted, so a program that uses
  none of it gets the same C as before.
- **Volatile accesses** (§29.1) are `(*(volatile T *)p)`; `@align(n)` (§29.4) is `_Alignas(n)` on a
  struct's first member; `Cpu.spinHint()` and the target queries are `nova/rt.h` inlines.

## 3. Lowering of control flow and cleanup

**Flat form** (M1; `--flat-c`): each NIR block becomes a C label. Terminators become `goto`, `switch`
(SwitchInt) or `return`. It is the reference form: simple enough to check by reading.

**Structured form** (M7b; the default). It is a readability pass and adds nothing that
correctness depends on. One invariant makes it safe: **every block's code ends in an explicit
transfer**: `goto`, `break`, `continue`, `return`, or a `_Noreturn` call. Code therefore never
falls into the next block by accident. Where a block is placed changes only how the code reads,
and a transfer may be dropped only where falling through provably reaches the same label.

1. **Analysis.**
   - Reverse postorder, dominators (Cooper–Harvey–Kennedy), and *forward predecessors*: in-edges
     that are not back edges, counted with multiplicity, so a switch with two cases to one block
     counts twice.
   - A *back edge* `u → h` is one where `h` dominates `u`. A retreating edge that is not a back
     edge makes the graph irreducible. The function is then emitted flat. Nova source has no
     `goto`, so this is not expected; it is a fallback.
   - The natural loop of a header `h` is `h` plus every block that reaches a back-edge source
     without passing through `h`.
2. **Placement.** Each block's code is emitted exactly once, inside the code of its immediate
   dominator `d`, in one of three ways:
   - **Inline**, at the branch to it: when it has one forward predecessor and leaves no loop that
     contains `d`.
   - **Exit follow**: when it leaves loops. Let `h` be the outermost loop header whose loop
     contains `d` but not the block. The block is emitted after `h`'s loop statement. One
     exception keeps early returns in place: a block that leaves a loop from a block other than
     the header itself is inlined if it has one forward predecessor and its dominator subtree is
     *closed* and small. Closed means no edge leaves the subtree; small means at most 2 blocks
     and 8 statements.
   - **Follow**: otherwise (a merge point). It is emitted after `d`'s own code, inside the same
     loops. Follows and exit follows are emitted in reverse postorder.
3. **Shapes.**
   - A loop header's code (with its inline and in-loop placements) becomes `for (;;) { … }`.
   - `NirBranch` becomes `if (c) { … } else { … }`, and `NirSwitch` becomes `switch` with one
     `case` body per target.
   - A branch to an inline block emits that block's code there. Every other branch is first
     emitted as `goto bbN;`.
4. **Transfer elimination**, in tree order and repeated to a fixpoint, since removing a `goto`
   can lengthen what falls through before it:
   - A `goto L` is removed when falling through reaches `L`. Falling through means passing over
     only labels, the end of an `if` arm, or the end of a loop body back to its own header's
     label.
   - Otherwise it becomes `continue` if `L` is the innermost loop's header.
   - Otherwise it becomes `break` if `L` is what falls through after the innermost loop, unless
     a `switch` lies in between.
   - Otherwise it becomes `break` if `L` falls through after the innermost `switch`.
   - The end of a `case` body never falls through: its transfer is kept, so no `case` falls into
     the next (`-Wimplicit-fallthrough`).
5. **Labels** that no remaining `goto` names are removed (`-Wunused-label`). A label at the end of
   a block is written `bbN: ;`.
6. **Guard clauses.**
   - An `if` arm that cannot complete normally is hoisted: `if (c) { A } else { B }` with `A`
     ending in a transfer becomes `if (c) { A } B`. Symmetrically, when only `B` ends in a
     transfer it becomes `if (!c) { B } A`. When both arms end in a transfer, the shorter arm is
     the guarded one.
   - An empty `then` arm is negated away.
   - An `else` holding a single `if` is printed as `else if`.

The run corpus is built in both forms under every C configuration: structured by default, flat as
the oracle in a `clang -O2 flat` configuration. Both must agree.

Shape of `gcd` (structured):

```c
    v_x_2 = v_a_0;
    v_y_3 = v_b_1;
    for (;;) {
        t_5 = (v_y_3 != ((uint64_t)0));
        if (!t_5) {
            break;
        }
        v_t_4 = nv_rem_u64(v_x_2, v_y_3, NV_LOC(nv_file_0, 5u, 17u));
        v_x_2 = v_y_3;
        v_y_3 = v_t_4;
    }
    t_6 = v_x_2;
    return t_6;
```

Flat form of the same function (M1), shown in an earlier, illustrative notation:

```c
int32_t nv_demo_gcd(uint64_t a, uint64_t b) {
    uint64_t x; uint64_t y; uint64_t t;
bb0:
    x = a; y = b;
    goto bb1;
bb1:
    if (!(y != 0u)) goto bb3;
    goto bb2;
bb2:
    if (y == 0u) nova_panic_div0(NV_SRC(3, 17));   /* inserted check (statically elidable later) */
    t = x % y; x = y; y = t;
    goto bb1;
bb3:
    return x;
}
```

Cleanup: drop elaboration has already put `Drop` calls on every exit edge,
so early `return`, `?` and `break` need no special backend support. Drop flags are
`bool` locals initialised to `false`. No `setjmp/longjmp` and no C++.

Panics are `noreturn` calls into the runtime (`nova_panic(kind, src)`), so destructors are
not run (panic = abort, OD-5).

## 4. Evaluation order and temporaries

NIR has already sequenced every side effect into separate statements, so the C
backend never emits an expression whose meaning depends on unspecified C
evaluation order (function-argument order, `a[i] = i++`-style). Arguments
with side effects are always assigned to temporaries first.

## 5. UB avoidance checklist (enforced by construction + tests)

| Hazard | Strategy |
|---|---|
| Signed overflow | `nova_add_i32_chk(a,b,src)` via `__builtin_add_overflow`, with a portable fallback that computes in unsigned and compares. Wrapping ops compute in unsigned and convert back through a well-defined helper (implementation-defined conversion avoided by `memcpy`-free bit tricks verified per compiler) |
| Division | Check for zero and `MIN/-1` before `/` and `%` |
| Shifts | Check `0 <= n < width`. Left-shifting a negative signed value is done in unsigned |
| Unsigned promotions | `uint8_t`/`uint16_t` arithmetic is promoted to `int`. All narrow arithmetic is performed as `uint32_t` and truncated explicitly |
| Pointer arithmetic | Never form out-of-range pointers. Bounds checks compare indices before computing `ptr + i`. Raw-pointer arithmetic (M8a) is done on `uintptr_t`, and raw pointers have no ordering comparisons (`unsafe.md` §4) |
| Strict aliasing | No type punning. Reinterpretation (`transmute`, unsafe only) goes through `memcpy` (a builtin; supplied by the runtime in freestanding builds) |
| Uninitialised reads | Definite-init analysis forbids them. Locals of aggregate type that are partially written via fields before a full read are zero-initialised in C to avoid indeterminate padding being copied |
| Null | References are never null (type system). `T?` niche checks are explicit |
| Alignment | All accesses are through properly typed lvalues. `@packed` fields are accessed via `memcpy` helpers |
| Lifetime of compound literals / temporaries | Not used across statements. Temporaries are named locals |
| `memcpy` overlap | Moves use `memmove` when the source and destination may alias; NIR knows when they cannot |
| Float→int conversion out of range (UB in C) | Only via `toIntSaturating`/`checkedCast` helpers that range-check first |
| Warnings on valid programs (M7d) | Nova accepts code that C compilers warn about, and generated C must still be warning-free. `x = x;` emits nothing (`-Wself-assign`). A comparison of a place with itself is the constant `true`/`false` (`-Wtautological-compare`). So is an ordering comparison against the type's own minimum or maximum, such as `u < 0` or `x <= MAX` (GCC's `-Wtype-limits`, found in Linux CI by `FuzzTests`). A constant index past a fixed array's end, after the check that always panics, goes through `nv_opaque_index` (`-Warray-bounds`). So does a constant slice or vector index too large for any object (index × element size > 2⁶³ − 1), found by the M7e campaign (seeds 1009815, 1014252, 1016777). In the structured form, statements that print nothing are dropped, so no label ends a block (`-Wc23-extensions`). Found by the random program generator; each case is in `run-pass/warning-free.nova` or `run-fail/constant-index-past-end.nova` |
| Infinite side-effect-free loops (C11 6.8.5p6 permits assuming termination) | Loops whose controlling expression is not a constant expression and that have no side effects get a `nova_loop_barrier()` (an empty `asm volatile` / volatile access) |

## 6. Runtime surface (`nova/rt.h`)

Heap (M3b):
- `nova_alloc(size, allocator)` and `nova_free(p, allocator)` go through the platform's
  `nova_platform_alloc`/`nova_platform_free` (hosted: `malloc`/`free`, which also serve
  CosmoOS userland). They count attempts and live allocations. `nova_fail_after`/`nova_fail_reset`
  implement `FailingAllocator`.
- The generated `main` calls `nova_rt_start()` (reads `NOVA_FAIL_ALLOC_AT`, `NOVA_ALLOC_REPORT`
  via `nova_platform_config`) and returns through `nova_rt_finish(code)`, which prints the
  allocation report if asked. `Process.exit` and panics skip the report.


- `nova_panic(kind, src)`: `noreturn`. Provided by the profile: hosted (write to
  stderr + `abort()`), CosmoOS userland (same, through CosmoOS libc), kernel
  (calls the kernel panic routine; C5).
- Checked-arithmetic helpers (`static inline`).
- `nova_mem_copy/move/set` (map to `__builtin_memcpy` etc. hosted; to the
  kernel's own routines in the kernel profile).
- Allocator vtable ABI for dynamic allocators (`memory-model.md` §3.2).
- Source location encoding `NV_SRC(line, col)`. File IDs are resolved through a
  per-package table, so absolute host paths are never embedded (reproducibility).

## 7. Debugging and source maps (M7a)

- **`#line` directives** map generated C back to Nova source. They are off by default and turned
  on by `--line-directives`; `novac build -g` turns them on and compiles the C with `-g -O0`
  instead of `-O2`.
- With directives on, each function body maps to Nova lines:
  - the function's opening line maps to the line of its name;
  - each statement and terminator maps to the first line of the source construct it came from
    (its span). A directive is written only when the line or file changes. Code without a span of
    its own, such as compiler temporaries, keeps the line before it.
  - A block's label shares the line of the block's first statement, so labels need no directive
    of their own. The local declarations share one line, attributed to the function's name.
  - After each function, `#line <n> "main.c"` hands the following lines back to the C file.
    Everything outside function bodies (types, drop glue, helpers, `main`) is attributed to
    `main.c`.
- **Paths.** Directives and panic locations name a source by its *source path*. This is the path
  given to the compiler, with `/` separators. The CLI rewrites an absolute path under the current
  directory to a relative one. Any other absolute path is reduced to its file name. Generated C
  therefore never contains a host path (`build-integration.md` §6).
- `novac build` runs the C compiler with the current directory as its working directory. The
  debug info's compilation directory then makes the source paths resolve, so a debugger finds
  the `.nova` files.
- Locals keep stable names: `v_<name>_<n>` for a named local, `t_<n>` for a temporary.
  A C debugger therefore shows recognisable variables (`v_count_3`).
- Directives never change the code itself. A program with directives must behave exactly like
  one without them. The run corpus is built both ways (§8).

## 7a. Implementation notes (M1, increment 1c)

- Every Nova function becomes `static NV_UNUSED` with a prototype. Nova allows unused functions,
  parameters and locals, and `-Wunused-*` must stay clean under `-Werror`.
- Locals are declared at the top of the function and zero-initialised (scalars only in M1). Only
  blocks that are jump targets get a label.
- Runtime files:
  - `nova/rt.h`: freestanding checked helpers `nv_add_i32`, `nv_div_u64`, `nv_shl_u8`,
    `nv_shift_amount_s/u`, `nv_as_iN`, …;
  - `nova_rt_core.c`: output formatting and the panic message, with no libc;
  - `nova_rt_hosted.c`: the platform layer over stdio.

  `novac build --emit c` writes all of them next to `main.c`. Build with
  `cc -I <dir> <dir>/*.c`.
- Panic output: `nova panic: <reason> at <file>:<line>:<column>`. Stdout is flushed first, then
  `abort()` (exit status 134).
- `main() -> i32` becomes `int main(void) { return (int)nvF_main(); }` (M7d renamed `nv_` to `nvF_`, §2.1). `Process.exit(c)` calls
  `exit(c)`, which flushes stdio.

## 8. Validation

- Golden-file tests for representative C output (mainly to review diffs).
- Every `run-pass` test is compiled with clang and gcc (host) at `-O0` and `-O2`,
  with and without `-fsanitize=address,undefined`. Outputs must agree
  (differential testing).
- M7a: every run-corpus program is also built with `#line` directives (clang `-O0 -g`) and must
  produce the same results. `SourceMapTests` compiles programs with `-g` and checks the
  debug line table: it names the `.nova` file and the lines of the statements.
- M7: a Csmith-style random program generator for Nova; three-way
  differential testing (clang/gcc × opt levels), plus a reference
  interpreter on NIR (`novac run --interp`) as an oracle (`milestone-7.md`).
