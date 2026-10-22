# Architecture Decisions — Register

> **2026-10-22: every proposal below was ACCEPTED** by the project owner and recorded in
> [ADR 0002](decisions/0002-milestone-0-decision-set.md). The exceptions are KQ-2, 3, 5, 6
> and 7, which need CosmoOS changes or rulings; Nova uses the conservative defaults listed
> in ADR 0002. Add new open questions below with the next free ID (OD-22+).

Original status: Milestone 0 (master prompt §23.K). Each item lists the proposed (now accepted) answer.

## Language and compiler (owner: Nova)

| ID | Question | Proposed | Alternatives | Safety impact | Perf impact | Blocks |
|---|---|---|---|---|---|---|
| **OD-1** | Compiler implementation language | **C# / .NET 10, NativeAOT for distribution** (ADR 0001) | Rust, C++20 | none directly | compile speed only | **M1** |
| **OD-2** | Memory architecture | **Hybrid (C)**: ownership + regions-as-loans + optimisation-only escape analysis (`memory-model.md`) | A (pure ownership), B (regions) | defines the safety argument | — | **M1** (shapes IR) |
| **OD-3** | Lifetime-parameterised structs (references in user aggregates) | Not in v0 (E3). Design for M6 with syntax such as `struct Parser<region r>` | Rust-style `'a` now | larger checker surface | enables zero-copy | M6 |
| OD-4 | `class` keyword | Reserved; future sugar for always-boxed non-Copy types | Classes now; drop the keyword | none if sugar | — | M6 |
| **OD-5** | Panic strategy | **Abort, no unwinding**; destructors do not run on panic | Unwinding via C `setjmp` (no `setjmp` in CosmoOS libc, and costly) | simpler cleanup proofs | smaller code | **M1** |
| **OD-6** | Integer overflow | **Panic in all builds**; explicit `wrapping*`/`checked*`/`saturating*` | Rust-like wrap in release | logic safety | small check cost (BCE/range analysis M9) | **M1** |
| **OD-7** | Copyability of structs | **Implicitly Copy** iff all fields Copy and no `drop`; `@noncopy` to opt out | Explicit `copy struct` opt-in (Rust-like) | none (both sound) | — | M2 |
| OD-8 | Arena with destructors | v0: arena contents must have no `drop` (R2); later a drop list | Allow drops now | resource leaks if done wrong | — | M4 |
| OD-9 | Closures | None in v0. Non-capturing `fn` pointers only. Scoped capturing closures in M6+ | — | closures carry borrows | — | M6 |
| OD-10 | Dynamic dispatch (`dyn Interface`) | Future. Static generics only in v0 | vtables now | — | code size | M6 |
| **OD-11** | Package / manifest format | `nova.toml` with name, version, profile, dependencies (path-only in v0) | — | — | — | **M1** (minimal) |
| OD-12 | Growable scratch arrays (Goose "one growable on top") | Not in v0 | adopt | complex shrink rules | faster temporaries | M5 |
| OD-13 | `Rc<T>`/`Weak<T>` library type | Post-M6, explicit and opt-in, single-threaded | never | cycles leak | RC traffic | post-M6 |
| OD-14 | `@flat` inline variable-size layouts | Post-M9 research | — | — | — | — |
| OD-17 | Call-site borrow syntax | Implicit shared borrow, `mut x` for exclusive, `borrow x`/`borrow mut x` elsewhere | Rust `&x`/`&mut x` | none | — | **M2** |
| OD-18 | Surface syntax for references in types | `borrow T` / `borrow mut T` (as the prompt proposes) | `&T`/`ref T` | none | — | **M2** |

## CosmoOS integration (owner: CosmoOS project owner)

| ID | Question | Proposed | Blocks |
|---|---|---|---|
| **OD-15** | Is the Nova runtime "library" under libc invariant L1 (may it use raw `cosmo_*` syscall wrappers)? | **No**: public libc functions only, until the owner says otherwise | **C1** (confirms the approach) |
| OD-16 | May Profile A also target the musl/Linux personality? | Later, as an opt-in profile; native is the default | C3 |
| **OD-19** | Is checked-in generated C acceptable (port route S1)? Or must generation run in the build (.NET in CI)? | S1: checked-in, deterministic, with a header naming the novac version and source hash | **C1→C3 in-tree** |
| **OD-20** | Will the owner schedule a Nova unit (AGENTS.md restricts work to the owner-chosen 1.2 unit)? | Propose after C1 succeeds out of tree (S0) | in-tree work |
| OD-21 | Missing libc pieces (`aligned_alloc`, `math.h`, `poll`, `strtod`, `exec*`): add to CosmoOS libc, or implement in the Nova runtime? | Add to libc as owner-chosen units (they benefit C too); Nova works around them meanwhile | C3 |
| KQ-1…KQ-8 | Kernel questions | See `cosmoos/kernel-memory-and-lifetimes.md` §8 | C5–C8 |

Also to report to the owner (no decision needed from Nova): CosmoOS's
`limits.h` `CHAR_MIN/MAX` are wrong on AArch64. About 18 doc/code disagreements
are listed in the two audit reports.
