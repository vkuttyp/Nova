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
- Plain C `char` is **never** used for Nova data (`char` is signed on x86-64 and
  unsigned on AArch64; CosmoOS's `limits.h` is also wrong about it on AArch64). Bytes are `uint8_t`.

## 2. Lowering of types

| Nova | C |
|---|---|
| `i8..i64`, `u8..u64` | `int8_t..int64_t`, `uint8_t..uint64_t` |
| `isize`/`usize` | the fixed-width type of the target's pointer width (`int64_t`/`uint64_t` on LP64), plus `_Static_assert(sizeof(void *) == 8)`. `ptrdiff_t`/`size_t` are used at FFI boundaries (M8) |
| `bool` | `bool` (`_Bool`) |
| `char` | `uint32_t` |
| `f32`/`f64` | `float`/`double` (`_Static_assert(sizeof(double)==8)`); **kernel profile forbids floats** unless the target profile enables them (`cosmoos/profiles.md`) |
| `struct S` | `typedef struct nvT_S nvT_S;` then `struct nvT_S { … }` with fields `f_<name>` in declaration order, structs emitted after the structs they contain. Each struct is followed by `_Static_assert`s on `sizeof`, `_Alignof` and every field's `offsetof`, against `TypeTable.LayoutOf`. An empty struct gets a `uint8_t nv_empty` member (C has no empty structs). `@repr(C)` is the default in v0; field reordering is an M9 opt-in (`@repr(auto)`) |
| `Result<T, E>` (M3a) | like an enum, named `nvR_<T><E>` with the element codes below (`Result<Res, Code>` → `nvR_S3ResE4Code`, `void` → `v`). Its drop glue is `nvD_R<T><E>`. `e!` on `Err` calls `nova_panic(NV_PANIC_UNWRAP, loc)` |
| `enum E { A, B(T) }` | `struct { uint32_t tag; union { T B; } u; }`. The tag width is the smallest unsigned that fits, unless `@repr(u8)` etc. |
| `T?` | `T*`-niche for `Box`/references, else `struct { bool has; T value; }` |
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

## 3. Lowering of control flow and cleanup

Each NIR block becomes a C label. Terminators become `goto`, `switch`
(SwitchInt) or `return`. Structured reconstruction (`if/while`) is an optional
readability pass (M7), not needed for correctness.

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
| Pointer arithmetic | Never form out-of-range pointers. Bounds checks compare indices before computing `ptr + i` |
| Strict aliasing | No type punning. Reinterpretation (`transmute`, unsafe only) goes through `memcpy` (a builtin; supplied by the runtime in freestanding builds) |
| Uninitialised reads | Definite-init analysis forbids them. Locals of aggregate type that are partially written via fields before a full read are zero-initialised in C to avoid indeterminate padding being copied |
| Null | References are never null (type system). `T?` niche checks are explicit |
| Alignment | All accesses are through properly typed lvalues. `@packed` fields are accessed via `memcpy` helpers |
| Lifetime of compound literals / temporaries | Not used across statements. Temporaries are named locals |
| `memcpy` overlap | Moves use `memmove` when the source and destination may alias; NIR knows when they cannot |
| Float→int conversion out of range (UB in C) | Only via `toIntSaturating`/`checkedCast` helpers that range-check first |
| Infinite side-effect-free loops (C11 6.8.5p6 permits assuming termination) | Loops whose controlling expression is not a constant expression and that have no side effects get a `nova_loop_barrier()` (an empty `asm volatile` / volatile access) |

## 6. Runtime surface (`nova/rt.h`)

- `nova_panic(kind, src)`: `noreturn`. Provided by the profile: hosted (write to
  stderr + `abort()`), CosmoOS userland (same, through CosmoOS libc), kernel
  (calls the kernel panic routine; C5).
- Checked-arithmetic helpers (`static inline`).
- `nova_mem_copy/move/set` (map to `__builtin_memcpy` etc. hosted; to the
  kernel's own routines in the kernel profile).
- Allocator vtable ABI for dynamic allocators (`memory-model.md` §3.2).
- Source location encoding `NV_SRC(line, col)`. File IDs are resolved through a
  per-package table, so absolute host paths are never embedded (reproducibility).

## 7. Debugging

- `#line` directives map C back to Nova source (optional, `--line-directives`;
  on by default for debug builds). They must use package-relative paths.
- Stable local names (`v_<name>_<n>`) so a C debugger shows recognisable variables.

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
- `main() -> i32` becomes `int main(void) { return (int)nv_main(); }`. `Process.exit(c)` calls
  `exit(c)`, which flushes stdio.

## 8. Validation

- Golden-file tests for representative C output (mainly to review diffs).
- Every `run-pass` test is compiled with clang and gcc (host) at `-O0` and `-O2`,
  with and without `-fsanitize=address,undefined`. Outputs must agree
  (differential testing).
- M7: a Csmith-style random program generator for Nova; three-way
  differential testing (clang/gcc × opt levels), plus a reference
  interpreter on NIR (`novac run --interp`) as an oracle.
