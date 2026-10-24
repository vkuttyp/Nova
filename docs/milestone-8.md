# Milestone 8 — Systems and freestanding support (plan and acceptance)

Status: **in progress** (started 2026-10-24; 8a and 8b done). Decisions: ADR 0007. Normative: `unsafe.md` (the
unsafe contract), `type-system.md` §27 (raw pointers and unsafe), and later sections named per
increment. Each increment writes its spec first, then the code and tests.

Master prompt §14, §15 and Milestone 8 ask for: a core library, freestanding compilation, a C FFI,
raw-pointer unsafe operations, custom entry points and target-specific capabilities. C4 (a
freestanding Nova object inside CosmoOS's rules) builds on all of it.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| **8a** | **Unsafe and raw pointers**: `unsafe { }` blocks and `unsafe { e }` expressions, `unsafe fn`, the `*T` / `*mut T` types, pointer casts, pointer methods and `Ptr.*` / `Slice.fromRaw` (read, write, arithmetic, null, references and slices from raw parts), slice `.ptr`; the U1 capability check and the U2 `/// Safety:` rule; C lowering without undefined pointer arithmetic; interpreter checks (null, dead, out of bounds, misaligned, uninitialised) | `unsafe.md`, type-system §27, grammar | **done 2026-10-24** |
| **8b** | **C FFI**: attributes, `extern "C"` imports, `@export` C-callable functions, `@repr(C)` structs and `@repr(uN)` enums, `CChar`, `nova_exports.h`, linking C sources into a build; safe wrappers over libc (Ex17); the interpreter's model of the C library | type-system §28, c-backend §2.3 | **done 2026-10-24** |
| 8c | **Profiles and the core library**: `--profile hosted\|freestanding`, the per-profile capability table (hosted-only built-ins rejected in freestanding code), the runtime split (core with user-provided hooks, hosted), `--emit obj`, the bundled `lib/core` Nova sources; a freestanding object whose undefined symbols are an allowlist (`llvm-nm -u`) | `profiles.md` §1, §4 (new), c-backend §1 | planned |
| 8d | **Entry points, callbacks, MMIO and target capabilities**: custom entry points (exported entry functions, no generated `main`), C function pointers (moved from 8b, ADR 0007), `Volatile<T>` / `MmioPtr<T>` / `PhysAddr` / `VirtAddr` in `lib/core`, `@align`, `Cpu.spinHint()`, target constants (Ex18) | type-system §29 | planned |
| 8e | **Acceptance**: a freestanding demo object for the host and both CosmoOS triples (`x86_64-unknown-none-elf`, `aarch64-unknown-none-elf`): the `llvm-nm -u` allowlist, no `.init_array`, TLS, `.eh_frame` or COMMON symbols; docs | this file | planned |

## Acceptance criteria (`roadmap.md`)

1. A freestanding Nova object with no libc: `llvm-nm -u` lists only allowlisted symbols (the
   memory functions and the profile's hooks), on the host and on both CosmoOS triples.
2. Unsafe is contained: every operation of U1 outside an unsafe context is rejected (compile-fail
   corpus, two tests per operation at least); `unsafe fn` and safe functions containing `unsafe`
   without a `/// Safety:` comment are rejected; safe code is checked exactly as before (the whole
   corpus is unchanged).
3. Raw-pointer programs are UB-free: they run under ASan and UBSan, and the interpreter agrees with
   native output on every one; deliberately broken unsafe programs are interpreter faults.
4. C FFI: Ex17's running form (`run-pass/ffi-libc.nova`, safe wrappers over the C library) runs, and C
   code calls an `@export` Nova function;
   `@repr(C)` layouts equal the C compiler's (`_Static_assert` on size, alignment and offsets).
5. Profiles: hosted-only names are rejected in freestanding code; the run corpus is unchanged in
   the hosted profile.
6. Docs: `unsafe.md`, `type-system.md`, `grammar.md`, `c-backend.md`, `profiles.md`,
   `intermediate-representation.md`, `diagnostics.md`, `roadmap.md`, `risks.md` (R-1, R-3, R-10),
   `testing.md`.

## Results by increment

| Increment | Evidence (run 2026-10-24) |
|---|---|
| 8a | Spec: `unsafe.md` (new, normative), `type-system.md` §27, grammar (doc comments, `unsafe fn`, `unsafe` blocks and expressions, `*T`/`*mut T`), ADR 0007, diagnostics N0280–N0283. Compile-fail corpus: `check-fail/unsafe-capabilities.nova` (every U1 operation outside an unsafe context, two or more cases each, and where a block or expression ends; plus the safe operations, which are not errors), `raw-pointer-types.nova` (pointees, operations, casts, comparisons), `unsafe-safety-comments.nova` (the Safety rule, `////`, interrupted doc comments, unsafe `main`, interface and `impl` methods). Run corpus: `run-pass/raw-pointers.nova` and `raw-slices.nova` (arithmetic in both directions, struct pointees, references and slices from raw parts, a safe wrapper, pointer casts and equality, null, pointers in arrays and to pointers) pass in every C configuration, including ASan+UBSan and the strict warning set with `-Wcast-qual`, and in the interpreter. `UnsafeTests`: invalid accesses through pointers are interpreter faults (ended memory, out of bounds, misaligned, null, a write to static memory, a slice from raw parts past its array). Placement rule 1c: `placement/address-exposed.nova`. Parse snapshot `parse-pass/unsafe-and-pointers.ast` reviewed. The rest of the corpus is unchanged (1,880 tests pass; no existing snapshot changed). Two spellings changed from the first draft of the spec, because the words are keywords: `Ptr.borrow` is `Ptr.toBorrow`, and `Ptr.null<T>()` became `0 as *T`. Found while testing: `s.ptr` moved a `SliceMut` instead of reborrowing it. Not done: move-type pointees (ADR 0007 decision 3), pointers to type parameters, borrow tracking in the interpreter |
| 8b | Spec: `type-system.md` §28, c-backend §2.3, grammar (attributes, `extern` blocks), `intermediate-representation.md` §5.4 (the interpreter's C model), ADR 0007 decision 6, Ex17 updated, diagnostics N0290–N0292. Extern prototypes use C's own types (`size_t`, `T const *`, `char` through the pointee-only `CChar`), so they are compatible with the C library's declarations, and calls go through generated wrappers. `@repr(C)` structs keep their Nova names and members in C. Compile-fail corpus: `check-fail/ffi-declarations.nova` (every N0290/N0291/N0292 case, duplicate C names, calls outside `unsafe`, `CChar` as a value, reads through `*CChar`). `ffi/exports.nova` with its C half `exports.c` (`FfiTests`): C calls exports taking and returning `@repr(C)` structs, `@repr(u8)` and `@repr(i32)` enums, `bool`, `isize`/`usize` and pointers; Nova calls C with pointers to structs and an out-parameter; a bad enum value from C panics. It passes under every C configuration, ASan+UBSan included, and its `nova_exports.h` is a reviewed snapshot. `run-pass/ffi-libc.nova` (safe wrappers over `strlen`, `puts`, `abs`, `memcmp`, `memcpy`, `memset`) runs in every configuration, in the interpreter's model of the C library, and in the guests. The interpreter faults on any other C call (`FfiTests`). Parse snapshot `parse-pass/attributes-and-extern.ast` reviewed. 1,897 tests pass. Not done: C function pointers (moved to 8d), arrays and `@repr` enums inside `@repr(C)` structs, enums with explicit values, C's `long` (no Nova type matches it on every target) |
