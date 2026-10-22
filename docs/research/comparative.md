# Comparative Research — Memory-Management Techniques for Nova

Status: Milestone 0 research note. Companion files: `goose.md` (primary-source
study of Goose), `cosmoos-kernel-audit.md`, `cosmoos-platform-audit.md`.

## Evidence levels

Every technique below is tagged with the strength of evidence behind its
safety claim:

| Tag | Meaning |
|---|---|
| **[proved]** | Mechanised or peer-reviewed soundness proof of a formal model of the technique |
| **[deployed]** | Used at scale in production; soundness relied upon but not formally proved for the full language |
| **[demonstrated]** | Working implementation exists; safety shown by tests/examples only |
| **[claimed]** | Author or project statement without public reproducible evidence |
| **[speculative]** | Research proposal or design document |

This note was written from the published literature and documentation listed
under each entry. It has **not** been re-verified against the latest source
trees of Rust, Zig, Swift, Nim or Vale; version-sensitive details are marked
"(as of the cited version)". Goose is covered separately from its source.

---

## 1. Rust — ownership, borrowing, lifetimes

Sources:
- The Rust Reference, "Ownership", "Lifetimes", "Destructors" (doc.rust-lang.org/reference).
- RFC 2094, *Non-lexical lifetimes* (2017).
- Jung et al., *RustBelt: Securing the Foundations of the Rust Programming Language*, POPL 2018.
- Jung et al., *Stacked Borrows*, POPL 2020; Villani et al., *Tree Borrows*, PLDI 2025.
- Polonius project (rust-lang/polonius), location-sensitive borrow checking.
- The Rustonomicon (unsafe code guidelines).

| Aspect | Notes |
|---|---|
| Guarantee | Safe code cannot cause use-after-free, double free, dangling dereference, data races or uninitialised reads. Out-of-bounds is prevented by runtime checks. **[proved]** for the λRust core via RustBelt, including selected `unsafe` libraries (Arc, Mutex, Cell, RefCell…). **[deployed]** for the full language. |
| Mechanism | Affine ownership with moves; `&T` shared XOR `&mut T` exclusive; region (lifetime) variables solved by MIR-level dataflow (NLL); drop elaboration with drop flags for conditionally moved values; `Send`/`Sync` auto-traits for thread safety. |
| Implementation complexity | **Very high.** The borrow checker operates on MIR (a CFG IR), not the AST. NLL needed a liveness-based region solver. Two-phase borrows, reborrowing, closures and generics with lifetime bounds each add significant complexity. |
| Runtime overhead | Near zero. Bounds checks and drop flags only. |
| Compile overhead | Moderate. Borrow checking is rarely the dominant cost; monomorphisation is. |
| Limitations | Self-referential structs, graph and cyclic ownership, and some mutation patterns need `unsafe`, indices or `Rc<RefCell>`. Lifetime annotations on structs and functions add friction. Elision rules hide some of it. |
| Suitability for Nova | **Core model to adopt in simplified form.** Nova should copy the *semantics* (affine ownership, XOR aliasing, CFG-based liveness borrow checking) and **not** the full generality (higher-ranked lifetimes, variance on user lifetimes) in the MVP. |

Key lessons for Nova:
1. Borrow checking must run on a **CFG IR**, not on syntax. Lexical lifetimes reject too many correct programs (the NLL motivation).
2. Drop elaboration (static drop where possible, drop flags otherwise) is a separate, well-understood pass. Nova needs it for cleanup lowering to C.
3. The soundness of the *library* matters as much as the checker. RustBelt's main contribution is a method for specifying `unsafe` abstractions, which Nova's `unsafe` contracts should copy.

---

## 2. Zig — explicit allocators

Sources: Zig Language Reference (ziglang.org/documentation, 0.11–0.13); `std.mem.Allocator`, `std.heap.*` in the Zig standard library.

| Aspect | Notes |
|---|---|
| Guarantee | **No static memory safety.** Safety-checked build modes trap on overflow, out-of-bounds and some invalid unions. Use-after-free is caught only probabilistically (GeneralPurposeAllocator can detect double free and leaks in debug). **[deployed]** as a *discipline*, not a guarantee. |
| Mechanism | `Allocator` is a fat pointer `{ptr: *anyopaque, vtable: *const VTable}` with `alloc`, `resize` and `free` (plus `remap` in later versions). Every allocating API takes an allocator parameter. Allocation failure is the error `error.OutOfMemory` in an error union. `defer` and `errdefer` handle cleanup. |
| Allocators | `page_allocator`, `GeneralPurposeAllocator`/`DebugAllocator`, `ArenaAllocator` (wraps a child allocator, frees all at `deinit`), `FixedBufferAllocator` (bump allocation in a caller-provided buffer; can free only the last allocation), `MemoryPool`, `c_allocator`. |
| Implementation complexity | Low. It is a library convention. |
| Runtime overhead | One indirect call per allocation through the vtable. It can be devirtualised when the allocator is comptime-known. |
| Limitations | Easy to free with the wrong allocator. Arena-derived pointers can outlive the arena. Nothing ties an allocation to its allocator in the type system. |
| Suitability for Nova | **Adopt the allocator interface shape and failure-as-value; reject the lack of tracking.** Nova must statically tie an allocation's lifetime to its allocator (see §5 Cyclone and the hybrid proposal). |

Key lessons:
- Making allocation failure an ordinary error value is what kernel code needs: there is no implicit abort. This maps directly onto CosmoOS `kmalloc` returning NULL.
- `errdefer`, which cleans up only on the error path, is a useful primitive for partially initialised objects. Nova's drop elaboration achieves the same result automatically for owned values.
- Freestanding builds work because the standard library assumes no OS unless asked. Nova's `core`/`std` split should copy this.

---

## 3. Cyclone — safe C with regions

Sources:
- Grossman et al., *Region-Based Memory Management in Cyclone*, PLDI 2002.
- Jim et al., *Cyclone: A Safe Dialect of C*, USENIX ATC 2002.
- Swamy et al., *Safe Manual Memory Management in Cyclone*, SCP 2006.
- cyclone.thelanguage.org documentation.

| Aspect | Notes |
|---|---|
| Guarantee | Type-safe C dialect with no dangling pointers into regions. **[proved]** for a core calculus (the PLDI 2002 formalisation). **[demonstrated]** for the implementation. The project is no longer maintained. |
| Mechanism | Every pointer type carries a region name: `int *ρ`. Regions are the stack frame (`` `L ``), the heap (`` `H ``, garbage collected in original Cyclone), lexical dynamic regions (`region r { ... rnew(r) ...}`), and later unique and reference-counted pointers. Region subtyping uses an *outlives* relation. Effects (sets of live regions) are attached to functions, and a pointer may be dereferenced only if its region is in the current capability set. Default region annotations and local inference reduce the annotation burden. |
| Pointer kinds | `*` (nullable, checked), `@` (non-null), `?` (fat pointer with bounds). Pointer arithmetic is allowed only on fat pointers. |
| Implementation complexity | Moderate to high: a region type system with effect inference. |
| Runtime overhead | Fat pointers carry bounds; null checks; dynamic regions use bump allocation. |
| Limitations | The original Cyclone relied on a conservative GC for the heap region; the unique and refcounted pointers came later. Annotation burden was a reported problem in practice (Grossman's thesis discusses it). |
| Suitability for Nova | **Adopt lexical regions with outlives constraints for arenas.** An arena in Nova is a Cyclone dynamic region whose lifetime is a borrow-checker region. Reject reliance on GC for the heap; use Rust-style unique ownership instead. |

Key lessons:
- Region-typed pointers are the clean way to stop arena references from outliving the arena (master prompt §5.4). In a borrow checker this is the same as "an allocation from arena `a` borrows `a`".
- Distinguishing pointer *kinds* (nullable, non-null, fat) at the type level maps well onto Nova's `T?`, `&T` and `Slice<T>`.
- Cyclone's C interoperability was good because it compiled to C, which is a precedent for Nova's backend.

---

## 4. Swift — ownership and noncopyable types

Sources: Swift Evolution SE-0377 (`borrowing`/`consuming` parameter modifiers), SE-0390 (noncopyable structs and enums, `~Copyable`), SE-0366 (`consume` operator), SE-0427 (noncopyable generics), SE-0446 (nonescapable types, in review/accepted per version); *The Swift Programming Language* (Memory Safety chapter).

| Aspect | Notes |
|---|---|
| Guarantee | Memory safety through ARC plus runtime exclusivity enforcement (dynamic checks for class properties and globals, static for locals). **[deployed]**. |
| Mechanism | Value types are copyable by default. Noncopyable types (`~Copyable`) get move-only semantics with `deinit`. Parameter conventions are `borrowing` and `consuming`, and `inout` for exclusive mutation. Classes use automatic reference counting. |
| Relevance | The **surface syntax** for ownership (`borrowing`, `consuming`, `consume x`) is readable to C#/TS developers, which supports Nova's `own`/`borrow` keywords. "Copyable unless declared otherwise" is a usable default for plain-data structs. |
| Rejected parts | Implicit ARC on ordinary objects (the master prompt §3 forbids implicit RC) and runtime exclusivity checks as the primary mechanism. |
| Suitability for Nova | Borrow the parameter-convention syntax and the copyable/noncopyable split. |

---

## 5. Nim — C backend and ARC/ORC

Sources: Nim Manual; *Nim Destructors and Move Semantics* (nim-lang.org/docs/destructors.html); Nim's `--mm:arc`/`--mm:orc` documentation; the Nim compiler's `cgen` module.

| Aspect | Notes |
|---|---|
| Relevance | Nim is the most mature production compiler that **emits C as its primary backend**. |
| C backend lessons | Name mangling for overloads and generics; per-module C files for incremental builds; emitting `#line` directives for debugging; using `NIM_BOOL`-style portable typedefs; handling C compilers' differing warnings; respecting strict aliasing (Nim emits `-fno-strict-aliasing` by default, which shows how hard it is to emit strictly conforming C). |
| Memory lessons | ARC uses scope-based destructor injection with move analysis ("sink" parameters, `=destroy`, `=copy`, `=sink` hooks, `wasMoved`). It shows that destructor injection plus last-use move optimisation can be implemented as an AST/CFG pass in a C-emitting compiler. ORC adds a cycle collector, which Nova rejects for the baseline. |
| Safety | Nim is **not** memory-safe in general: `ptr`, `addr` and `unsafeAddr` are pervasive, and `var` borrows are only partially checked. |
| Suitability for Nova | Copy the backend engineering (line directives, mangling, per-module emission, explicit C-UB avoidance helpers) and the hook-based destructor model. Do not copy the safety model. |

Note for Nova: Nim's default `-fno-strict-aliasing` is a warning sign. Nova's C backend should be designed to be **correct under strict aliasing** (no type-punning except through `memcpy` or unions defined by C11) and should *additionally* build with `-fno-strict-aliasing` where the host project already uses it. See `docs/c-backend.md`.

---

## 6. Vale — generational references

Sources: vale.dev; Evan Ovadia's blog series (*Generational References*, 2021; *Zero-Cost Memory Safety with Vale Regions and Hybrid-Generational Memory*, 2022/2023).

| Aspect | Notes |
|---|---|
| Mechanism | Every allocation carries a generation counter. A non-owning reference stores the pointer plus the generation it expects. Each dereference compares the two and traps on mismatch. A single owner frees the object and increments the generation. "Region borrowing" is proposed to elide checks within immutable regions. |
| Guarantee | Use-after-free is detected at runtime (a trap) rather than prevented. **[demonstrated]** in the prototype compiler. The performance figures are **[claimed]** in blog posts. Hybrid-generational memory and region elision are **[speculative]** / partially implemented. |
| Overhead | An 8-byte generation per object, a check per dereference, and allocations must never return memory to the OS for a different type unless generation stability is preserved (type-stable or never-unmapped memory). |
| Suitability for Nova | **Not the primary model.** It is valuable as a *library abstraction*: `Handle<T>` / slot maps with generation validation are exactly the "handle/slot abstractions with validation" option in master prompt §11 for stable references into collections. It also matches CosmoOS capability handles if those carry generations (see the kernel audit). |

---

## 7. Escape analysis (JVM / Go / native compilers)

Sources:
- Choi et al., *Escape Analysis for Java*, OOPSLA 1999.
- Kotzmann & Mössenböck, *Escape Analysis in the Context of Dynamic Compilation and Deoptimization*, VEE 2005.
- Stadler et al., *Partial Escape Analysis and Scalar Replacement for Java*, CGO 2014 (Graal).
- Go compiler `cmd/compile/internal/escape` and the `-gcflags=-m` diagnostics.

| Aspect | Notes |
|---|---|
| Purpose | Decide whether an allocation can live on the stack, be scalar-replaced, or must go to the heap. |
| Relevance | Go is the model for **developer-facing diagnostics**: `-m` prints "moved to heap: x" and "does not escape". Nova's proposed `--explain-allocations` should look like this. |
| Critical difference | In Go and Java escape analysis is **semantically invisible** because a GC makes heap placement always safe. In Nova it **must not** be used to make an invalid program valid (master prompt §7, rules 10–11). Nova's escape analysis may only *demote* an allocation the programmer requested on the heap to cheaper storage when it is provably equivalent: same destruction point, same observable allocator interactions, or allocator interactions explicitly declared unobservable. |
| Complexity | Intraprocedural connection graphs are moderate; interprocedural analysis needs summaries per function. |
| Suitability for Nova | Milestone 5 only, as an optimisation over an already-checked IR. It is never part of the safety argument. |

---

## 8. Region inference (Tofte–Talpin, MLKit)

Sources: Tofte & Talpin, *Region-Based Memory Management*, Information & Computation 1997; Tofte et al., *A Retrospective on Region-Based Memory Management*, HOSC 2004.

| Aspect | Notes |
|---|---|
| Mechanism | Fully automatic inference of a region for every allocation in ML programs; regions are stack-allocated lexically. |
| Guarantee | **[proved]** sound for the core calculus. |
| Limitation | The retrospective reports *space leaks*: inferred regions can live much longer than the data, which is unpredictable for programmers. The MLKit later added a GC alongside regions. |
| Suitability for Nova | **Reject full automatic region inference.** It conflicts with predictable resource usage, especially in the kernel. Use *explicit* arenas (Cyclone-style) with *inferred lifetimes of references into them* (Rust-style). |

---

## 9. SSA and IR design

Sources: Cytron et al. 1991 (SSA construction); Braun et al., *Simple and Efficient Construction of SSA Form*, CC 2013; Rust MIR RFC 1211; the Swift SIL documentation (ownership SSA, "OSSA").

| Aspect | Notes |
|---|---|
| Relevance | Ownership checking wants a **non-SSA, place-based CFG IR** (as Rust MIR does: locals and places with explicit `move`, `copy`, borrow and `drop` statements). Optimisation and escape analysis benefit from SSA. Swift's OSSA shows that the two can be combined, but OSSA is complex. |
| Decision for Nova | A MIR-like place-based CFG IR ("NIR") for checking and drop elaboration. SSA only later and only for optimisation (Milestone 9). Braun's algorithm builds SSA directly from NIR when needed. See `docs/intermediate-representation.md`. |

---

## 10. Pointer provenance and C undefined behaviour

Sources: ISO/IEC 9899:2011 and 9899:2018; ISO TS 6010 (*A Provenance-aware Memory Object Model for C*, PNVI-ae-udi); Memarian et al., *Exploring C Semantics and Pointer Provenance*, POPL 2019; Regehr's and Lattner's writings on UB.

Constraints the C backend must respect (detailed in `docs/c-backend.md`):
- Signed overflow is UB. Emit unsigned arithmetic with explicit checks (`__builtin_*_overflow` where available, portable fallbacks otherwise).
- Shifts ≥ width or by a negative amount are UB. Mask or check.
- Out-of-bounds pointer arithmetic beyond one-past-end is UB even if the result is never dereferenced. Compute indices, not pointers, until a bounds check has passed.
- Strict aliasing: access objects only through their declared type, `char`, or `memcpy`.
- Reading an uninitialised automatic object whose address is never taken is UB. Nova's definite-initialisation analysis must guarantee initialisation before use. The backend must zero-initialise or `memcpy` compound temporaries where needed.
- Provenance: integer↔pointer round-trips lose provenance under PNVI-ae-udi. They are allowed only in `unsafe` code, and the backend must use `uintptr_t` exclusively for them.
- `NULL` dereference, misaligned access and `memcpy` overlap are UB. These are guarded by Nova's type system or runtime checks.

---

## 11. Memory sanitizers and compiler fuzzing

Sources: Serebryany et al., *AddressSanitizer*, USENIX ATC 2012; UBSan docs; Yang et al., *Finding and Understanding Bugs in C Compilers* (Csmith), PLDI 2011; Le et al., *Compiler Validation via Equivalence Modulo Inputs*, PLDI 2014; Livinskii et al., *YARPGen*, OOPSLA 2020.

Use in Nova:
- ASan, UBSan and MSan on generated C in CI. This is **supporting evidence, not proof**, of backend correctness (master prompt §18).
- A grammar-based generator of well-typed Nova programs (Csmith-style), compiled with clang and gcc at -O0 and -O2, then differentially compared. This is the main defence against backend UB.
- Compile-fail corpora for the borrow checker, plus mutation of passing programs to check that the checker rejects the obvious unsafe mutants.
- CosmoOS already has sanitizer and analyzer gates (see the platform audit), so generated C should be held to the same gates.

---

## 12. Summary — what Nova takes from each

| Source | Adopt | Reject |
|---|---|---|
| Rust | Affine ownership, XOR borrowing, CFG/NLL-style checking, drop elaboration, Send/Sync, unsafe-contract discipline | Full lifetime generality in the MVP; trait-system complexity |
| Zig | Allocator interface, failure-as-value, freestanding-first std split, `defer` | Untracked allocator lifetimes |
| Cyclone | Arenas as typed regions with outlives; pointer kinds; compile-to-C precedent | GC-backed heap |
| Swift | `borrow`/`own` parameter syntax, copyable-by-default value types | Implicit ARC, dynamic exclusivity |
| Nim | C-backend engineering, destructor hooks | Unchecked `ptr`, cycle collector |
| Vale | Generational handles as a *library* type | As the default reference model |
| Escape analysis | Diagnostics UX (Go `-m`); stack demotion as a pure optimisation | Any role in safety |
| Region inference | — | Full automatic inference |
| Goose | See `goose.md` | See `goose.md` |
