# Nova Compiler Architecture (v0)

Status: Milestone 0 proposal. Implementation language: C#/.NET 10 (ADR 0001,
pending approval). Normative semantics: `ownership.md`, `language-spec.md`.

## 1. Pipeline

```text
 source files
     │  Lexer            → Token[] (struct array) + trivia; interned Symbols
     │  Parser           → AST (lossless spans; error recovery)
     ▼
   AST ───── Name resolution   → DefIds, scopes, imports, visibility
     │       Type collection   → struct/enum layouts (abstract), signatures
     │       Type check        → typed HIR; kinds: Copy / Move / Scoped; unsafe ctx
     ▼
   HIR  (typed, desugared tree: `for`, `?`, `using`, `defer`, method calls,
     │   auto-borrows and implicit reborrows made explicit)
     │       Monomorphisation (M6) → concrete HIR instances
     │       NIR build          → CFG
     ▼
   NIR  (place-based CFG, MIR-like; non-SSA)
     │   1. definite-init / move check         (O2–O5)
     │   2. borrow check (NLL regions)         (B1–B5, E1–E2, R1, R4)
     │   3. effect check (kernel profile, C7)  (@noalloc/@nosleep/...)
     │   4. drop elaboration                   (O4: static drops, drop flags)
     │   5. runtime-check insertion            (bounds, overflow — explicit stmts)
     │   6. escape / placement analysis (M5)   (optimisation only)
     │   7. NIR optimisations (M9)             (each with preconditions + tests)
     ▼
   LIR  (C-shaped: C types resolved with TargetInfo, temporaries named,
     │   evaluation order fixed, every UB-prone op replaced by a helper call)
     │       C emitter → .c + .h per package (deterministic text)
     ▼
   C compiler (clang / gcc / CosmoOS cross-toolchain) → .o → link
```

Safety checking is complete before LIR. No later pass may introduce a borrow,
a move, or a drop that the checker has not seen. Optimisation passes on NIR (M9)
must preserve a recorded *drop schedule* and are validated by re-running the
checker on their output in debug builds ("checker as validator").

## 2. Intermediate representations

### 2.1 AST
Concrete syntax with spans. Used for diagnostics, formatting (M10) and the language
server. Immutable class nodes in M1 (clarity first).

### 2.2 HIR — typed high-level IR
- Every expression carries a resolved `TypeId`. Every name carries a `DefId`.
- Desugared constructs:
  - `for x in s` becomes iterator `loop`/`match`;
  - `e?` becomes `match` + `return`;
  - `using`/`defer` become explicit scope-exit actions;
  - method calls become path calls with explicit receiver mode;
  - auto-borrow (`borrow`/`mut` arguments) becomes explicit `Borrow` nodes;
  - implicit reborrows become explicit nodes.
- *Why:* type checking needs tree structure and source correspondence.
  Desugaring here means NIR construction has few constructs to handle.

### 2.3 NIR — Nova IR (ownership IR)
Modelled on Rust MIR. A function body:

```text
locals:   _0: ret, _1.._n: params, then user vars and temporaries (each with a type and a scope)
blocks:   bb0..bbk, each: [statement]* + terminator
statements:
  StorageLive(l) | StorageDead(l)
  Assign(place, rvalue)
  Drop(place)                    -- before elaboration: "drop if init"
  CheckIndex(len_operand, idx_operand, span)   -- inserted in pass 5
  Nop
rvalues:
  Use(operand) | Borrow(Shared|Mut|TwoPhaseMut, place) | BinOp(op, a, b)
  | CheckedBinOp(op, a, b) | UnOp | Aggregate(kind, operands) | Cast(kind, operand, ty)
  | Len(place) | Discriminant(place) | AddrOfRaw(place)
operands: Copy(place) | Move(place) | Const(value)
places:   local (. field | [index_local] | deref | downcast(variant))*
terminators:
  Goto(bb) | SwitchInt(operand, targets, otherwise) | Return | Unreachable
  | Call(func, args, dest, next_bb) | Panic(kind, span)
```

*Why NIR supports ownership checking:* every move, copy, borrow, storage
start/end and drop is an explicit statement at a CFG point. Init analysis and
loan liveness are standard dataflow over blocks. Places make field-level
disjointness (B1) and partial moves (O2) exact.

*Why it supports C generation:* a CFG with explicit temporaries maps 1:1 onto
C labelled blocks and `goto`, so evaluation order is fixed by construction. Drop
elaboration produces plain calls on edges, so cleanup on early return,
`?` and `break` costs nothing extra in the backend.

Storage representation: `Local[]`, `BasicBlock[]` and `Statement[]` stored in
per-function arrays indexed by `int`. Dataflow bitsets are `ulong[]` per block.

### 2.4 LIR — C-level IR
- C types computed with `TargetInfo`: sizes and alignments are cross-checked
  in the generated C with `_Static_assert(sizeof(T) == N)` and `offsetof`.
- Nova arithmetic is mapped onto runtime helpers (`nova_add_i32_chk`, …) or onto
  C operators only when proven UB-free (unsigned arithmetic, comparisons).
- Each NIR block becomes a labelled C block. Locals hoist to the function top
  with explicit initialisation where the C rules require it (`c-backend.md`).
- Exists separately from NIR so that a future native backend or LLVM/QBE backend
  can start from NIR while the C emitter stays dumb.

## 3. Passes, inputs and outputs

| # | Pass | Input → Output | Milestone | Key test corpus |
|---|---|---|---|---|
| 1 | Lexer | text → tokens | M1 | `parse-pass`, `parse-fail` |
| 2 | Parser (recursive descent + Pratt for expressions) | tokens → AST | M1 | precedence tables, recovery |
| 3 | Name resolution | AST → resolved AST | M1 | `check-fail/name-*` |
| 4 | Type check + HIR lowering | → HIR | M1 (prims), M2 (structs/refs), M6 (generics) | `check-pass/fail` |
| 5 | NIR build | HIR → NIR | M1 | NIR snapshot tests (`--dump-nir`) |
| 6 | Init/move check | NIR | M2 | `compile-fail/move-*` |
| 7 | Borrow check | NIR | M2 | `compile-fail/borrow-*` |
| 8 | Effect check | NIR + call graph | C7 | `compile-fail/effect-*` |
| 9 | Drop elaboration | NIR → NIR | M2 | `run-pass/drop-order-*` |
| 10 | Check insertion | NIR → NIR | M1 (overflow/div), M2 (bounds) | `run-fail/*` |
| 11 | Placement analysis | NIR → annotations | M5 | `--explain-allocations` snapshots |
| 12 | Optimisations | NIR → NIR | M9 | equivalence tests |
| 13 | LIR lowering | NIR → LIR | M1 | — |
| 14 | C emission | LIR → `.c/.h` | M1 | golden C + compile with `-Wall -Wextra -Werror -std=c11 -pedantic` |

All passes are deterministic. Ordered collections only. No reliance on `GetHashCode`
order. Debug dumps (`--dump-hir`, `--dump-nir`, `--dump-lir`) are stable text
formats, so snapshot tests are possible.

## 4. Diagnostics

- Error codes `N0001…`. Every code has a long explanation (`novac --explain N0123`),
  as in rustc.
- Borrow errors show three labelled spans: the loan, the conflicting access, and the later use.
- Error recovery: parser recovery at statement or declaration boundaries. Type
  errors produce an `Error` type that suppresses cascades. Borrow checking is
  skipped for functions that fail type checking.
- Machine-readable output `--diagnostics-format=json` for editors and CI.

## 5. Command-line interface (proposed)

```text
novac build [--profile userland|kernel|hosted] [--target x86_64-cosmoos|aarch64-cosmoos|host]
            [--emit c|obj|exe] [-o out] [--cc <path>] [--cflags "..."]
novac check            # type + ownership only
novac build --explain-allocations | --allocation-report | --check-lifetimes   (M5)
novac test             # test runner over tests/corpus
novac --explain N0123
```

`--emit c` is the primary integration mode for CosmoOS: the CosmoOS build
compiles the generated C with its own toolchain and flags
(`cosmoos/build-integration.md`). `--emit exe` drives the host C compiler for
convenience and testing.

## 6. Repository structure

```text
Nova/
  Nova.slnx                    # .NET 10 solution format
  Directory.Build.props        # warnings-as-errors, nullable, deterministic, banned-API analyzer
  global.json                  # pinned .NET SDK
  src/
    Nova.Compiler/
      Syntax/                  # Lexer, Token, Parser, AST, SourceText, Span
      Semantics/               # Symbols, NameResolution, Types, TypeChecker, Kinds
      Hir/                     # HIR nodes, lowering from AST
      Nir/                     # NIR, builder, dataflow framework, printer
      Analysis/                # InitCheck, BorrowCheck, Effects, DropElab, Checks, Placement
      Lir/                     # LIR, lowering
      Backend.C/               # CEmitter, Mangling, RuntimeABI
      Target/                  # TargetInfo (x86_64/aarch64 LP64, host), Profiles
      Diagnostics/             # Diagnostic, codes, renderers (text/json)
    Nova.Cli/                  # `novac` entry point
  runtime/
    include/nova/rt.h          # freestanding core: types, checked-arith helpers, panic hook decl
    core/                      # freestanding C (memcpy-free; no libc)
    hosted/                    # libc-backed: panic=abort, SystemHeap=malloc, Console
    cosmoos/                   # CosmoOS userland glue (C1); kernel adapter (C5)
  lib/
    core/                      # Nova sources: Option, Result, Slice, Box, Allocator, ...  (freestanding)
    std/                       # hosted: String, Vector, Console, IO
  tests/
    Nova.Compiler.Tests/       # xUnit unit tests per pass
    corpus/
      parse-pass/ parse-fail/ check-pass/ check-fail/
      compile-fail/            # ownership/borrow negatives, each with `// error: N....` annotations
      run-pass/ run-fail/      # expected stdout / expected panic kind + exit code
    Nova.TestRunner/           # corpus runner; compiles generated C with clang AND gcc, ASan+UBSan
  benchmarks/                  # M9: Nova vs C/C++/Rust/Zig/Goose, harness + methodology
  examples/
  integrations/
    cosmoos/                   # Makefile fragment, QEMU test glue, port recipe (not in the CosmoOS tree until approved)
  docs/
```

Rationale: the compiler, runtime C and Nova libraries are versioned together
because the runtime ABI between them is internal and unstable until M7. The
`integrations/cosmoos` directory keeps every CosmoOS-specific artefact out of
the CosmoOS repository until the integration interface is approved
(`prompts/cosmoos-native.md` §1).
