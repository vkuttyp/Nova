# ADR 0006 — OD-3: region parameters on aggregates and functions

- Status: **Accepted** (project owner, 2026-10-23: "implement OD-3 first", before C3). The two
  remaining questions of `od3-design.md` §10 were not answered explicitly; this ADR takes the
  design's defaults, which the owner may revisit.
- Scope: references and other scoped values inside user structs and enums, generic instances and
  built-in collections. Implemented as milestone **M6x** (`milestone-6x.md`).

## Decision

1. **Implement OD-3 now**, as designed in `od3-design.md`, before C3.
2. **Syntax (default of §10 question 2):** region parameters `<region r>` on structs, enums and
   functions; region arguments first in a type's argument list (`Token<src>`, `StringView<r>`,
   `Slice<r, T>`); `borrow(r) T` for references; `static` is the built-in region of static data.
3. **Precision (default of §10 question 3):** one loan set per value, the union over its regions.
4. **Regions are erased after checking.** Two types that differ only in region arguments are the
   same type. Regions only determine, in signatures, which arguments' loans flow into the result
   and into `borrow mut` arguments (`type-system.md` §25.6).
5. **Type parameters act as regions** in signatures, and values whose type mentions a type
   parameter carry loans in generic bodies, so a generic checked once stays sound when
   instantiated with a scoped type.
6. Destroying a value that both holds loans and needs drop is a use of its loans (drop check). It
   reports the existing N0243 rather than a new code; N0271 stays unassigned.

## Consequences

- E3 and E4 are relaxed exactly as `type-system.md` §25 says. `Box`, statics, constants, arrays,
  slices and pools still reject scoped values (N0246).
- v0 programs keep their meaning: a v0 signature has no region names, and its flows equal its v0
  return sources.
- New diagnostics: N0270, N0272, N0273, N0274.
