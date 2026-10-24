# ADR 0007 — M8: hybrid core library, raw pointers, unsafe contexts

- Status: **Accepted** (project owner, 2026-10-24, when starting M8: "Hybrid" for the core
  library). Decisions 2–5 are the agent's defaults under that instruction, following the pattern of
  ADR 0004; the owner may revise them.
- Scope: how M8 provides the core library, raw pointers and unsafe code (`milestone-8.md`). FFI,
  profiles and MMIO details are specified with their increments (8b–8d).

## Context

Master prompt Milestone 8 asks for a core library, freestanding compilation, a C FFI, raw-pointer
unsafe operations, custom entry points and target capabilities. Every library type today (Option,
Result, Box, Vector, Console, File, …) is built into the compiler and the C runtime (ADR 0003,
0005); Nova has no library source files. The grammar reserves `unsafe` and `extern`, and
`ownership.md` U1–U3 sketch the rules, but nothing is specified or implemented.

## Decision

1. **Core library: hybrid** (owner). The existing built-in types stay built in. The "split" is
   a per-profile capability table (hosted-only names such as `Console`, `File` and `SystemHeap` are
   rejected in freestanding code) plus the runtime split into a freestanding core and a hosted part.
   New systems types that unsafe Nova can express (`Volatile<T>`, `MmioPtr<T>`, `PhysAddr`, memory
   helpers) are written in Nova, in a `lib/core` bundled with the compiler (8c, 8d).
   *Rejected:* everything built in (`lib/core` would stay empty, and safe wrappers over unsafe code
   would be exercised only by tests); porting the M3–M6 built-ins to Nova (a large rewrite with
   regression risk, not needed for C4).
2. **Raw pointers are `*T` and `*mut T`**, Copy, with no loans (U3). Making and converting them is
   safe; using them is unsafe. Conversions use `as`, so no new operator is needed; the operations
   are methods (`p.read()`, `p.write(v)`, `p.add(n)`) and `Ptr.*` / `Slice.fromRaw` built-ins.
   There is no `*p` dereference operator, since Nova has no prefix dereference at all (references
   and boxes auto-dereference). *Rejected:* a `*p` place operator (new syntax, and `*` is already
   multiplication); `null` as a pointer literal (`null` is the optional's empty value, §22.1); the null pointer is
   `0 as *T`.
3. **Pointer reads and writes need a Copy pointee** in v0. Moving a value out of or into raw memory
   would need the drop elaborator to track raw places. It can come later, with the allocator
   interface.
4. **Unsafe contexts** are `unsafe { }` blocks, `unsafe { e }` expressions (Ex17: `let n =
   unsafe { write(…) };`, since Nova has no block expressions) and `unsafe fn` bodies. The `Safety:`
   doc comment (U2) is an **error** when missing, not a warning: Nova has no warnings, and the rule
   is cheap to satisfy.
5. **Generated C has no undefined pointer arithmetic**: raw pointers are plain `T *` (no `const`),
   and arithmetic and casts go through `uintptr_t` (`unsafe.md` §4).

6. **FFI (8b)**:
   - Extern declarations name C types exactly, through the mapping of `type-system.md` §28.3
     (`usize` is `size_t`, `*T` is `const T *`, and the pointee-only `CChar` is `char`), and calls go
     through generated wrappers. Calling a C function through an incompatible prototype is undefined
     in C11, and clang and GCC reject redeclared library functions with other types.
   - `@export` alone makes a function C-callable. Ex17's `public extern "C" fn` spelling is not
     used: the attribute already says everything, and an `extern` definition would read like a
     declaration.
   - `@repr(C)` structs keep their Nova names in C, so C code using `nova_exports.h` has the same
     struct type. Arrays and other non-FFI-safe types are not allowed in them yet.
   - The interpreter models a few C library functions and faults on any other C call.
   - Function pointers move to 8d: a C callback needs a C-typed wrapper, which exports provide.
7. **Systems capabilities (8d)**:
   - `lib/core` is Nova source embedded in `novac` and part of every package; unused parts are
     not emitted, so programs that do not use it compile to the same C as before.
   - `Volatile<T>` is a cell written through `borrow mut this`, not an interior-mutable cell: writing a
     register needs exclusive access to the register block, which `MmioPtr<T>.getMut()` gives.
   - Only C function pointers exist (`extern "C" fn(…)`); their values are extern and exported
     functions, whose C addresses already exist. Calling one is unsafe.
   - `Target.isX86_64()` and friends are decided by the C compiler, because one generated C file
     serves every LP64 target; there is no conditional compilation in Nova yet.

## Consequences

- `ownership.md` U3's `borrowFromRaw(p)` is spelled `Ptr.toBorrow(p)` / `Ptr.toBorrowMut(p)` (`borrow` is a
  keyword, so `Ptr.borrow` would not parse).
- The interpreter becomes the dynamic oracle for unsafe code too (null, dead, out-of-bounds,
  misaligned, uninitialised accesses), but it does not track loans.
- M5's rule 1c (address exposed to unsafe) is met conservatively: a function that turns references
  into pointers keeps its boxes on the heap.
- Pointee types excluded type parameters in 8a. 8d lifts this (`lib/core`'s `Volatile<T>` needs
  `*T`); a scoped type argument for such a `T` is N0274, as for boxes.
