# Milestone 4 — Regions (plan and acceptance)

Status: started 2026-10-22. Normative semantics: `ownership.md` R1–R4, `memory-model.md` §3.3–3.5,
`type-system.md` §19 (written increment by increment), ADR 0004. Each increment writes its
spec first, then the code and tests.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| **4a** | **Scoped results and `Arena`**. Results holding references or slices, with loans tracked through any loan-holding local. `Arena.create(allocator, chunkSize)`, `arena.alloc(v)`, `arena.allocArray(n, fill)`, `arena.reset()`, bulk release on drop. R1 and R2 | type-system §19.1–19.3, lifetimes §3 | **done 2026-10-22** |
| 4b | **`FixedBufferAllocator` and `Scratch`**: allocators over caller storage (scoped to it), and LIFO scratch frames (ADR 0004) | type-system §19.4–19.5, §14.6 | **done 2026-10-22** |
| 4c | **`Pool<T>` and `PoolBox<T>`**: a typed pool of fixed capacity, with individually freeable, scoped boxes | type-system §19.6 | planned |

## Acceptance criteria (`roadmap.md`)

1. **Compile-fail tests for every escape form.** An allocation must not:
   - be returned;
   - be stored in a `Box` or another owned value;
   - outlive its region through `reset`, drop, move or a scope end.
   Each form gets a test for each region type.
2. **Run tests for bulk release.** An arena with many allocations frees everything at reset and
   drop (`live=0`), also under the fault-injection sweep.
3. **R2 enforced.** No region accepts a type with `drop` or a scoped type (N02xx).
4. Ex15 and Ex16 in adapted forms (ADR 0004): `allocArray(n, fill)`, and positional arguments.
5. Clean under GCC/clang, ASan+UBSan, the fault-injection sweep, and inside CosmoOS
   (`c2.py`) on both arches.
6. Docs: `type-system.md`, `lifetimes.md`, `memory-model.md`, `c-backend.md`, `diagnostics.md`,
   `risks.md`.
