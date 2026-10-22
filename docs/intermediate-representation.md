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
stmt      = Assign(local, rvalue)
          | Eval(rvalue)             -- for calls whose result is unused (void)
rvalue    = Use(operand)
          | Binary(op, operand, operand, span)     -- op semantics: type-system.md §6 (may panic)
          | Unary(op, operand, span)
          | Cast(operand, type)                    -- lossless `as`
          | Truncate(operand, type)
          | Call(function, [operand])
          | Builtin(kind, [operand])               -- Console.write/writeLine
operand   = Local(id) | Const(value, type) | String(text)
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
- `x op= e` becomes `x = Binary(op, x, e)`.
- `while` becomes `cond → body → cond`, and `loop` becomes `body → body`. `break` and `continue`
  jump to the innermost loop's exit or head.
- Statements after a `return`, `break`, `continue`, `Process.exit` or never-typed call are
  unreachable and are not lowered. Blocks with no predecessors are not emitted.
- Constants are already folded in HIR. NIR performs no optimisation in M1.

Printed by `novac build --dump-nir`.

## 4. LIR — deferred

`architecture.md` §2.4 plans a C-shaped LIR between NIR and C. In M1 the C emitter consumes NIR
directly. The C-level decisions (type names, helper selection, temporaries) are made in one place,
`Backend.C/CEmitter.cs`, because NIR operands are already flat. LIR will be introduced when a
second consumer appears or when drop elaboration makes the emitter non-trivial (M2/M7).
This is a recorded deviation from the plan, not an omission.
