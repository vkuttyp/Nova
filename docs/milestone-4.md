# Milestone 4 — Regions (plan and acceptance)

Status: **complete 2026-10-22** (criteria below). Normative semantics: `ownership.md` R1–R4, `memory-model.md` §3.3–3.5,
`type-system.md` §19 (written increment by increment), ADR 0004. Each increment writes its
spec first, then the code and tests.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| **4a** | **Scoped results and `Arena`**. Results holding references or slices, with loans tracked through any loan-holding local. `Arena.create(allocator, chunkSize)`, `arena.alloc(v)`, `arena.allocArray(n, fill)`, `arena.reset()`, bulk release on drop. R1 and R2 | type-system §19.1–19.3, lifetimes §3 | **done 2026-10-22** |
| 4b | **`FixedBufferAllocator` and `Scratch`**: allocators over caller storage (scoped to it), and LIFO scratch frames (ADR 0004) | type-system §19.4–19.5, §14.6 | **done 2026-10-22** |
| 4c | **`Pool<T>` and `PoolBox<T>`**: a typed pool of fixed capacity, with individually freeable, scoped boxes | type-system §19.6, §19.1 (`var`) | **done 2026-10-22** |

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

## Status at completion

1. **Met.** Escape forms by region type:

   | Escape | Arena | FixedBufferAllocator | ScratchFrame | PoolBox |
   |---|---|---|---|---|
   | returned | `arena-escapes` (2) | `region-escapes` | `region-escapes` | `pool-borrows` |
   | stored in a box, struct or other owned value | `arena-types` | `region-types` | — (a frame can't be stored either: `region-types`) | `pool-escapes` |
   | region reset, moved or dropped while live | `arena-escapes` (reset, move) | storage access: `region-escapes` | `region-escapes` (move; parent allocates while child live) | `pool-borrows` (move) |
   | temporary region | `arena-escapes` | `region-escapes` | `region-types` (N0247) | `pool-borrows` |

2. **Met.** `arena-bulk-release` (5 reset rounds, then drop, `live=0`), `ex15-arena`,
   `fixed-buffer-and-scratch` and `pools`, under the host and guest fault-injection sweeps.
3. **Met.** N0264: `arena-types`, `region-types`, `pool-escapes`. Pools hold values with
   `drop`: R2 is about bulk release, and pool boxes are destroyed one by one.
4. **Met.** `run-pass/ex15-arena.nova`, `run-pass/ex16-fixed-buffer.nova`.
5. **Met.** Local suite (clang -O0/-O2/portable/ASan+UBSan), CI (GCC 13, clang 18), and
   `c2.py` inside CosmoOS on both arches.
6. **Met.** `type-system.md` §14.6, §19; `lifetimes.md` §3; `memory-model.md` §3.4–3.5;
   `c-backend.md`; `diagnostics.md`; ADR 0004.

Design corrections made during M4, all recorded in ADR 0004 and the spec:
- `memory-model.md` §3.4 claimed that `using` alone kept scratch frames LIFO. It does not.
  Nesting now borrows the parent's allocation right, and roots are unique.
- A scratch frame's two borrow parts let a parent's existing allocations stay usable while a
  child frame is live.
- `using` bindings are pinned, not immutable.
- Scoped values other than references and slices may be `var` (M4c).
