# Type System and Static Semantics — M1 subset

Status: normative for Milestone 1 (increment 1b implements it). Later milestones extend these
rules. Where `language-spec.md` describes a later feature, it does not apply until then. Error
codes are listed in `diagnostics.md`.

## 1. Types

| Type | Values | Notes |
|---|---|---|
| `i8 i16 i32 i64` | two's complement, −2^(n−1) … 2^(n−1)−1 | |
| `u8 u16 u32 u64` | 0 … 2^n−1 | |
| `isize` / `usize` | pointer width of the **target** (64 on every M1 target) | from `TargetInfo`, never the host |
| `bool` | `true`, `false` | |
| `void` | no value | function return type only |
| *never* | no value; the expression does not complete | internal only (`Process.exit`); not nameable |

Not in M1 (error N0216): floats, `char`, strings as values, user-defined types, references.
A string literal is allowed **only** as a direct argument of `Console.write`/`writeLine`.

`void` may appear only as a return type (writing `-> void` is the same as omitting the
return type). A *never* expression is accepted wherever any type is expected.

## 2. Names and scopes

- **Package scope.** M1 compiles one package: all files of a compilation share one item
  scope. Items are referenced by simple name. `namespace` declarations are recorded but do
  not qualify names yet, and visibility modifiers have no effect within one package. Two items with the
  same name are an error (N0201), even in different files or namespaces.
- **Built-in scope** (outside the package scope, so a user item hides a built-in):
  `Console.write`, `Console.writeLine`, `Process.exit`, `truncate`, and the primitive
  type names.
- **Function scope.** Parameters, then nested block scopes. A local is visible from the end of
  its declaration to the end of its block.
- **No shadowing of locals.** Declaring a local or parameter whose name is already a local or
  parameter in scope is an error (N0203), as in C#. Shadowing an *item* with a local is allowed.
- Lookup order for a simple name: locals (innermost first), package items, built-ins.

## 3. Bindings and mutability

- `let x = e;` declares an immutable local and **requires** an initialiser (N0220).
- `var x = e;` / `var x: T = e;` / `var x: T;` declare a mutable local. Without an initialiser
  the type is required (N0221), and the variable must be definitely assigned before every read (§7).
- Without an annotation, a local's type is the type of its initialiser. An initialiser of type
  `void` is an error (N0224).
- Parameters and `const` items are immutable. Assigning to them, or to a `let`, is N0211.
- Assignment targets in M1 are locals only. Member targets (`a.b = …`) are N0216.

## 4. Integer literals

- A suffixed literal (`7u8`) has the suffix's type.
- An unsuffixed literal takes the **expected type** from context (§5) if that type is an
  integer type. With no integer expectation it defaults to `i32`.
- The value must be representable in the literal's type (N0206). A literal directly under
  unary minus is checked as a negative value, so `-128i8` and `-9223372036854775808` (as `i64`) are valid.

## 5. Expected types (bidirectional checking)

Each expression is checked against an optional expected type:

- local initialiser → the annotated type; assignment value → the target's type;
  argument → the parameter type; `return` value → the return type; `const` value → its
  declared type; `if`/`while` condition → `bool`.
- An expression's *actual* type must equal the expected type, unless it is *never* (N0205).
- **Flexible operands.** An expression is *flexible* if it consists only of unsuffixed integer
  literals combined with unary `-`, parentheses and the arithmetic/bitwise binary operators.
  For a binary operator whose operands must have the same type:
  1. if the operator's result type is the operand type and an integer type is expected, both
     operands are checked against it;
  2. otherwise, if exactly one operand is flexible, the other operand is checked first
     (without expectation) and its type becomes the flexible operand's expected type;
  3. otherwise both are checked without expectation (flexible ones default to `i32`).

  So `x + 1` with `x: u8` types `1` as `u8`, and `let y: u64 = 2 * 3;` types both literals as `u64`.

## 6. Operators

All binary operators require both operands to have the **same type**. There are no implicit
conversions.

| Operators | Operand types | Result | Runtime behaviour (implemented in 1c) |
|---|---|---|---|
| `+ - *` | integer | same | overflow → panic |
| `/ %` | integer | same | zero divisor, or `MIN / -1`, → panic. `%` has the sign of the dividend |
| `& \| ^` | **unsigned** integer | same | |
| `<< >>` | left: **unsigned** integer; right: any integer type | left type | amount `< 0` or `≥ bit width` → panic. `>>` is logical |
| `== !=` | integer or `bool` | `bool` | |
| `< <= > >=` | integer | `bool` | |
| `&& \|\|` | `bool` | `bool` | short-circuit |
| unary `-` | **signed** integer | same | `-MIN` → panic |
| unary `!` | `bool` | `bool` | |

Bitwise operators and shifts are restricted to unsigned types in M1. This avoids the
implementation-defined and undefined parts of C's signed bit operations. Reinterpret
with `truncate<u32>(x)`. Relaxing this later is backward compatible. A shift amount that is a
flexible literal is typed as `u32`. Other mismatches are N0207.

## 7. Conversions

- `e as T` is allowed only when **every** value of the source type is representable in `T`
  (N0208 otherwise):
  - identity;
  - `uN → uM` and `iN → iM` for M ≥ N;
  - `uN → iM` for M > N;
  - `bool →` any integer type (`false`→0, `true`→1).
  `isize`/`usize` use the target width.
- `truncate<T>(e)`: `e` of any integer type, `T` any integer type. The result is `e`
  modulo 2^bits(T), reinterpreted in `T` (two's complement). It is always defined. Use it for
  narrowing and for signed↔unsigned reinterpretation.

## 8. Calls and statements

- A callee must be a package function, or one of the built-ins below (N0210 otherwise).
  Functions are not values in M1. The argument count must match (N0209), and each argument is
  checked against its parameter type.
- Built-ins:
  - `Console.write(x)` and `Console.writeLine(x)`: `x` is an integer, a `bool`, or a string
    literal. `writeLine()` with no argument writes a newline. Both return `void`.
  - `Process.exit(code: i32)`: has type *never*.
  - `truncate<T>(e)`: see §7. Exactly one type argument (N0216 for other generic arguments).
- An expression statement must be a call (N0225). `1 + 2;` is rejected.
- `break`/`continue` must be inside `while` or `loop` (N0214).
- `return;` only in a `void` function. `return e;` only in a non-`void` function (N0215).

## 9. Control flow facts

- **Definite assignment.** A local may be read only if it is assigned on every path from its
  declaration to the read (N0212). Compound assignment (`x += 1`) reads `x`. Code that is
  unreachable is not checked. M1 has no moves, so the analysis is a forward must-analysis
  over the structured statements; it moves to NIR in M2.
- **Termination.** A statement *may complete* unless it is a `return`, `break` or
  `continue`, or a call of type *never*. It also may not complete if it is a block containing
  such a statement, an `if` with an `else` whose two branches both cannot complete, or a `loop`
  with no `break` that targets it. A `while` loop may always complete, whatever its condition:
  write `loop` for infinite loops.
- **Missing return.** If the end of a non-`void` function body may be reached, that is N0213.

## 10. Constants

- `const N: T = e;` where `e` is a constant expression: literals, other constants, and unary,
  binary, `as` and `truncate` applied to constant expressions. Calls are not constant (N0217).
- Constants are evaluated at compile time with the runtime semantics of §6. An operation that
  would panic at runtime (overflow, division by zero, bad shift) is a compile error (N0217).
- A constant that depends on itself is N0218.
- Uses of a constant are replaced by its value, typed as the constant's declared type.

## 11. `main`

If a function named `main` exists, it must take no parameters and return `i32` or
`void` (N0219). Whether `main` is *required* is decided by `novac build` (increment 1c).
