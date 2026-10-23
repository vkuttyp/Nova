# Intermediate Representations (as implemented)

Status: describes the code as of M1 increment 1c. The design rationale is in `architecture.md` §2.
That document is the plan; this one is the implementation record.

## 1. AST

`src/Nova.Compiler/Syntax/Ast.cs`. Immutable records with spans. Printed by `novac parse --dump-ast`.

## 2. HIR

`src/Nova.Compiler/Hir/Hir.cs`. It is typed and fully resolved:

- Constant references are replaced by their values.
- Parentheses and expression-bodied functions are removed.
- `else if` becomes nested `if`.
- Assignment targets are locals.

Printed by `novac check --dump-hir`. Static semantics: `type-system.md`.

## 3. NIR (Nova IR)

`src/Nova.Compiler/Nir/`. A control-flow graph of basic blocks per function, modelled on Rust MIR.
It is not SSA. M1 subset:

```text
function  = locals: [local]          -- _0.._(n-1) are the parameters, in order
            blocks: [block]          -- bb0 is the entry block
local     = id, type, name?          -- name is null for compiler temporaries
block     = id, statements: [stmt], terminator
place     = local projection*                  -- projection = '.' field | Deref (M2a, M2b) | Downcast(V) | Index(operand) (M2d)
stmt      = Assign(place, rvalue)
          | Eval(rvalue)             -- for calls whose result is unused (void)
          | StorageDead(local)       -- scope end; temporaries at the end of their statement (M2b)
          | Drop(place, flag?)       -- inserted by drop elaboration (M2c); guarded by a bool flag local if present
          | Unpack(place, V, [local?])          -- M3c: move every payload of variant V out of a consumed enum local
          | BoxFree(place, flag?)               -- M3c: free a partially moved box without destroying its contents
          | BoundsCheck(index, length)          -- panics unless index < length (M2d)
          | RangeCheck(low, high, length)       -- panics unless low <= high <= length (M2d);
                                                 -- with a `Text` place (M6c), also unless both bounds
                                                 -- are UTF-8 character boundaries of that StringView
rvalue    = Use(operand)
          | Binary(op, operand, operand, span)     -- op semantics: type-system.md §6 (may panic)
          | Unary(op, operand, span)
          | Cast(operand, type)                    -- lossless `as`
          | Truncate(operand, type)
          | Aggregate(struct, [operand])           -- field values in declaration order (M2a)
          | Ref(Shared | Mut, place)               -- creates a loan (M2b; lifetimes.md)
          | Discriminant(place) | EnumAggregate(V, [operand])   -- M2d enums
          | Array([operand]) | ArrayRepeat(operand)             -- M2d array values
          | Slice(Shared | Mut, place, low, high)  -- a slice of an array or deref'd slice; a loan of the whole place
          | Length(place)                          -- a slice's length
          | Call(function, [operand])
          | Builtin(kind, [operand])               -- Console.write/writeLine
operand   = Place(place, move?) | Const(value, type) | String(text)   -- move: non-Copy type or explicit `move` (M2c)
terminator= Goto(bb) | Branch(operand, then_bb, else_bb) | Return(operand?)
          | Switch(operand, [(value, bb)], otherwise_bb)    -- match (M2d)
          | Exit(operand)                          -- Process.exit
          | Unreachable                            -- after a call of type never
          | Panic(kind)                            -- `e!` on Err (M3a), `o!` on null (M6c); aborts
```

Lowering rules (HIR → NIR, `NirBuilder`):

- **Evaluation order is explicit.** Operands are evaluated left to right into operands or
  temporaries. If the right operand of a binary operator (or a later argument) contains a call,
  an earlier operand that reads a local is first copied into a temporary. This keeps
  left-to-right semantics when calls can mutate state (M2+).
- `&&` and `||` become branches into a boolean temporary. The right operand is evaluated
  only when needed.
- `x op= e` becomes `x = Binary(op, x, e)`; the same applies with a place such as `p.a.b`.
- A struct literal evaluates its initialisers **in source order** into operands, then builds an
  `Aggregate` with them in declaration order.
- A field read of a place is a place operand (`_3.a.b`). A field read of any other value (a call
  result) first spills the value into a temporary. A method call passes the receiver as the first
  argument, evaluated before the explicit arguments.
- `while` becomes `cond → body → cond`, and `loop` becomes `body → body`. `break` and `continue`
  jump to the innermost loop's exit or head.
- Statements after a `return`, `break`, `continue`, `Process.exit` or never-typed call are
  unreachable and are not lowered. Blocks with no predecessors are not emitted.
- Constants are already folded in HIR. NIR performs no optimisation in M1.

- References (M2b):
  - an explicit or implicit borrow becomes `t = Ref(k, place)`;
  - auto-deref reads `(*r)`;
  - a use of a `borrow mut` variable becomes `Ref(Mut, (*r))` (reborrow);
  - a call-site borrow of a value spills it into a temporary, which gets `StorageDead` at the end of
    the statement;
  - for a `borrow mut this` receiver, the receiver's `Ref` is emitted after the other arguments (B5);
  - every statement carries a source span for borrow diagnostics.
- The **borrow checker** (`Analysis/BorrowChecker.cs`, `lifetimes.md`) runs on NIR inside
  `Compilation.Create`, so `novac check` reports borrow errors too.
- **Moves and destruction (M2c).**
  - Scopes hold locals *and* deferred statements. Leaving a scope (its end, `break`/`continue`,
    or `return`) emits, in reverse order of position, `StorageDead` for locals and a fresh
    lowering of each deferred statement.
  - By-value parameters form the outermost scope.
  - `return e` computes `e` into an unscoped temporary first, then ends the statement's
    temporaries and every scope, then returns.
  - **Move paths** (`Analysis/MovePaths.cs`): each local is a tree, expanded field by field
    wherever a field is assigned or moved; all siblings are expanded, so children partition their
    parent. A forward dataflow over the leaves tracks maybe-init, maybe-uninit and maybe-moved.
    `MoveChecker` uses it for N0212/N0250–N0253.
  - **Drop elaboration** (`Analysis/DropElaborator.cs`) runs only when every check passed:
    - it inserts `Drop` before `StorageDead` of a local that needs drop, and before an assignment
      that overwrites one; the assignment becomes `t = rvalue; Drop(p); p = t`;
    - what is dropped depends on the state: whole, nothing, a flagged drop, or per field for a
      partially moved struct;
    - one bool flag per leaf is updated after every statement that changes that leaf's state.

    The C backend emits drop glue `nvD_<len><Name>(T *)`: the `drop` block, then each field that
    needs drop, in declaration order.

- **Arrays, slices and `for` (M2d).**
  - A slice type is a reference to the unsized `[T]`; `s[i]` is the place `(*s)[i]`.
  - An index is evaluated into a `usize` temporary (or kept as a constant), then checked with
    `BoundsCheck` against the array's constant length or the slice's `Length`, then used as an
    `Index` projection. Element places are never move paths: an element's init state is its
    array's.
  - An assignment evaluates its target's indexes (and checks them) before the value.
  - `a[lo..hi]` evaluates the base place, then the bounds left to right, then `RangeCheck` (none
    for `a[..]`), then `Slice`.
  - `for` evaluates its bounds, or its sequence place and length, once into locals that live until
    the loop exits. A `usize` counter is compared with the end at the head. The body binds the
    variable in its own scope; `continue` jumps to the counter's increment, and `break` to the
    exit, which ends the header's locals.

- **Results (M3a).**
  - `e?` and `e!` move `e` into a temporary, switch on its tag, and in the `Ok` block read
    the payload `t as Ok .0`.
  - On `Err`, `?` builds `Err(t as Err .0)` of the function's return type and returns through the
    same path as `return`; `!` ends in `Panic`.
  - **Optionals (M6c)** lower the same way with `Some` (tag 1) as the success variant: on `null`,
    `?` returns the `null` of the function's return type and `!` ends in `Panic(UnwrapNull)`.
    `o == null` (HIR `IsNull`) takes a shared borrow of `o` (or of a temporary), reads
    `Discriminant(*r)` and compares it with 0, so `o` is not consumed.
  - **Vectors and strings (M6d).** A vector dereferences, like a box, to its unsized elements: `v[i]`
    is the place `v.*[i]`, with a `BoundsCheck` against `Length(v)`, and views are `Ref` of `v.*`.
    The deref is not a move path, so moving an element out is a move out of a deref (N0251).
    `push`/`append` lower their argument before the receiver's `Ref mut` (B5). `for (x in v)`
    iterates over a view held in a header temporary, so the vector stays borrowed for the whole
    loop. When the value written to a vector element contains a call, it is evaluated into a
    temporary and the element's index is checked again just before the write (the call may have
    shrunk the vector).
  - **Slot maps and stable vectors (M6e)** are built-in calls on a borrow of the container
    (`insert`, `getMut` and `remove` take `Ref mut`, with `insert` two-phase); `get`'s optional
    reference carries the map's loan like any built-in result. A stable vector dereferences like
    a vector, so `sv[i]` is the place `sv.*[i]`; the C backend renders its elements through the
    chunk table. Liveness treats the targets of `Unpack` as definitions (fixed in M6e: a reference
    payload bound by a consuming match used to be live from the function's start).
  - **Region parameters (M6x).** A loan-carrying value stored into a field or element is lowered
    into a temporary first, so the borrow checker sees a local whose loans flow into the target.
  - **String views (M6c).** A literal is a constant operand of type `StringView` (no loans); a place
    is needed only when it is spilled into a temporary. `s[lo..hi]` always emits a `RangeCheck`
    with `Text`; `s.bytes()` is a `Cast` to `Slice<u8>` that keeps the view's loans.
  - Moving a variant payload out of a local (only these lowerings do that) uninitialises the whole
    local in the move dataflow, so the consumed result is not destroyed again.
  - **Open temporaries.** The builder records the temporaries of every statement still being
    lowered, with the scope they belong to. An early exit (`return`, `break`, `continue`, `?`)
    ends them together with that scope, before its locals. Before M3a the temporaries of an
    enclosing statement, such as a `match` scrutinee, were never ended on such paths.
  - An expression statement whose value needs drop assigns it to a statement temporary, which is
    destroyed at the end of the statement (type-system.md §17.5).

- **Boxes and consuming matches (M3c).**
  - Move paths go through box derefs (`NirPlace.IsMovePath`). Once a box's contents are tracked,
    the box node has two children: its contents (fields below), then an *allocation* leaf that is
    initialised while the box owns its memory.
  - Drop elaboration destroys a partially moved box as: the initialised fields of its contents,
    then `BoxFree` of the box. `BoxFree` is flagged if the allocation is only maybe initialised.
  - A consuming `match` moves its scrutinee into a temporary. An arm that lists payloads starts
    with one `Unpack`, which moves every payload into its binding or into a hidden local of the
    arm and consumes the temporary as a whole. Other arms leave the value in the temporary, which
    is destroyed at the end of the statement.

- **Generic code (M6a).** NIR is built for generic versions too (type parameters are move types
  that may need drop), so the move and borrow checkers run on them. Instantiations skip those
  checks, which would only repeat the generic version's diagnostics. Drop elaboration and C
  emission skip generic versions.

Printed by `novac build --dump-nir`.

## 4. LIR — deferred

`architecture.md` §2.4 plans a C-shaped LIR between NIR and C. In M1 the C emitter consumes NIR
directly. The C-level decisions (type names, helper selection, temporaries) are made in one place,
`Backend.C/CEmitter.cs`, because NIR operands are already flat. LIR will be introduced when a
second consumer appears or when drop elaboration makes the emitter non-trivial (M2/M7).
This is a recorded deviation from the plan, not an omission.

## 5. The NIR reference interpreter (M7c)

`novac run --interp <files> [-- <arguments>]` executes a program's NIR directly
(`src/Nova.Compiler/Interpreter/`). It serves as an oracle for the C backend. The C backend and
the interpreter consume the same checked NIR, after drop elaboration. Agreement between them
therefore tests the C backend and its runtime, not NIR lowering.

### 5.1 Observable behaviour

The interpreter must produce exactly what the native program produces:

- the bytes written to standard output and standard error, in the same order: every write to
  standard error first flushes standard output (`nova_rt_hosted.c`);
- the exit status:
  - `main`'s result, or `Process.exit`'s code (the operating system keeps the low 8 bits);
  - 134 after a panic, as `abort()` gives natively;
- the panic message (`nova panic: <reason> at <file>:<line>:<column>`);
- the allocation counters: `NOVA_FAIL_ALLOC_AT` and the `NOVA_ALLOC_REPORT` line. The interpreter
  counts every attempt that `nova_alloc` would. This includes vector growth, stable-vector chunks,
  slot-map slots, arena and scratch chunks, pool slots, and `Process.run`'s argument copies. The
  allocation-failure sweep therefore sees the same attempts.

To get these, the runtime's algorithms are reimplemented step for step:
- `nova_rt_core.c`: growth (doubling, at least 4), arena chunks with their 16-byte header,
  scratch chunks of 64 KiB, the pool slot size, and the panic and report texts;
- the emitter's helpers (`c-backend.md` §2): push, pop and clear, slot-map generations, and the
  drop-glue order.

### 5.2 Memory model

Memory is **byte-addressed**. Values have the target layout of `TypeTable.LayoutOf`, which is the
same layout the generated C asserts. Pointers are 8-byte addresses, and null is 0. Memory is a set of
*allocations*, each with a base address aligned to 16 (as `malloc` gives) and a size:

| Kind | Created by | Ends at |
|---|---|---|
| local | a function's entry (every local, zero-filled, as the C locals are) | `StorageDead`, or the function's return |
| heap | `nova_alloc` | `nova_free` |
| static | a string literal (read-only), the empty-vector sentinel (size 0) | never |

`StorageDead` ends a local's allocation. The next write to that local (a whole assignment, or a
write or borrow through one of its fields) gives it a *new* allocation, so an old reference to it
stays dead.

**Interpreter faults.** Each of the following stops the program with
`nova interp: fault: <what> at <file>:<line>:<column>` on standard error and exit status 70:
- an access to an ended allocation, or outside an allocation's bounds;
- a dereference of null;
- a write to static memory;
- `nova_free` of anything but the base of a live heap allocation;
- a *use* of uninitialised bytes. Heap memory starts uninitialised. Each byte carries an
  initialised bit, which copies carry along: copying a struct with padding is not a use.
  Reading a scalar to compute with it, to branch on it, to compare it, as a tag or as an address
  is a use.

No correct program can fault. A fault means the compiler, its runtime, or its checks let an
unsafe program through. This makes the interpreter a dynamic safety oracle: liveness, bounds and
initialisation. It does not track borrows (no Stacked Borrows), so it does not catch aliasing
violations that stay in bounds.

### 5.3 Execution

- Calls recurse on the host stack. The interpreter runs on its own thread with a 1 GiB stack.
- Drop glue and the runtime helpers are interpreted in the same order as the emitted C. A user
  `drop` function, for example, runs before its fields' drops.
- Arithmetic follows `IntegerSemantics` (type-system.md §6–7), the same table the checked C helpers
  implement.

### 5.4 The host

- **Output.** Standard output, standard error and standard input are streams supplied by the
  caller. The CLI uses the process's own.
- **Arguments.** `argv[0]` is the first source file's path. The arguments after `--` follow it.
- **Configuration.** The CLI reads `NOVA_FAIL_ALLOC_AT` and `NOVA_ALLOC_REPORT` as
  `nova_platform_config` does (leading digits).
- **Files and sockets** use the host C library through P/Invoke (`open`, `read`, `write`,
  `close`, `unlink`, `socket`, …) on Linux and macOS, with the same flags and errno mapping as
  `nova_rt_io.c`. On Windows, I/O is an interpreter fault ("unsupported").
- **Working directory.** An optional working directory resolves relative file paths and is
  where child processes start. Tests use it, since they run in parallel in one process. The CLI
  uses the process's own directory.
- **`Process.run`** starts the program with .NET's process API, after flushing standard output.
  When the interpreter's output streams are the process's own, the child inherits them. When the
  caller captures output (tests), the child's output is copied into the captured streams.

### 5.5 Agreement tests

- `InterpreterTests` run every run-pass and run-fail program. Standard output, standard error
  and exit status must equal the corpus expectations. The native runs check the same
  expectations under every C configuration.
- The allocation-failure sweep runs the interpreter beside the native ASan build, for the
  baseline and at every failure point. The outcomes must be equal.
