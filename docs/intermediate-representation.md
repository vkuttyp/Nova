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
place     = local projection*                  -- projection = '.' field | Deref (M2a, M2b)
stmt      = Assign(place, rvalue)
          | Eval(rvalue)             -- for calls whose result is unused (void)
          | StorageDead(local)       -- scope end; temporaries at the end of their statement (M2b)
          | Drop(place, flag?)       -- inserted by drop elaboration (M2c); guarded by a bool flag local if present
rvalue    = Use(operand)
          | Binary(op, operand, operand, span)     -- op semantics: type-system.md §6 (may panic)
          | Unary(op, operand, span)
          | Cast(operand, type)                    -- lossless `as`
          | Truncate(operand, type)
          | Aggregate(struct, [operand])           -- field values in declaration order (M2a)
          | Ref(Shared | Mut, place)               -- creates a loan (M2b; lifetimes.md)
          | Call(function, [operand])
          | Builtin(kind, [operand])               -- Console.write/writeLine
operand   = Place(place, move?) | Const(value, type) | String(text)   -- move: non-Copy type or explicit `move` (M2c)
terminator= Goto(bb) | Branch(operand, then_bb, else_bb) | Return(operand?)
          | Exit(operand)                          -- Process.exit
          | Unreachable                            -- after a call of type never
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

Printed by `novac build --dump-nir`.

## 4. LIR — deferred

`architecture.md` §2.4 plans a C-shaped LIR between NIR and C. In M1 the C emitter consumes NIR
directly. The C-level decisions (type names, helper selection, temporaries) are made in one place,
`Backend.C/CEmitter.cs`, because NIR operands are already flat. LIR will be introduced when a
second consumer appears or when drop elaboration makes the emitter non-trivial (M2/M7).
This is a recorded deviation from the plan, not an omission.
