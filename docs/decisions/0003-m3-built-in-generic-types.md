# ADR 0003 — M3 heap types are built in, ahead of general generics

- Status: **Accepted** (project owner, 2026-10-22, when starting M3: "Built-in types")
- Scope: how M3 provides `Result`, `Box` and allocators before generics (M6), interfaces
  (M6) and raw pointers / `unsafe` (M8) exist.

## Context

The M3 acceptance examples (`language-spec.md` Ex4, Ex5, Ex11, Ex14) use `Result<T, E>`,
`Box<T, A>`, `T?`, a generic function with an interface bound (`createUser<A: Allocator>`),
`StringView` and integer methods such as `checkedMul`. The `Allocator` interface in
`memory-model.md` §3.2 uses `unsafe fn` and `*mut u8`. Generics and interfaces are scheduled
for M6 and raw pointers for M8 (`roadmap.md`).

## Decision

1. `Result<T, E>` and `Box<T, A>` are **compiler-known generic types**, like `Slice<T>` in M2.
   Each instantiation is monomorphised in C. User-declared generic types and functions remain
   M6.
2. Allocators are **built-in, free-standing types**: `SystemHeap` and `FailingAllocator`. Their
   values are zero-sized. `AllocError` is a built-in enum.
3. The `Allocator` interface stays as specified in `memory-model.md` §3.2. User-defined
   allocators, and generic functions over allocators (Ex5 as written), come with interfaces in
   M6. M3 runs Ex5 with a concrete allocator type.
4. Optionals (`T?`, `null`, `?` on optionals) stay in M6 with the other library types. Ex11 is
   run in an adapted form without `StringView`, `checkedMul` and `okOr`.
5. When M6 adds generics, `Result` and `Box` should become library types with the same surface
   syntax and the same C layout, so programs written for M3 keep compiling.

## Consequences

- M3 stays small: no generic type checking and no interface resolution.
- The acceptance criteria are met with Ex4, Ex14 and adapted Ex5/Ex11 (`milestone-3.md`); the
  deferred parts of Ex5 and Ex11 are tracked for M6.
- The built-in surface (`Ok`, `Err`, `Box.create`, `Box.createIn`, `?`, `!`) is the one M6 must
  preserve.
