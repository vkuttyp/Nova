# Goose: technical analysis for Nova

Research reference for the Nova brief (`prompts/master-prompt.md` §2 "Goose", §5.2, §23.B).
Goose is treated here as a research reference, not a design to copy.

Citation conventions (all paths relative to the Goose checkout):

- `S §x` = `docs/goose_spec.md` section x (line numbers given where useful as `S:L123`).
- `I §x` = `docs/implementation.md` section x (`I:L123`).
- `R §x` = `docs/reference.md`.
- Source files are under `src/`, cited as `file.h:line` or `file.h` + function name.
- "(inferred)" marks conclusions I drew rather than read stated in the source or docs.

---

## 1. Source and version

| Item | Value |
|---|---|
| Repository | https://github.com/aardappel/goose |
| Commit read | `f605b5a07dd2aa3a5cb3f4c9b14002bd3c9d3d1a` |
| Commit date | 2026-10-21 09:20:51 -0700 ("Regenerate runtime_inline.h from the merged runtime sources") |
| Clone | `git clone --depth 50`; all 50 commits are dated 2026-10-21 |
| Spec version | "v4 draft" (`S:L1`) |
| Authors | Wouter van Oortmerssen (design); "Claude Fable" credited with compiler implementation, benchmarking, samples and docs (`README.md:600-635`) |
| Built or run? | No. Nothing in the checkout was built or executed. Everything below comes from reading files. |

**Brief's named docs:** all four exist: `README.md` (635 lines), `docs/goose_spec.md` (4512), `docs/implementation.md` (6448), `docs/reference.md` (3803). Other docs: `docs/tutorial.md`, `docs/stdlib.md`, `docs/testing.md`, `docs/packages.md`, `docs/editors.md`, `docs/index.md`, and `docs/design/*.md` (15 design notes).

**Files read in full or in the relevant part:**

- `README.md` (all)
- `docs/goose_spec.md`: §1, §3.3-3.4, §3.8, §3.11, §4.1-4.3, §5 (all), §7.2-7.4, §7.8 (first part), §7.9, §7.14, §9.1-9.4, §10 (all), §11.2-11.3, §12, App. B, C, E
- `docs/implementation.md`: §1, §3.1, §3.4, §3.5 (start), §4 (start), §5.1-5.3, §5.8-5.12, §6.1-6.6, §6.10 (start), §7, §8, §10, §11
- `docs/reference.md` §4; headings of the rest
- `docs/design/owning_dynamic_data.md` (all), `docs/design/analysis_inventory.md` §1-3, §7, `docs/design/wasm.md` §1-2
- `bench/summary.md`, `bench/design.md`, `bench/results.md` (header + aggregate table), `bench/plan.md` (start), `bench/adoption.md` (start), `bench/plb/README.md` (results), `bench/kostya*/README.md`, `bench/bddicken/README.md` (heads)
- Source: `src/runtime/runtime.h` (stack and check macros, L185-205, L785-930), `src/runtime/runtime_impl.h` (region reservation L505-600), `src/codegen_stacks.h` (L1-80), `src/codegen_frames.h` (L400-430, L740-851), `src/codegen_stmts.h` (L730-800), `src/codegen_builtins.h` (L589-700), `src/codegen_values.h` (L640-670), `src/codegen_calls.h` (L413-440), `src/codegen.h` (L551-560), `src/codegen_construct.h` (L13-18); function locations found by grep across `typecheck*.h`, `bce.h`, `optimize.h`, `main.cpp`

**Size:** `src/*.h` + `src/*.cpp` total 60,425 lines. The README's "about thirty thousand lines of C++" (`README.md:588`) undercounts this. The tree includes the LSP, packages and build tooling, which may account for the difference (inferred). The runtime (`src/runtime/*`) is 4,062 lines. `test/` holds 2,670 `.goose` files.

---

## 2. Stack model

### 2.1 Three kinds of memory

A Goose program has three kinds of memory (`S §1.2`, `S:L60-78`; `R §4`):

1. **The native call stack and registers.** These hold fixed-size locals and temporaries. Recursion depth uses only this stack (`S §7.8`, `I §7` "Native stacks").
2. **N data stacks.** Each is a large reserved virtual-address region with a bump pointer. N is computed statically by the compiler.
3. **Static data.** String literals and `const` globals with compile-time initializers.

There is "no general-purpose heap and no `malloc`" (`S:L76`). The runtime does allocate internally in a few places: thread queues (`S:L78`, `runtime.h` `gs_qnode`), the per-thread stack-block array (`calloc` in `gs_stack_block`, `runtime.h:888-893`), and contained-worker abort messages (`I §7` "Contained workers").

### 2.2 Size classes decide placement

Every type has a size class (`S §1.1`):

- **fixed**: compile-time byte size.
- **variable**: size chosen at construction, then frozen.
- **resizable**: grow-only `T[>..]` or grow-shrink `T[>..<]`, plus any struct whose last field is resizable.

Placement by class:

- **Fixed values** go on the native stack or in registers (`S §1.3(5)`). Backend exception: fixed locals and temporaries larger than 4096 bytes go on data stacks, except inside recursive cycles (`S §10.3`, `S:L3550-3565`).
- **Variable and resizable values** go on data stacks. `S:L3566-3569` allows `alloca` for variable-class locals, but the current backend always uses data stacks.

### 2.3 The stack invariant (`S §1.3`, `S:L80-105`)

1. Values are pushed in lifetime order and popped together at scope exit. Lifetimes on one stack are strictly nested.
2. **At most one resizable value is live per stack, and it is always the topmost value.** Growth is therefore a pointer bump with no capacity check. Address space is pre-reserved, and guard pages catch overflow.
3. Two simultaneously live resizables never share a stack. Locals with disjoint lifetimes reuse stacks.
4. A variable-size value can be buried under later allocations once its construction completes. While a value is under construction on stack S, nothing else may allocate on S.
5. The length header of an outermost resizable local lives in the owning native frame, not on the data stack.

"The compiler proves all of this statically; there is no runtime bookkeeping beyond the bump pointers themselves" (`S:L104-105`).

### 2.4 Stack assignment

**Spec recipe (`S §10.3`).** Do a DFS over specializations from `main`, carrying a compile-time "stack environment" (a watermark plus local-to-index map). Each new simultaneously live resizable gets the lowest free index. Indices are freed at scope exit. A pending nonfixed return's destination stays in the in-use set until the value is received. N is the maximum index + 1.

**Implementation: hidden-argument strategy (`S App. E`, `I §6.2`).**

- Every function that touches data stacks takes a hidden `int64_t gs_sp`.
- It addresses its own nonfixed locals as `GS(gs_sp + k)` for per-function constants `k` (`codegen.h:551` `SpIdx`).
- It calls callees with `gs_sp + <indices in use>` (`SpTop`, `codegen.h:552`).
- `GS(i)` is `(&gs_stks[i])`, where `gs_stks` is a thread-local array of `gs_stack { uint8_t *top; }` (`runtime.h:798-880`).
- Globals own dedicated stacks inside the per-instance globals struct (`I §6.3`).

**Static count.** The total is bounded at compile time by `CodeGen::BoundStacks` (`codegen_stacks.h:21-73`):

- It runs Tarjan's SCC over the call graph, where a function's need = max(own count, offset + callee need).
- Members of a recursive cycle share one count.
- A call into a cycle made with stacks in use is an internal error (`codegen_stacks.h:56-60`). The checker is supposed to have rejected it already (`NoStackAcrossCycleCall`, `codegen.h:553-559`).
- The cap is `GS_MAX_STACKS` = 1024. Exceeding it is a compile error (`runtime.h:74`, `I §7` "Stack counts").

**Reservation (`I §7`, `runtime_impl.h:516-597`).**

- Each stack is one `mmap(PROT_READ|PROT_WRITE, MAP_PRIVATE|MAP_ANONYMOUS|MAP_NORESERVE)` region of `GS_STACK_RESERVE`, 2 GB by default (`runtime.h:76-77`).
- A trailing `GS_STACK_GAP` (1 MB, `runtime.h:79-80`) is set to `PROT_NONE`.
- On Windows, pages are reserved with `MEM_RESERVE` and committed on fault by a vectored handler (`runtime_impl.h:323,375`).
- If the OS refuses a region, `gs_reserve_region` halves the size down to 1 MB and retries (`runtime_impl.h:575-597`).
- An overall 32 TB address-space budget (`GS_STACK_BUDGET`, `runtime.h:87-88`) caps `hardware_threads()` (`S §10.4`, `S §11.2`).

### 2.5 Resizable-value restrictions (`S §3.3`, `S §3.4`, `S §5`)

- **Where resizables may appear.** Only locals/globals, by-value params, returns, and the **final field** of a struct (recursively). At most one resizable per struct. **Never an array element** (`S:L411-427`). So there is no "vector of vectors" (`docs/design/owning_dynamic_data.md:146-148`).
- **Element sizes.** `T[>..<]`, `T[..k]` and `T[..]` need fixed-size elements. `T[]` and `T[>..]` may hold variable-size elements, which makes the array *sequential*: iterable but not indexable, and with no `pop` (`S:L358-370`).
- **Shrinking a grow-only `[>..]`.** `pop`/`clear`/`resize`-down is legal only where the compiler can see that nothing live refers into the array. Liveness is syntactic, plus a program-wide "store record" of every reference store (`S §5.1`, `S:L1311-1420`).
- **Grow-shrink `[>..<]`.** References into it may be held **only by variables, never stored** in fields, elements or globals. A shrink is an error while any live variable may refer in (`S §5.2`, `S:L1504-1567`). "Balanced calls" (a callee that only `resize`s back to a saved mark) are exempt (`S:L1568-1606`).
- **Moves.** There is no move operation for resizables. Copying is explicit (`copy(x)`) and O(size) (`S §4.1`, `S:L1042-1048`; TODO 3 in `S App. B`).
- **Recursion (`S §7.8`).** No function in a recursive cycle may call into the cycle while it has a nonfixed local in scope or a nonfixed temporary live in the statement. Otherwise each activation would need its own data stack and N would not be static. Growable data that must outlive a recursive call has to be owned outside the cycle and passed in.

### 2.6 Growth, resizing and aliasing

- **Growth never moves memory.** `[>..]` growth bumps `top`, and the base is fixed for the array's lifetime. So "references into a grow-only resizable `[>..]` remain valid for as long as they can be named" (`S:L683-685`; `README.md:95-108`). This is the headline difference from `Vec`/`std::vector`.
- **Stacks are never resized or relocated.** Exceeding a reservation is a guard-page abort, not a reallocation (`S §9.3`, `S:L3307`). The wasm design note says that relocating stacks would require index-based references and growth checks everywhere (`docs/design/wasm.md` §1-2, proposal only).
- **Codegen aliasing hazard.** Because a `uint8_t *` store may alias a stack's `top` field in C, stack tops are cached in C locals and flushed/reloaded around calls that can reach the stack (`I §6.2` end, `I §6.10` "Tops"). Soundness requires "one spelling per cached stack", and a fat reference's `.stk` could be a second spelling. So caching is decided class by class using the checker's root classes (`I §6.10`, `bench/plan.md` §2.1).
- **Memory is never returned to the OS.** I found no `MADV_DONTNEED`/`MEM_DECOMMIT` in `src/runtime/` (grep, inferred). Pages committed by a stack's high-water mark therefore stay committed. `owning_dynamic_data.md:123-124` says the same for pools ("never returned to the OS").

### 2.7 Construction into destination storage (`S §4.2`, `S §4.3`, `S §7.3`, `I §6.5`)

**The guarantee.** A fresh nonfixed value "is built directly in its receiving storage, including through calls and the selected branch of an `if`/`match`/case-dispatch" (`S:L1170-1176`). Byte relocation for packed-layout changes (length-prefix width, header removal) is allowed. Staging is allowed only for limited-capacity receivers (`S:L1177-1189`).

**Mechanism.**

- Every value-producing node is emitted against a destination `Dst`: discard, a C lvalue, or a data-stack top (`I §6.5`, `GenAny`/`GenConstruct`, `codegen_construct.h:360`).
- Callees receive the destination stack as a hidden argument and return only the outermost metadata (counts) in registers (`S App. C.3`).
- **Request kinds.** A destination asks for either "one value" or "a run of elements". For `v.append(f())` the compiler builds an `_er` twin of `f` that emits raw elements plus a count (`S:L2342-2356`, `I §6.5` `EnsureEr`).
- **Guaranteed NRVO.** When every return of a nonfixed result names the same local, that local is allocated at the destination from its declaration (`S:L2321-2333`, `codegen_render.h:596` `DetectNrvo`).
- **Exits.** A `return`/`break` taken mid-construction builds its value behind the partial one and `memmove`s it down (`S:L2335-2340`, `I §6.5` "Exits").
- **Known gap, since fixed.** The internal audit found paths that still copied whole fresh results (runtime-capacity limited arrays into `append`). It was resolved in this version (`I §11` item 9).

### 2.8 Contiguous layouts (`S §3.3`, `S App. C.2`, `I §6.1`)

- **Array family.** Every member is `[metadata][elements...]`, inline, packed, with no pointer to an element block. `T[]` is a length then elements, `T[..k]` a length then k slots, `T[..]` a capacity, a length, then slots (`S:L304-319`, `S:L4213-4216`).
- **Packing and alignment.** Structs are packed concatenations in declaration order (`#pragma pack(1)`), with all loads and stores unaligned-safe (`S:L4205-4211`, `I §6.1`).
- **Variable-size fields** sit inline. A field after one is reached by walking sizes with generated `gs_size_<T>` walkers (`I §6.1` "Bytes values").
- **ADTs.** Fixed mode is tag + max payload. Variable mode is tag + the actual variant's payload (`S:L4217-4221`).
- **Relative references** `T&<u8|u16|u32|varint>` store offsets from the field itself or from a named pool's base. This makes structures position-independent and serializable by `memcpy` plus a verifier (`S §3.9`, `README.md:204-251`).
- **Resizable values** are a native-frame header `gs_rhdr { base, len }` with elements on the stack. A resizable-tailed struct with a fixed prefix is a "frame object": a C struct of the fixed fields plus the tail header (`S:L4234-4263`).
- **Example from the README:** an `Order` record is "29 bytes and 0 allocations" versus 160 bytes in C++ and 153 bytes in Rust (`README.md:164-169`). The cost is that arrays of variable-size elements cannot be indexed.

---

## 3. Safety model

### 3.1 What "safe" means here (`S:L32-37`, `S §9.1`)

Goose guarantees three things:

- no out-of-bounds access;
- no access to memory at a type other than the one it was written with;
- no dangling references into freed or retyped memory.

It explicitly does **not** provide Rust-style aliasing control: "no aliasing/exclusivity restrictions at all". The argument is that without shared-memory concurrency, aliasing cannot break type safety given the layout rules (`S:L3126-3132`).

### 3.2 Reference roots and lifetime inference (`S §9.2`, `I §3.4`, `I §3.5`)

**Roots.** Every reference or slice value has one or more static roots. A root is the local or global variable (or a statement temporary) whose scope bounds the target.

- A root is **exact** if that variable's own storage holds the target, and **inexact** if it only bounds the target's lifetime (`S:L3136-3148`).
- Implementation: `struct RootAlt { VarDef *root; bool exact; VarDef *from; bool slotread; bool classread; }` and `Roots { vector<RootAlt> alts; }`, wrapped in `Prov` with writability, pool and byte-view bits (`I:L953-970`, `ast.h`).

**Depth.** Each `VarDef` has a depth (0 for globals, otherwise the scope count along the *compile-time call path*). "A outlives B" is `Depth(A) <= Depth(B)` (`I:L996-1001`). This works only because checking is whole-program in call-graph order with per-root specialization (next point).

**Specialization replaces annotations.** Functions are monomorphized per (argument types, reference root classes, writability, destination stacks and request kinds, static function values, ...). Roots are therefore always concrete inside a body. The spec calls this "Rust-lifetime precision via monomorphization, with zero syntax" (`S:L3143-3148`, `S §10.2`). The key is built in `GetOrCreateSpec` (`typecheck_calls.h:1361`), with the table at `I:L408-418`.

**Rules (`S:L3161-3279`).**

- **Store.** `r` may be stored into a location owned by root `L` only if `scope(root(r)) ⊇ scope(L)`. Every store is logged in the program-wide **store record** (`RecordStore`, `typecheck_builtins.h:1565`), which shrink checks consult. Enforced in `FitsAt` (`typecheck_exprs.h:1045`).
- **Return.** A returned reference's root must be visible to the caller: a parameter-supplied root, a global, or the in-place-constructed return value.
- **Merged values.** For `if`/`match`/loops/returns, the value carries every alternative's root, and each rule must hold for all of them.
- **Implicit root generics.** Structs with reference fields are implicitly generic over those fields' roots.
- **Rebinding.** A reference variable commits to its first binding's root. `.=` may rebind only within that root or to one at the same depth.
- **Temporaries** live to the end of their statement. A slice of a call result cannot be bound to a `let` (`let s = f()[..]` is an error).
- "Violations are compile errors. There is no escape hatch in v1" (`S:L3279`).

**What references may and may not do.**

- **Permitted:** aliasing; pointing anywhere except the interior of a fixed-mode ADT payload (`S:L678-688`).
- **Restricted:** a reference into a grow-shrink array may be held only in variables (`S §5.2`). Inside recursive cycles, only references into globals, pool parameters or outer non-cycle locals may be stored (`I §3.5` rule 4, `CycleStorable`, `typecheck_flow.h:1123`).
- **Not available:** pointer arithmetic, casts, references to references.

**Type-safe reuse instead of freeing (`S §5.3`, `S §5.4`, `S §9.4`).** For non-nested lifetimes, `reusable` pools pair a grow-only array with a hidden freelist (`alloc_ref`/`free`). Elements are never actually freed, so a stale reference reads "a different value of the correct type": a logic bug, never memory corruption. Limited arrays behave the same way under pop/push.

### 3.3 Bounds-check elimination (`S §10.5`, `I §5`, `bce.h`)

- **Baseline.** Every index is checked with one unsigned compare, `GS_IDX` (`runtime.h:194-196`).
- **The pass.** `BCE::RunAll` (`bce.h:3296`) runs after the optimizer and marks `Index::nobc`/`SliceExpr::nobc`. Codegen then omits the check (`codegen_values.h:660-661`).
- **Domain.** A flow-sensitive difference-constraint domain `l <= r + c` over bases (variables, place lengths, constants), queried by Bellman-Ford shortest path. It is capped at 200 facts per state (`I §5.1`).
- **Generations.** Kills open a new generation rather than deleting facts. Grow/shrink operations bridge generations with `old <= new` or `new <= old` (`I §5.2`).
- **Language features that make the pass strong:**
  - The 2^48 maximum size makes `len+1`, `len*2` and `i*elemsize` overflow-free by axiom (`S §10.4`, `S:L3583-3613`).
  - Grow-only arrays are monotone by type, so a bound holds across `push`.
  - Static roots mean a call cannot touch arrays it has no path to.
  - Index types `u32 in a` are validated once and never re-checked, because `a` may never shrink (`S §3.11`).
- **Interprocedural.** `ComputeEffects` (`bce.h:3189`) produces per-function effect summaries as a call-graph fixpoint. `SeedEntryFacts` (`bce.h:2057`) gives a callee the meet of its call sites' facts (`I §5.8`).
- **`checkfree fn`** turns a surviving check into a compile error (`S §7.14`).
- **Known gaps (`I §5.12`).** Indices loaded from array contents (`dist[q[i]]`), `u64` variables, disjunctive loop exits, and anything after a shrink.
- **Soundness testing.** `bench/bce_fuzz.py` differential-fuzzes BCE on/off and has found real bugs (`I §5.11`).
- **Sound by spec.** BCE must be sound, and program acceptance never depends on it, except for `checkfree` (`S:L3620-3627`).

### 3.4 What is NOT guaranteed

**Runtime aborts still exist (`S §9.3`).** Out-of-bounds index (where not elided), pool `free` out of range, capacity overflow, shrinking below empty, relative-offset overflow, division by zero, `assert`, guard-page hits and native stack overflow all abort. None of them are catchable, except inside contained workers.

**Debug-only checks.**

- Integer overflow and value-changing `as` conversions are checked only under `GS_DEBUG`. Release builds wrap (`S §9.3`, `I §7` "Integer semantics").
- ADT tag validity is checked only in debug builds. Release builds use `__builtin_unreachable`/`__assume(0)` (`runtime.h:198-205`).

**Logic-level staleness is allowed by design.** Pool slots and limited arrays can hand out stale-but-typed values (`S §9.4`). Freeing a slice twice "can then hand the same elements out twice" (`S:L1702-1703`).

**The FFI is trusted.** `extern fn` obligations (no retention of borrowed arguments, no shrinking builders, no invalidating lengths) are "obligations on C, not proofs the Goose compiler performs" (`I:L4618-4629`).

**Allocation failure is not recoverable.** Exceeding a reservation is a process abort, and so is a Windows commit refusal (`I §7` "Data stacks", `owning_dynamic_data.md:118-122`).

**Implementation soundness is not established.**

- `I §11` lists nine audit findings, all marked "Resolved", including use-after-shrink through slice-parameter stores (item 3) and a BCE kill missed through field defaults that allowed a stale read (item 2).
- `docs/design/analysis_inventory.md` §1 grades the lifetime analyses C to D ("structurally fragile"). It says that "of the 60 most recent commits, about 30 fix a construct one of these analyses did not cover", and that the lifetime family is ~5,000 lines of checker code.
- There is no formal model or proof in the repo. I saw none, but did not search exhaustively (inferred).

**Behavior is unsound when flags are used.** `--unsafe-no-rf-check` (`I §1` flags) exists for measurement. The spec mentions a "designated unsafe-fast build" that disables bounds checks (`S:L3287`). I found no such flag in `main.cpp` or `runtime.h` (inferred: not implemented).

**Concurrency safety comes from having no shared memory.** Workers are separate program instances with copied globals. Only *flat* values cross through typed queues (`S §11.2`). Data races are impossible because sharing is impossible, not because the type system controls it.

---

## 4. Compiler pipeline

### 4.1 Phases

The driver is `main.cpp`. All phases are headers in one translation unit (`I §1`, `I:L58-71`):

| Phase | Files / entry | Output |
|---|---|---|
| Parse | `lexer.h`, `parser.h`, `ParseProgram` | `Ast` (append-only tables, no destructors) |
| Resolve | `resolve.h` `ResolveTypeNames` | resolved type kinds, aliases removed |
| Typecheck | `typecheck*.h` `TypeCheckProgram` (`main.cpp:1298`) | one `FnSpec` per specialization with a *cloned, annotated body*; the store record; all diagnostics |
| Optimize | `optimize*.h` `Optimizer` (`main.cpp:1301`) | inlining (`TryInline`, `optimize.h:776`), constant propagation, folding, base-case inlining, accumulator TRE, loop shaping |
| BCE | `bce.h` `BCE::RunAll` (`main.cpp:1313`) | `nobc` marks, `fixedlen`/`lenbound`, `hoistrefs`/`hoistfields` |
| Codegen | `codegen*.h` `CodeGen` (`main.cpp:1359`) | one C file |
| JIT | `jit.h` | the program run in-process through libtcc |

### 4.2 IR

There is **no separate IR**. Every pass is a set of virtual methods on AST `Node`s (`Check`, `Opt`, `BceWalk`, `CgX`, ...). The per-specialization unit is a cloned AST body. The inliner copies with `Cp1`, remapping `VarDef`s (`I:L73-89`).

### 4.3 Where the analyses live

**Typechecking.** It runs in call-graph order from `main`, then each `thread_fn`, then unreached eligible functions (`I §3.1`). A function is checked once per specialization, inside the call that first reaches it (`CheckSpecBody`, `typecheck_calls.h:2710`). The typechecker recurses as deep as the program's compile-time call path. For that reason the compiler runs on a dedicated 64 MB-stack thread (`RunOnCompilerStack`, `main.cpp:1654`, `I:L91-100`).

**Lifetime, store and shrink analysis** happens inside typechecking:

- `FitsAt`, `RecordStore`, `StoredIntoGrowShrink` (`typecheck_flow.h:162`), `CheckGrowShrink` (`typecheck_builtins.h:1035`), `NoteShrink`, `NoteLiveShrink`.
- Recursive cycles are checked "in rounds" with predicted return roots (`I §3.11`, `S App. B` 0g).
- Loops are re-checked to a fixpoint (`CheckLoopPasses`; `analysis_inventory.md` §7).

**Stack counting and top-cache planning** happen in codegen: `BoundStacks`, `PlanTopCaches`, `ExpandTopMarkers` (a text pass over the emitted C body, `I §6.10`).

**Three separate storage models.** The analysis inventory notes that the checker, BCE and codegen each keep their own "what may this reach/alias/mutate" model with separate call-graph traversals, and recommends unifying them (`analysis_inventory.md` §1, point 3).

### 4.4 Compile-time cost characteristics

These are mostly inferred:

- Monomorphization per root class and per destination kind multiplies specializations. The repo gives no specialization-count measurements.
- `I §10` notes one option that was not taken because it would cost "one specialization per distinct global passed" (`I:L5888-5894`).
- Polymorphic recursion is bounded: there is an error for runaway type growth past 16 levels (`S:L299-302`).
- The only compile-time number I found concerns the runtime split: ~60 ms saved per MSVC compile by moving the runtime into a separate object (`I:L5289-5291`).

---

## 5. Allocation restrictions: what programs cannot express

Each row cites the source rule and, where one exists, the documented workaround.

| Cannot express | Rule | Workaround in Goose |
|---|---|---|
| Heap allocation with arbitrary lifetime (`new`/`Box`/`malloc`) | `S:L76` | `reusable` pool (fixed-size slots, never truly freed), `reusable[]` slice pool (first-fit runs, B+tree freelist; `S §5.4`, `I §7` "Slice pools") |
| Growable array of growable arrays (`Vec<Vec<T>>`, `vector<string>` that each grow) | resizables never array elements (`S:L411-415`) | slice-pool runs (`owning_dynamic_data.md`); "dynamic stacks" are future work (`S §11.3`) |
| More than one growable field per struct | `S:L426-427` | separate locals; future "memmove-on-insert" (TODO 8) |
| Recursive functions that own growable data across the recursive call | `S §7.8` | pass scratch from outside the cycle with the `mark`/`resize(mark)` pattern |
| Returning a reference to a callee-local, or storing a shorter-lived reference into longer-lived storage | `S §9.2` | none (compile error); return by value with in-place construction |
| Storing references into grow-shrink arrays | `S §5.2` | use grow-only arrays or indices |
| Shrinking a grow-only array while views into it are live | `S §5.1` | end views first; restructure |
| Releasing memory back to the OS mid-scope / per object | no decommit (`owning_dynamic_data.md:123-124`; runtime grep, inferred) | scope exit resets the bump pointer, but pages stay committed |
| Cyclic object graphs with owning edges | ownership is purely scope-based; cycles only through non-owning references or relative references within a pool (`S:L429-435`) | links in one pool (`T&<u32 in pool>`) |
| Moves of resizables (O(1) transfer of ownership) | `S:L1042-1045`, TODO 3 | `copy(x)` (O(n)) or construct at the destination |
| Escaping closures, function pointers, vtables / trait objects | `README.md:316-322, 406-407` | ADTs + case functions (tag dispatch), compile-time function values |
| Sharing mutable memory between threads; atomics; locks | `S §11.2` | typed queues of flat values (copied) |
| Indexing arrays of variable-size elements | `S:L364-370` | a side array of references (`T&<u32 in pool>`) |
| Referencing into a fixed-mode ADT payload | `S:L678-679` | variable-mode ADT (not overwritable) |
| Copying values that hold self-relative references | `S §3.9`, TODO 16 | the pool-relative form |
| Recoverable out-of-memory | `I §7`; `owning_dynamic_data.md:121-122` | none (abort) |
| Separate compilation / dynamic libraries of Goose code | whole-program only (`S §10.1`, `README.md:407`) | `export fn` C wrappers (`S §7.11`) |

The README's own cost list (`README.md:396-409`):

- "You think about where data lives."
- Recursion cannot keep growable data across a call.
- Arrays of variable-size elements do not index.
- There are no escaping closures, function pointers or dynamic dispatch beyond ADT tags.
- Compilation is whole-program only.
- A stale pool slot gives logic bugs.

---

## 6. C backend

### 6.1 Shape of the output

- **One C file per program.** The runtime is split in two (`I §7`):
  - `runtime.h`: macros and inline helpers the program's own translation unit needs.
  - `runtime_impl.h` + `runtime_threads.h` + `runtime_os.h`: OS layer, region reservation, printing and threads, emitted separately by `--emit-runtime`. `--standalone` writes one self-contained file, which is also the form the TinyCC JIT compiles.
- **Version pinning.** `GS_RUNTIME_VERSION` (a hash) renames `gs_rt_start` so that mismatched runtimes fail to link (`I:L5281-5285`).
- **Target assumptions.** 64-bit little-endian hosts with unaligned packed access (`I §6.1`, `I:L4196-4197`). Big-endian hosts abort in serialization (`S:L4187-4189`).
- **Type emission.** Fixed types become `#pragma pack(1)` C structs. Fixed arrays are wrapped in structs. Slices are `{data, len}`. Plain references are pointers (`I §6.1`).
- **Name mangling.** Program names get a `_g` suffix, with collision-suffixed mangling (`I:L4262-4270`).
- **Function values are spliced inline.** There are no function pointers in the output (`I §6.6` `EmitFvCall`).
- **Locals are declared at function scope.** Every aggregate-typed C local is declared at the top of its C function to avoid an MSVC 19.44-19.51 miscompilation (`S App. E` "C locals at function scope").

### 6.2 Stack frames and cleanup

There are no destructors. "Cleanup" is restoring each stack's `top` to the watermark taken at scope entry.

- `CScope` mirrors C braces. Each nonfixed local's base pointer doubles as the watermark.
- Every exit path (fallthrough, `break`, `continue`, `return`, `return from` propagation) emits the restores of the scopes it leaves, in reverse order (`EmitExitRestores`, `codegen_frames.h:777-780`; `I §6.2`).
- Each statement gets its own scope (`SC_STMT`) so temporaries die at the statement's end.

Codegen template, verbatim (`codegen_frames.h:765-768`, `codegen_stmts.h:776-786`):

```cpp
// codegen_frames.h -- emitted at every scope exit, innermost save first
inline void CodeGen::EmitRestores(const CScope &s) {
    for (auto i = (int)s.saves.size() - 1; i >= 0; i--)
        L(Top(s.saves[i].first), " = ", s.saves[i].second, ";");
}

// codegen_stmts.h -- declaring a resizable local (and a reusable pool's freelist)
L("gs_rhdr ", name, " = { ", Top(stk), ", 0 };");
if (own) SaveBase(forlocal, stk, cat(name, ".base"));
if (d->reusable) {
    auto flstk = AllocStk(forlocal);
    auto fln = Unique2(cat(name, "_fl"));
    L("gs_rhdr ", fln, " = { ", Top(flstk), ", 0 };");
    SaveBase(forlocal, flstk, cat(fln, ".base"));
```

Push of a fixed-size element on a resizable (`codegen_builtins.h:605-620`, plus `EmitValStore` in `codegen_construct.h:13-18`, which stores at `Top(stk)` and then `Bump`s it): `T *e = (T *)top; *e = v; top += sizeof(T); hdr.len++;`. This matches the spec's `*(T*)S.top = e; S.top += size; h.len++` (`S:L4239-4240`). There is no capacity check. The guard gap catches overflow.

**Reconstructed example.** The following is **assembled by hand from the templates above, not output from a real compiler run**. For `fn f() { var xs: i64[>..] = []; for i in 10 { xs.push(i); } print(xs.len); }` the emitted C has roughly this shape (inferred):

```c
void f_g(int64_t gs_sp) {
    gs_rhdr xs = { GS(gs_sp + 0)->top, 0 };      /* base doubles as watermark */
    uint8_t *top0 = GS(gs_sp + 0)->top;          /* top cached in a local (I §6.10) */
    for (int64_t i = 0; i < 10; i++) {
        int64_t *e = (int64_t *)top0;  *e = i;  top0 += 8;  xs.len++;
    }
    GS(gs_sp + 0)->top = top0;                   /* flushed before calls/exits */
    /* ... print xs.len ... */
    GS(gs_sp + 0)->top = xs.base;                /* scope exit: the "free" */
}
```

### 6.3 Bounds checks

`codegen_values.h:660-661`:

```cpp
if (nobc || (il && statlen >= 0 && il->val >= 0 && il->val < statlen)) ix = idx;
else ix = cat("GS_IDX((int64_t)(", idx, "), ", v.len, ", ", LocArgs(ln), ")");
```

The macro behind it (`runtime.h:194-196`):

```c
#define GS_IDX(i, n, f, l) \
    ((uint64_t)(i) < (uint64_t)(n) ? (i) \
                                   : (gs_idxfail((int64_t)(i), (n), (f), (l)), (int64_t)0))
```

`gs_idxfail` is declared no-return, so the C compiler keeps the check's path short (`I:L5293-5295`).

Codegen also merges identical leading checks in both arms of an `if` into one check placed before the branch (`LeadingCheck`/`EmitHoistedCheck`, `I §5.9`).

### 6.4 Calling convention (`S App. C.3`, `I §6.4`)

**Fixed values.**

- Fixed parameters and returns up to 4096 bytes are native C values. The first fixed return is the C return value, and others use out-pointers.
- Larger fixed values go through data-stack copies or out-pointers.

**Nonfixed and reference values.**

- Nonfixed by-value parameters are built by the caller on the parameter's assigned stack. The header is passed by value.
- Slices and fat references are passed as separate scalar C parameters. This avoids Windows x64/AArch64 by-reference struct passing, which forced reloads (`I:L4368-4383`).

**Hidden parameters**, in order after the declared ones: free variables of nested functions, then result channels (a destination `gs_stack *` per nonfixed result), then `gs_sp`.

**`return E from f`.** A thread-local `int32_t gs_rf` discriminant, checked after each call on a propagation path (`if (gs_rf) {...}`, `EmitRfCheck`, `codegen_calls.h:413-440`). It costs one load and one never-taken branch per call. The in-flight value is built at the target's destination stack and `memmove`d down at the catch (`I §6.6`).

### 6.5 Runtime support (`I §7`)

| Area | What it contains |
|---|---|
| Data stacks | regions reserved at thread-program start (`gs_stack_block`, `runtime.h:888-893`); a fault handler that tells commit from overrun; an overrun message written without stdio |
| Native stack overflow | Windows `SetThreadStackGuarantee` plus a vectored handler; POSIX `sigaltstack` plus a bounds check against `pthread_getattr_np` |
| Integer ops | `gs_add_i32`-style helpers: functions under `GS_DEBUG`, release macros otherwise. Division and modulo are always functions and always zero-checked; `%` is Euclidean |
| Varints | ULEB128 / zigzag helpers |
| Workers and queues | workers, typed queues as malloc'd byte-image nodes, contained workers via `setjmp`/`longjmp` |
| Slice-pool freelist | B+ tree with 512-byte nodes, kept on the freelist's own data stack |
| Byte search | SSE2/SSSE3 byte scanning; CPUID through inline `__asm__` (`runtime.h:235-247`) |
| Copy/compare | `gs_memcpy`/`gs_memeq` inline fast paths up to 256 bytes |

### 6.6 Two backends

- **External C compiler.** The generated C goes to an external compiler (MSVC, clang or gcc). `goose build` defaults to `-O2` inlining.
- **In-process TinyCC.** TinyCC compiles the same C inside the compiler. It does not optimize, and it refuses programs that use workers because it cannot place TLS (`S App. E`, `I §1`).

---

## 7. Performance claims

### 7.1 (a) Benchmarks and methodology

**Main suite.** Sixteen benchmarks in `bench/goose/`, `bench/cpp/` and `bench/rust/`, run by `bench/run_bench.py` (1,057 lines) at three sizes (`bench/design.md` "The suite as built").

- **Comparison tiers:** C++ "idiomatic" (`unique_ptr`, `vector<string>`, `unordered_map`, `std::variant`) and "expert" (reserved arenas with indices, `string_view`, CSR, open addressing), plus the "best safe Rust row" (`bench/results.md` "Across all benchmarks").
- **Timing:** best of 3 whole-process wall-clock runs after 2 warm-ups. Input generation and teardown are inside the timing. Checksums are cross-verified (`bench/results.md` "How to read this").
- **Memory:** peak working set.
- **Environment:** one machine, an AMD Ryzen 9 9950X3D on Windows 11. MSVC 19.51 `/O2` and clang-cl 22.1.3 `/O2` both build the same generated C. Rust 1.98.0 with `-O -C codegen-units=1` (`bench/results.md` "Environment").
- **Methodology deviation.** `bench/design.md` "Measurement notes" recommends pinning to a core and "report a distribution rather than a best of N". The suite as run does not pin and reports best of 3 (`bench/summary.md:68-71`).
- **Selection per tier.** The aggregate picks the fastest Goose variant per backend and tier, and the smallest variant for memory. So "these are not the resource profiles of a single implementation" (`bench/summary.md:57-66`).

**External suites.**

- `bench/plb/` re-implements Programming-Language-Benchmarks problems. Runs are pinned to CPUs 16-31, best of at least 5, clang `-O3 -march=broadwell` (`bench/plb/README.md`).
- `bench/kostya/` (LangArena, 50 tests), `bench/kostya2/` (kostya/benchmarks: 5 tests) and `bench/bddicken/` exist with runners. I did not read their result tables.

**Compiler-change studies.** `bench/plan.md` and `bench/adoption.md`: per-optimization A/B runs, interleaved, best of 5, with a noise floor stated as 5-10% (~15% on `lru` and `graph`).

### 7.2 (b) Claimed results

These are the repo's claims, not verified here.

- **`README.md:24-27`:** "3.3x the speed of idiomatic C++, 1.16x hand-optimized C++ and 1.12x the best safe Rust, on 1.9x, 1.3x and 1.2x less memory".
- **The README's own table (`README.md:386-390`) and `bench/summary.md:1-5` (2026-10-16 run):** 3.51x / 3.54x vs idiomatic C++, 1.24x / 1.26x vs expert C++, 1.13x / 1.19x vs safe Rust (MSVC / clang). Memory: 1.95x, 1.32x and 1.25x less. **These figures disagree with the README's prose numbers above.**
- **Per-benchmark extremes (`bench/results.md` aggregate table):**
  - `records`: 2.26x / 2.02x vs Rust on 3.98x less memory.
  - `bintrees`: 15.6x vs idiomatic C++.
  - Losses: `lru` 0.81x (MSVC) vs Rust, `scene` 0.82x (MSVC), `calc` 0.91x / 0.89x vs Rust.
- **PLB (`bench/plb/README.md`):** binarytrees ratio 15.04 and merkletrees 6.04 vs Rust; nbody 0.78 (a loss); most others between 1.0 and 2.5.

### 7.3 (c) What is demonstrated vs asserted

**Demonstrated, within the repo's own setup:**

- Reproducible-looking harnesses with cross-checked outputs.
- Losses are reported (`calc`, `lru`, `scene`, `nbody`, `secp256k1`).
- A/B studies tie specific compiler changes to specific row changes (`bench/adoption.md`).

**Caveats the repo itself states:**

- `bintrees` "is really an allocator benchmark" (`bench/design.md` "Candidate algorithms"). The Goose version uses a function-local pool reset in one store, against C++ / Rust per-node allocation in the idiomatic rows.
- The PLB binarytrees and merkletrees ratios "say as much about Windows' heap as about Rust" (`bench/plb/README.md`).
- The `lru` comparison is against `0-unsafe` Rust.

**Not established:**

- **Single machine, single OS.** The comparisons run on Windows only, where `Box`/`malloc` go through `HeapAlloc`. Generalization to Linux/glibc or jemalloc is not shown. `bench/design.md` asked for mimalloc/jemalloc rows, and the aggregate table has none. I looked only at the aggregate and summary, so this is partly inferred.
- **Benchmark authorship.** The suite, the Goose implementations and the comparison implementations were written by the same project (AI-authored per `README.md:605-623`). Fairness of the C++/Rust rows is asserted, not independently reviewed.
- **Causal attribution.** "These gains come from Goose's data layouts and memory model" (`README.md:27-28`) is asserted. `bench/summary.md:36-37` says the refresh "does not re-establish the older diagnostic claims about which individual compiler mechanisms caused them".
- **Mixed variants.** The aggregate mixes the best Goose variant per tier with separately chosen memory variants (`bench/summary.md:57-66`). A single program does not hit both columns at once.
- **Unmeasured overheads.** No measurement covers compile time, specialization blow-up, or memory retained because stacks never decommit.

---

## 8. Limitations for general-purpose systems and kernel development

**Platform dependence.**

1. **It requires a virtual-memory OS.** The model depends on multi-GB address-space reservation, lazy commit and guard pages, and on fault handlers (signal/vectored) to turn overflow into an abort (`S §10.4`, `I §7`).
   - A kernel, a bare-metal target or an MMU-less microcontroller has none of this by default.
   - The documented fallback for platforms without reservation is "index-based references + bounds-checked growth, with reduced performance" (`S:L3579-3581`). It is unimplemented (`docs/design/wasm.md`: "A proposal, not implemented").
2. **There is no heap, and memory is never returned to the OS.** Long-lived services with non-nested lifetimes (caches, connection tables, page caches, schedulers' task lists) fall back to pools that never shrink and never decommit (`owning_dynamic_data.md` "What the slice pool costs").

**Failure and resource handling.**

3. **Out-of-memory is fatal.** Exhaustion of a reservation, a commit failure, native stack overflow and all §9.3 checks abort the process. A kernel cannot abort on OOM.
4. **Resources need manual cleanup.** There are no destructors, RAII or move semantics. File descriptors, locks and device handles get no scope-based cleanup. The `resource<U>` proposal explicitly leaves cleanup unresolved (`owning_dynamic_data.md` "Cleanup remains unresolved").

**Concurrency.**

5. **Shared memory is excluded.** There are no atomics, locks, memory orderings or shared buffers (`S §11.2`). Kernels, lock-free structures, DMA rings and SMP schedulers cannot be expressed. Thread-spawn copies globals, and every worker reserves its own stack block.

**Low-level control.**

6. **There are no function pointers or vtables.** Interrupt/trap vectors, driver operation tables and callbacks registered with C cannot be Goose functions except through `export fn` wrappers (`S §7.11`).
7. **Hardware-level features are missing.** There is no inline asm, `volatile`/MMIO, alignment control, or explicit memory layout beyond `pad` (I found none in the spec, inferred from grep). Packed unaligned layouts are the default (`S App. C.2`). Alignment is an open item (TODO 10).
8. **Targets are 64-bit little-endian only** (`I §6.1`).

**Compilation model.**

9. **Compilation is whole-program and closed-world.** There is no separate compilation, no dynamic loading of Goose code, and no stable ABI for Goose types (`S §10.1`).
10. **Recursion is restricted.** Recursive functions cannot hold growable locals across recursion (`S §7.8`). Tree and graph algorithms must thread external scratch.

**Trust in the implementation.**

11. **The lifetime checker is complex and self-described as fragile.** The analysis inventory grades the lifetime family "C"/"D" and reports that half of recent commits fix holes (`analysis_inventory.md` §1, §3). This implementation is a weak basis for a kernel's trusted computing base.
12. **Data-stack count is bounded.** It is capped at 1024 per program by default (`runtime.h:74`), and each worker pays address-space reservations for its own stacks.

---

## 9. Techniques generalizable to Nova (which has heap allocation)

For each technique: the guarantee, implementation complexity, runtime overhead, compile-time overhead, limitations and suitability. Complexity and overhead ratings are my estimates (inferred) unless cited.

### 9.1 Compiler-managed data stacks for scoped dynamic values

- **Guarantee:** O(1) allocation and O(1) bulk free per scope; no allocator metadata; no fragmentation. Combined with lifetime checking, no use-after-scope.
- **Implementation:** Medium. Needs static per-scope watermarks, exit-path restores (`EmitExitRestores`) and a stack index per live dynamic local. Goose's hidden `gs_sp` + constant-offset scheme (`I §6.2`) is simple to emit.
- **Runtime overhead:** a pointer bump per allocation, a store per scope exit, and one hidden argument per stack-using function. Address-space reservation per stack (2 GB default) plus guard gap. Pages are never decommitted unless Nova adds that.
- **Compile-time overhead:** low for assignment. Stack counting is an SCC pass (`BoundStacks`).
- **Limitations:** nested lifetimes only. Needs VM reservation or an explicit growth check. The recursion restriction is required to keep N static.
- **Suitability for Nova:** **High** as the implementation of "temporary buffers", "scoped reset" and "bump allocation" in brief §5.2, *alongside* a heap.
  - Nova can relax the recursion rule by falling back to heap allocation (inferred) instead of rejecting.
  - Kernel targets need a no-MMU mode: fixed-size regions with explicit `if (lim - top < n)` checks, which `wasm.md` §2 shows touches every growth site.

### 9.2 "At most one resizable per stack, on top": non-moving growth

- **Guarantee:** growable arrays never relocate, so interior references stay valid across `push`. Growth needs no capacity check.
- **Implementation:** Medium. The analysis rejecting two live resizables on one stack is simple. The real complexity is in the shrink rules (`S §5.1-5.2`), graded "C-"/"D" in `analysis_inventory.md` §3 rows 6-7.
- **Runtime overhead:** none beyond the bump, but one reserved region per simultaneously live growable.
- **Compile-time overhead:** moderate (shrink liveness and the store record).
- **Limitations:** no growables as array elements; one growable per struct; depends on VM.
- **Suitability:** **Medium.** For Nova, a cheaper equivalent is a heap-backed "stable vector" type: chunked, or reserved-VM per object. That gives the reference-stability guarantee without whole-program stack assignment (inferred). Goose's own future "dynamic stacks" proposal moves in this direction (`owning_dynamic_data.md` "Future option A").

### 9.3 Grow-only vs grow-shrink split in the type

- **Guarantee:** a type-level monotonicity fact. A bound proven before `push` holds after it, which feeds BCE directly (`S:L3643-3647`). It also lets references into grow-only storage outlive arbitrary growth.
- **Implementation:** Low to define. Shrink legality needs liveness of views.
- **Runtime overhead:** none.
- **Compile-time overhead:** low for BCE; moderate for shrink checks.
- **Limitations:** users must choose the container kind up front.
- **Suitability:** **High.** It is cheap and valuable even on a heap: an append-only arena/vector type whose elements are never invalidated, for example backed by chunked or reserved memory.

### 9.4 Root inference by monomorphization (lifetimes without annotations)

- **Guarantee:** references never outlive their owner's scope, with no annotation syntax. Precision is per call site because functions are specialized per root class (`S §9.2`, `S §10.2`).
- **Implementation:** **High**, and fragile in practice. About 5,000 lines of checker code; roughly half of recent commits fix holes; three duplicated storage models (`analysis_inventory.md` §1). Recursion needs round-based fixpoints (`I §3.11`).
- **Runtime overhead:** none, but specialization increases code size (not measured in the repo).
- **Compile-time overhead:** potentially high. There is a specialization per (types × root classes × destinations × ...), and checking recurses along compile-time call paths (a 64 MB compiler stack, `I:L91-100`).
- **Limitations:**
  - Needs whole-program compilation.
  - No separate compilation or stable library ABI.
  - Errors are reported in callee bodies "wherever a call site passes ..." (`I §10`), which is hard for users.
- **Suitability:** **Low as a whole**, **Medium in part.** For Nova, better options are inference within a function plus *elided-by-default but declarable* lifetime/region parameters at module boundaries. Goose's "depth outlives" rule and its merged-root sets (`RootAlt` alternatives) are useful internal representations (inferred).

### 9.5 Construction directly into destination storage (guaranteed RVO, request kinds)

- **Guarantee:** fresh values are built in their final location, through calls and branches. `v.push(f())` and returning a growable array cost no copy (`S §4.3`, `S §7.3`).
- **Implementation:** Medium to high. Needs:
  - destination passing (`Dst`) through every value-producing node;
  - hidden destination arguments;
  - value-form vs element-run (`_er`) twins;
  - NRVO detection;
  - "exit while construction is open" relocation (`I §6.5`).
  The repo needed an audit fix for remaining copy paths (`I §11` item 9).
- **Runtime overhead:** negative (copies removed); an extra hidden parameter per call.
- **Compile-time overhead:** moderate, since some functions are compiled twice (`_er` twins).
- **Limitations:** with packed varint prefixes, relocation is sometimes needed anyway (`S:L1177-1183`).
- **Suitability:** **High.** Destination-passing style works with a heap too: a heap destination is just another `Dst` kind. Guaranteed NRVO for single-named-result functions is a cheap, user-visible win (inferred).

### 9.6 Contiguous, pointer-free nested layouts (inline variable-size fields, varint lengths, variable-mode ADTs)

- **Guarantee:** a record with strings and sub-arrays is one block, so it is cache-friendly, cheap to free and serializable by `memcpy` (`README.md:135-169`).
- **Implementation:** Medium. Needs size walkers (`gs_size_<T>`), cursor-based field access past variable fields, and in-place construction ordering (`S §1.3(4)`).
- **Runtime overhead:** variable-size fields make later fields O(k) to reach. Arrays of such elements are sequential-only.
- **Compile-time overhead:** low.
- **Limitations:** no random access; no in-place growth of an inner field; no overwrite of variable-mode values.
- **Suitability:** **Medium.** Valuable as an opt-in "packed/flat" type family for serialization, network messages and immutable records. It should not be Nova's default object model.

### 9.7 Relative (offset) references and pool-relative links

- **Guarantee:** typed 1/2/4-byte links. Structures are position-independent. Loading untrusted bytes goes through a generated verifier that checks every tag, length and link (`S §3.9`, `S §9.4`, `README.md:204-251`).
- **Implementation:** Medium. The audit found link-encoding bugs, such as offset 0 colliding with null (`I §11` item 6).
- **Runtime overhead:** add/sub per load/store. The `lru` workload measured relative links at 1.5x the cost of indices on a relink-heavy path (`bench/design.md` "Stack discipline friction").
- **Compile-time overhead:** low.
- **Limitations:** copying self-relative values is forbidden; links stay within one region or pool.
- **Suitability:** **Medium to high** as a library or type feature for arenas and zero-copy file formats in Nova (inferred).

### 9.8 Type-safe reuse pools (`reusable`, `reusable[]`)

- **Guarantee:** no memory corruption or type confusion after a "free". A stale reference reads a valid value of the same type (`S §5.4`, `S §9.4`).
- **Implementation:** Low for slot pools (a freelist on a side stack). Medium for slice pools (a B+ tree freelist, `I §7`).
- **Runtime overhead:** freelist push/pop; a range check when a slice cannot be statically attributed to a pool.
- **Compile-time overhead:** low.
- **Limitations:** logic bugs (ABA, double-free handing elements out twice) remain; memory is never returned.
- **Suitability:** **High** as a safe arena/slab primitive in Nova, complementary to owned heap objects. Adding generation counters to handles would turn silent staleness into detectable errors. Goose notes generation checks protect handles but not escaped raw slices (`owning_dynamic_data.md:210-211`).

### 9.9 Bounds-check elimination with language-supplied facts

- **Guarantee:** sound removal of index checks. `checkfree` makes "no checks remain" a compile-time contract (`S §7.14`).
- **Implementation:** Medium to high. ~2,300 lines, graded structurally sound (`analysis_inventory.md` §1). Difference constraints, generations, effect summaries, entry facts.
- **Runtime overhead:** none; it removes checks.
- **Compile-time overhead:** moderate. Bellman-Ford per query with a 200-fact cap (`I §5.1`).
- **Limitations:** indirect indices, `u64`, disjunctive loop exits (`I §5.12`).
- **Suitability:** **High.** These pieces are directly portable to Nova:
  - **Spec-level size cap.** 2^48 makes length arithmetic provably non-overflowing.
  - **Index types** (`u32 in a`) and **bound types**.
  - **`checkfree`**.
  - **Fuzz-differential testing** of the BCE pass on vs off (`bench/bce_fuzz.py`).

### 9.10 `return … from` (multi-frame return via a discriminant)

- **Guarantee:** statically checked non-local return with no unwinder. Scope watermarks restore along the path (`S §7.9`).
- **Implementation:** Medium. A thread-local discriminant, per-target channels, and destination landing (`I §6.6`).
- **Runtime overhead:** one load and one never-taken branch per call on propagation paths.
- **Compile-time overhead:** low to moderate (a whole-program "needs" analysis).
- **Limitations:** in the current implementation, values carrying references across a long return may only be rooted in globals or static data (`I §10`).
- **Suitability:** **Medium.** In a language with heap objects and destructors, intermediate frames must run cleanup, which a discriminant check supports naturally (inferred). Nova may prefer a `Result` type, possibly with this as an optimization.

### 9.11 Hidden stack index / fat references for "which arena to grow"

- **Guarantee:** a callee can grow a caller-owned growable without knowing statically which region it is in.
- **Implementation:** Low to medium (`gs_rref { hdr, stk }`, `I §6.1`).
- **Runtime overhead:** one extra word per reference to a growable. Top-of-stack caching is blocked when aliasing cannot be ruled out; measured at 8-13% on push-heavy callees before a fix (`bench/plan.md` §2.1).
- **Compile-time overhead:** low.
- **Suitability:** **Medium.** It is the natural representation for "allocator/arena handle" parameters in Nova, and is a strong argument for passing allocators explicitly or implicitly as context (inferred).

### 9.12 Share-nothing workers with flat-value queues

- **Guarantee:** data-race freedom by construction (`S §11.2`).
- **Implementation:** Medium. Separate program instances, copied globals, typed queues.
- **Runtime overhead:** a `memcpy` per message. Per-worker address-space reservation.
- **Limitations:** excludes shared-memory parallelism, which kernels and many systems need.
- **Suitability:** **Low as the only model; Medium as one option** (for example an actor/channel library) beside a Send/Sync-like typed shared-memory story (inferred).

### Summary table

| Technique | Guarantee | Impl. complexity | Runtime cost | Compile cost | Nova fit |
|---|---|---|---|---|---|
| Data stacks for scoped dynamic values | O(1) alloc/free, no use-after-scope | Medium | bump + restore; VM reservation | Low | High (with heap fallback, no-MMU mode) |
| Non-moving growth (one resizable on top) | stable interior refs | Medium (high for shrink rules) | ~0 | Moderate | Medium (prefer heap "stable vector") |
| Grow-only/grow-shrink type split | monotone lengths for BCE and ref stability | Low | 0 | Low | High |
| Root inference by monomorphization | lifetimes w/o annotations | High, fragile | 0 (code size ↑) | High | Low (borrow ideas, not scheme) |
| In-place construction / guaranteed NRVO | no construct-then-copy | Medium-high | negative | Moderate | High |
| Inline variable-size layouts | one block per record | Medium | O(k) field access | Low | Medium (opt-in) |
| Relative references + verifier | compact, position-independent, safe load | Medium | add per hop | Low | Medium-high |
| Type-safe reuse pools | no corruption after free | Low-medium | freelist ops | Low | High (+ generations) |
| BCE + index types + `checkfree` | sound check removal, contract | Medium-high | negative | Moderate | High |
| `return … from` | cheap multi-frame return | Medium | load+branch/call | Low-moderate | Medium |
| Fat refs / hidden arena index | grow caller storage | Low-medium | +1 word | Low | Medium |
| Share-nothing workers | race freedom | Medium | memcpy/message | Low | Low-medium |

### Key cautions for Nova (inferred)

- Goose's safety story depends on **having no heap**. Every reference's owner is a lexically scoped variable, so "outlives" reduces to scope depth on a compile-time call path. Adding owned heap objects (moves, `Box`, collections of owners) removes the property that made annotation-free inference tractable. Nova will need ownership/move tracking in addition to scope roots.
- The most transferable results are the *cheap, local* ones:
  - data stacks for temporaries;
  - grow-only arrays;
  - destination-passing construction;
  - index types and BCE with a spec-level size cap;
  - type-safe pools.
- The least transferable is the *whole-program, specialization-based lifetime checker*. Goose's own analysis inventory recommends restructuring it: explicit alternative sets (done), lifetime analysis as a separate per-cycle fixpoint over summaries, and one shared region/effect model (`analysis_inventory.md` §1 recommendations, §7 status).
