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

## Consequences

- `ownership.md` U3's `borrowFromRaw(p)` is spelled `Ptr.toBorrow(p)` / `Ptr.toBorrowMut(p)` (`borrow` is a
  keyword, so `Ptr.borrow` would not parse).
- The interpreter becomes the dynamic oracle for unsafe code too (null, dead, out-of-bounds,
  misaligned, uninitialised accesses), but it does not track loans.
- M5's rule 1c (address exposed to unsafe) is met conservatively: a function that turns references
  into pointers keeps its boxes on the heap.
- Pointee types exclude type parameters in v0, so generic code cannot make raw pointers to `T`.
  Generic allocators (memory-model §3.2) will need this lifted.
