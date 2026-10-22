# ADR 0005 — M6: user generics and interfaces; built-in collections; floats deferred

- Status: **Accepted** (project owner, 2026-10-22, when starting M6: "Built-in types" for collections,
  "Adapt Ex13" for floats)
- Scope: what M6 implements in the language, and what stays built in.

## Decision

1. **User generics and interfaces are real language features in M6.** This covers generic
   functions, structs and enums, monomorphisation, interfaces, `impl` blocks and bounds
   (`language-spec.md` Ex12, Ex13, OD-10: static dispatch only).
2. **Generic bodies are checked once, generically** (`type-system.md` §20):
   - a type parameter is an opaque type: a move type that may need drop, supporting only what its
     bounds allow;
   - move and borrow checking also run on the generic version;
   - each instantiation is then monomorphised for C. Errors are reported at the definition, not
     per instantiation.
3. **Collections are built-in generic types**, like `Box` (ADR 0003) and the regions (ADR 0004):
   `Vector<T, A>`, `String<A>`, `StringView`, `SlotMap<T>` with `Handle<T>`, and
   `StableVector<T>`. Their memory is managed by the C runtime. The user-visible API is the one
   `memory-model.md` §4 describes. A Nova core library implementation waits for `unsafe` (M8).
4. **Library conversion is deferred to M8.** `Result`, `Box`, the regions and the collections
   become Nova library types once raw pointers and `unsafe` exist. The surface syntax stays.
5. **Floats stay out.** Ex13 runs with integer geometry. Floats (`f32`/`f64` arithmetic,
   formatting, the kernel-profile ban) are an open item to schedule separately (OD-23 in `open-decisions.md`).

## Consequences

- M6 adds the generic machinery (type parameters, instantiation, inference, interfaces) that M8's
  library will need. The collections' semantics (invalidation through borrows, H2) are checked
  by the same borrow checker.
- Ex13 and Ex12 run in adapted forms. Ex12's `return if (…) a else b` is written as an `if`
  statement, since `if` expressions do not exist in v0.
