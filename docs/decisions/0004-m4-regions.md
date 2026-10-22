# ADR 0004 — M4 regions: built-in region types, scoped results, LIFO scratch frames

- Status: **Accepted** (2026-10-22, by the agent under the owner's instruction "start M4", following
  the pattern the owner chose in ADR 0003; open to revision by the owner)
- Scope: how M4 provides `Arena`, `FixedBufferAllocator`, `Scratch` and `Pool` before generics,
  interfaces and `unsafe` exist (M6, M8).

## Context

`memory-model.md` §3.3–3.5 and `ownership.md` R1–R4 describe region allocators whose allocations
are **loans of the region**. The examples (`language-spec.md` Ex15, Ex16) use generic methods
(`arena.allocArray<u32>(256)`), named arguments and allocations returned inside `Result`. v0 has
none of: generic methods, named arguments, references inside `Result` (§17.1 forbids them).

## Decision

1. **Built-in region types**, as ADR 0003 did for `Box`. `Arena`, `FixedBufferAllocator`,
   `ScratchFrame`, `Pool<T>` and `PoolBox<T>` are compiler-known, and their operations are
   built-in methods. M6 may turn them into library types with the same surface.
2. **Scoped results.** A `Result<T, E>` whose `T` or `E` is scoped (a reference, a slice, or
   another scoped value) is itself scoped. It follows the reference rules (§13.1): only in `let`
   locals, parameters and return types. The borrow checker treats every local of a scoped type as
   holding loans, not only reference locals (`lifetimes.md` §3).
3. **R1 as specified.** Allocation methods take `borrow this` and return values that carry a shared
   loan of the region. An allocation may still be a `borrow mut T`: its memory is fresh and
   disjoint from every other allocation, so exclusive access to it does not conflict with the
   shared loan of the region.
4. **No generic methods.** `allocArray(n, fill)` takes the fill value (like `[v; N]`), and `T`
   comes from `fill`. `Pool<T>`'s `T` comes from the expected type, as for `Ok`/`Err`.
5. **`Arena.create` cannot fail.** Chunks are allocated on demand, so it returns an `Arena`, not a
   `Result`.
6. **Scratch frames are LIFO by borrowing (corrects `memory-model.md` §3.4).**
   - The claim that `using` alone keeps frames LIFO is wrong. With frames `a`, then `b`, an
     allocation from `a` while `b` is live lands above `b`'s watermark, and `b`'s release then
     frees memory that `a`'s allocation still uses.
   - Fixed by: a nested frame is made with `parent.enter()`, which holds a **mutable loan** of the
     parent. The parent cannot allocate while the child lives (B1).
   - A root frame is made with `Scratch.enter()`, and only one root frame per thread may be live:
     entering a second one panics ("a scratch frame is already active").
   - Scratch memory is a runtime-owned stack of heap chunks, not the native stack.

## Consequences

- R1 needs no special case in the borrow checker beyond decision 2: loans flow from the region
  into the allocation through the method's receiver, like any `borrow(this)` result.
- Ex15 and Ex16 run in adapted forms (`milestone-4.md`).
- `memory-model.md` §3.4 is amended to point here.
