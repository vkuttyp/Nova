# ADR 0002 — Milestone 0 decision set

- Status: **Accepted** (project owner, 2026-10-22: "approve all the open decisions")
- Scope: every proposal listed in `docs/open-decisions.md` at the end of M0/C0.

Each decision below adopts the **proposed** answer from `open-decisions.md`, with the
rationale given in the referenced document. Later changes need a new ADR that
supersedes the relevant item.

## Language and compiler

| ID | Decision | Reference |
|---|---|---|
| OD-1 | Compiler in **C# / .NET 10**; NativeAOT for distribution | ADR 0001 |
| OD-2 | **Hybrid memory architecture**: ownership + borrowing as the sole safety mechanism; regions/arenas as loans; escape analysis optimisation-only | `memory-model.md` §1–2 |
| OD-3 | No references in user aggregates in v0 (E3); lifetime-parameterised structs designed for **M6** | `ownership.md` §4 |
| OD-4 | `class` reserved; future sugar for always-boxed non-Copy types | `language-spec.md` Ex4 |
| OD-5 | **Panic = abort**; no unwinding; destructors do not run on panic | `ownership.md` §0 |
| OD-6 | **Integer overflow panics in all builds**; explicit `wrapping*`/`checked*`/`saturating*` | `ownership.md` §0 |
| OD-7 | Structs are **implicitly Copy** iff all fields are Copy and no `drop`; `@noncopy` opts out | `ownership.md` §1 |
| OD-8 | v0 arena contents must have no `drop` (R2); drop lists later | `memory-model.md` §3.3 |
| OD-9 | No closures in v0 (non-capturing `fn` pointers only); scoped closures M6+ | `language-spec.md` §6 |
| OD-10 | Static generics only in v0; `dyn Interface` future | `language-spec.md` Ex12 |
| OD-11 | Package manifest **`nova.toml`** (name, version, profile, path-only dependencies in v0); exact schema specified in M1 | `milestone-1.md` |
| OD-12 | No growable scratch arrays in v0 | `memory-model.md` §3.4 |
| OD-13 | `Rc<T>`/`Weak<T>` post-M6, explicit, single-threaded | `memory-model.md` §6 |
| OD-14 | `@flat` layouts: post-M9 research only | `memory-model.md` §7 |
| OD-17 | Call sites: implicit shared borrow; `mut x` for exclusive; `borrow x`/`borrow mut x` elsewhere | `language-spec.md` §3 |
| OD-18 | Reference types spelled `borrow T` / `borrow mut T` | `language-spec.md` §2 |

## CosmoOS integration

| ID | Decision | Reference |
|---|---|---|
| OD-15 | Nova userland runtime uses **public libc functions only** (no raw `cosmo_*` syscall wrappers) | `cosmoos/profiles.md` §2.2 |
| OD-16 | Native personality is the default; musl/Linux personality profile later, opt-in | `cosmoos/profiles.md` §2.3 |
| OD-19 | In-tree route S1 uses **checked-in, deterministic generated C** with a provenance header (no .NET in CosmoOS CI) | `cosmoos/build-integration.md` §3 |
| OD-20 | Prove C1 out of tree (S0) first, then propose a Nova unit for CosmoOS | `cosmoos/feasibility.md` |
| OD-21 | Missing libc pieces are proposed as CosmoOS libc units; Nova works around them meanwhile | `cosmoos/profiles.md` §2.2 |

## Kernel questions (KQ)

Accepted where the answer is a **Nova-side policy**:

| ID | Decision |
|---|---|
| KQ-1 | IRQ-context Nova code is `@noalloc` (follow the `interrupt.h` policy) |
| KQ-4 | `@nosleep` scopes may allocate via `kmalloc`; the permission lives in the kmalloc binding and is revisited if kmalloc gains `might_sleep` |
| KQ-8 | Address typing stays Nova-side; no CosmoOS header changes requested |

**Not decidable from Nova alone.** Each needs a CosmoOS code change or a CosmoOS policy
ruling made as an owner-chosen unit. Until then, Nova adopts the conservative default shown:

| ID | Question | Nova default until resolved |
|---|---|---|
| KQ-2 | Export `preempt_enable` / out-of-line quiesce read lock for modules | Modules may not use `Quiesce.read()` |
| KQ-3 | `call_quiesce` barrier at module unload | Modules may not `deferDrop` into module text |
| KQ-5 | Built-in Nova code calling non-exported internals | Only exported symbols, even for built-in code, until ruled |
| KQ-6 | `kobject_put` from IRQ context for non-blocking releases | Forbidden |
| KQ-7 | FP-register check for `.ko` files | Nova's C4 acceptance runs the disassembly check itself |
