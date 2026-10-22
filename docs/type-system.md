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
| `/ %` | integer | same | zero divisor → panic. `MIN / -1` → panic (overflow). `/` truncates toward zero, `%` has the sign of the dividend, and `MIN % -1` is `0` |
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

## 12. Structs (M2a)

```nova
struct Point {
    x: i64;
    y: i64;

    fn sum(this) -> i64 => this.x + this.y;                    // method: `this` by value
    fn origin() -> Point => Point { x: 0, y: 0 };             // static function
}
```

- **Declaration.** Top level only. Field names and member names (fields and functions) are unique
  within the struct (N0228). Field types are any non-`void` M1 type or another struct. A struct
  must not contain itself by value, directly or through other structs (N0227: infinite size).
  Recursive data needs indirection (`Box`, M3). Empty structs are allowed.
- **Names.** A struct name is a package item (unique with functions and constants, N0201) and a
  type name. In expressions, `S.f(...)` calls the static function `f` of `S`.
- **Copy.** In M2a every struct is a Copy type: its fields are Copy and `drop` does not exist yet.
  Assignment, argument passing and return copy the value. OD-7's rule ("Copy iff all fields Copy
  and no `drop`") takes effect in 2c.
- **Literals.** `S { f1: e1, …, fn: en }` must initialise **every** field exactly once (missing:
  N0229, unknown: N0230, duplicate: N0231), in any order. Each `ei` is checked against the field's
  type. Initialisers are **evaluated in source order** (`intermediate-representation.md`), not in
  declaration order.
- **Field access.** `e.f` where `e` has struct type `S` and `f` is a field of `S` has `f`'s type
  (unknown field: N0230; `e` is not a struct: N0234). An access chain `a.b.c` is a *place* if `a`
  is a local or `this`.
- **Field assignment.** `a.f = e` and `a.f op= e` require the root local `a` to be a `var` (N0211).
  `this` is immutable in a by-value method. The root must be definitely assigned *as a whole*
  first (N0212): M2a has no partial initialisation.
- **Methods.** A function in a struct whose first parameter is `this` is a method. `this` has the
  struct's type and is an immutable binding (a copy for Copy structs). It is called as `e.m(args)`,
  where `e` is evaluated first and passed as `this`. A static function (no `this`) is called as
  `S.f(args)`. Calling a method as `S.m()`, or a static function as `e.f()`, is N0233. `this`
  anywhere other than a method's receiver position or body is N0232.
- **Operators.** No operator applies to struct values in M2a (`==` included: N0207).
  `Console.write` cannot print structs (N0207).
- **Layout.** Fields are laid out in declaration order with C's rules (`@repr(C)` by default,
  `memory-model.md` §2). The compiler computes size, alignment and field offsets from `TargetInfo`.
  The generated C checks them with `_Static_assert` (`c-backend.md` §2), so a disagreement with the
  real C compiler fails the build instead of miscompiling.

## 13. References (M2b)

The algorithm that enforces these rules is in `lifetimes.md`.

### 13.1 Types
- `borrow T` (shared) and `borrow mut T` (exclusive), where `T` is any non-reference, non-`void`
  type. `borrow borrow T` is rejected (N0246). At runtime a reference is a non-null pointer.
- **Where references may appear (v0):**
  - parameter types;
  - `let` locals with an initialiser;
  - return types (with a source list, §13.5).

  They may **not** appear in struct fields (E3), constants, or `var` locals (N0246). Struct
  fields of reference type need lifetime parameters (OD-3).
- `borrow T` is Copy. A `borrow mut T` value is never copied: every use reborrows it (§13.3).

### 13.2 Creating references
- `borrow p` / `borrow mut p`, where `p` is a **place**: a local, `this`, a field path, or a path
  through a reference such as `r.f` (`(*r).f`). Borrowing anything else is N0247; bind the value
  to a local first.
- `borrow mut p` requires `p` to be mutable: rooted at a `var` local, or reached through a
  `borrow mut` reference (N0248). A `let` binding, a parameter, a by-value `this`, and anything
  reached through a shared reference are immutable.
- **Call sites.**
  - A `borrow T` parameter accepts a place or value of type `T`, borrowed implicitly. A value
    becomes a temporary that lives until the end of the statement.
  - A `borrow T` parameter also accepts `borrow T` or `borrow mut T`; the latter is coerced by a
    shared reborrow.
  - A `borrow mut T` parameter accepts `mut p`, where `p` is a mutable place of type `T`, or a
    `borrow mut T` value (reborrowed).

  The `mut` marker is required for a plain place and is rejected where the parameter is not
  `borrow mut` (N0249). It makes mutation visible at the call site, as C#'s `ref` does.
- **Receivers.** `fn m(borrow this)` and `fn m(borrow mut this)` declare reference receivers;
  `this` then has type `borrow S` / `borrow mut S`. In `e.m(…)` the receiver is borrowed
  implicitly, with no marker. For `borrow mut this`, `e` must be a mutable place or a
  `borrow mut S` value (N0248).

### 13.3 Using references
- **Auto-deref.** Where a value of type `T` is needed (an operand, a by-value argument, an
  initialiser of type `T`, a return of type `T`, `Console.write`), a `borrow T` or
  `borrow mut T` operand is read through: `*r`. Field access `r.f` and method calls
  `r.m()` also look through references.
- **Writes through references.** `r = v`, `r op= v` and `r.f = v` write to the referent. This
  requires `borrow mut` (N0211: "cannot assign through a shared reference"). A reference local
  itself can never be re-pointed.
- **Reborrow of `borrow mut`.** Using a `borrow mut T` value as a call argument, an initialiser
  or a return value creates a new mutable loan of `*r`. The original reference is unusable while
  the new one is live, and usable again afterwards (B4).
- References support no operators of their own: `==` compares the referents (via auto-deref).

### 13.4 Borrow rules (summary of `lifetimes.md`)
While a loan is live (the reference, or something derived from it, is used later), the borrowed
place may not be:
- written, mutably borrowed, or go out of scope, if the loan is shared;
- accessed at all, if the loan is `borrow mut`.

Diagnostics: N0240 (write), N0241 (conflicting borrow), N0242 (read while mutably borrowed),
N0243 (out of scope or temporary dropped while borrowed).

### 13.5 Returning references
- A function returning `borrow T` / `borrow mut T` with exactly one reference parameter (counting
  a reference receiver) returns something derived from that parameter.
- Otherwise the return type names the sources: `fn longer(a: borrow T, b: borrow T) ->
  borrow(a, b) T`. Missing, unknown or non-reference sources are N0245, and so is a
  `borrow mut` result naming a shared parameter.
- The body may only return references derived from the named parameters (N0245). A reference to
  a local or temporary of the function is N0244.

## 14. Move types, destruction, `using` and `defer` (M2c)

Normative for 2c. It implements `ownership.md` O1–O5 and R3. The NIR analyses are described in
`intermediate-representation.md`.

### 14.1 Copy and move types (OD-7)
- **Copy:** integers, `bool`, and `borrow T`. A struct is Copy iff it has no `drop` block and every
  field is Copy.
- **Move types:** structs with a `drop` block or a move-type field, and `borrow mut T` (whose uses
  are reborrows, §13.3, so in practice it is never moved out of a variable).
- **Needs drop:** a struct with a `drop` block, or one with a field that needs drop. Every type that
  needs drop is a move type. The converse does not hold.

### 14.2 `drop` blocks
```nova
struct File {
    fd: i32;
    drop { Sys.close(this.fd); }      // `this` is `borrow mut File`
}
```
- At most one `drop` block per struct (N0228). Inside it, `this` has type `borrow mut S`. The block
  cannot be called explicitly. It runs exactly once when a value of `S` is destroyed (O4), and the
  fields that need drop are destroyed **after** it, in declaration order.
- Moving a field out of a value whose type has a `drop` block is N0252: the `drop` block needs the
  whole value. Moving out of `this` inside `drop` is impossible anyway (§14.3).

### 14.3 Moves
- **Using a move-type place by value moves it**:
  - an initialiser or right-hand side;
  - a by-value argument or a by-value `this` receiver;
  - a struct-literal field;
  - a `return` value.

  The place is then uninitialised. Reading, borrowing or moving it again before it is reassigned is
  **N0250** ("use of moved value"); this is O2 and O3.
- **`move p`** explicitly moves out of the place `p`, even for a Copy type. Afterwards `p` is
  uninitialised, which ends a value's use early.
- **Partial moves** (O2): moving `p.f` out of a local is allowed if no struct on the path from
  `p` to `f` has a `drop` block. `p` is then partially initialised. `p.f` may be reassigned, after
  which `p` is whole again. Reading `p` as a whole, or borrowing it, needs all of it initialised
  (N0250).
- **Not allowed:**
  - moving out of a place reached through a reference (N0251), because the referent must stay
    valid; this includes `this` in `borrow`/`borrow mut` methods;
  - moving out of a `using` binding (N0253).
- **Moves and borrows:** moving a place while it is borrowed is a conflict, treated like a write
  (N0240).

### 14.4 Initialisation (replaces §9's first bullet)
The analysis runs on NIR move paths (`intermediate-representation.md`):
- A place must be **definitely initialised** to be read, borrowed or moved. If it may never have
  been assigned, that is N0212. If it may have been moved, that is N0250.
- Assigning a field `p.f` requires the rest of `p` to be initialised (no piecewise construction
  of an uninitialised struct). Re-initialising a moved-out field is allowed.

### 14.5 Destruction (O4)
- A value that needs drop is destroyed exactly once, at the first of these:
  - the end of its variable's scope: the block end, a `break`/`continue` leaving the block, or a
    `return`;
  - the end of its statement, for a temporary;
  - being **overwritten** by an assignment, in which case the old value is destroyed first.
- **Order:** locals in reverse declaration order; then a struct's `drop` block; then its fields in
  declaration order. By-value parameters are destroyed when the function returns, after the locals.
- **Moved values are not destroyed.** A value that has been moved on *some* paths gets a runtime
  **drop flag**. A partially moved struct destroys only the fields it still has.
- **`Process.exit` and panics do not run destructors or deferred statements** (panic = abort,
  OD-5).

### 14.6 `using`
`using x = e;` declares an immutable local that cannot be moved out of (N0253) and is destroyed at
the end of its block, like any local (R3). It documents that the value's lifetime *is* the scope:
a lock guard, an arena, a file.

### 14.7 `defer`
`defer s;` schedules the statement `s` to run when the enclosing block exits on any path:
- the block end;
- `break`/`continue` out of it;
- `return` (after the return value is computed).

Deferred statements and local destructions run in reverse order of their position, so a `defer`
runs before the locals declared before it are destroyed, and after the locals declared after it.
`s` may not contain `return`, `break`, `continue` or another `defer` (N0254).

## 15. Enums and `match` (M2d)

```nova
enum Shape {
    Empty,
    Circle(i64),
    Rect(i64, i64),
}

fn area(s: Shape) -> i64 {
    match (s) {
        Shape.Empty => { return 0; }
        Shape.Circle(r) => { return 3 * r * r; }
        Shape.Rect(w, h) => { return w * h; }
    }
}
```

### 15.1 Declarations and values
- `enum E { V1, V2(T1, …), … }` at the top level. Variants are unique (N0228) and have zero or
  more positional payload types: any non-reference, non-`void` type (E3 applies to payloads too).
  An enum must not contain itself by value (N0227).
- **Values.** `E.V` for a variant without payload; `E.V(e1, …)` with exactly the payload arity
  (N0209). Each `ei` is checked against its payload type.
- **Copy and drop.** An enum is Copy iff every payload type is Copy. It needs drop iff some payload
  needs drop; destroying it destroys the payloads of the active variant. Enums have no `drop`
  block (v0).
- **Layout.**
  - A tag (`u8` for ≤ 256 variants, else `u16`/`u32`), then a union of the variants' payloads, each
    laid out like a struct.
  - Size and alignment follow C's rules for `struct { tag; union { … }; }`. The generated C
    asserts them.
  - Nova does not reorder or niche-optimise (v0).

### 15.2 `match`
```text
MatchStmt = 'match' '(' Expr ')' '{' Arm* '}'
Arm       = Pattern '=>' Block ','?
Pattern   = '_' | IntLiteral | '-' IntLiteral | 'true' | 'false'
          | QualifiedName ('(' Binding (',' Binding)* ')')?        -- E.V or E.V(x, _)
Binding   = Identifier | '_'
```
- `match` is a **statement** in v0. Arms are tried in order; the first that matches runs its block.
- **Scrutinee types:**
  - an enum: variant patterns;
  - an integer: literal patterns of that type;
  - `bool`: `true`/`false`;
  - or a reference to one of these.
- **Exhaustiveness** (N0256): enum matches must cover every variant or end with `_`. Integer
  matches need `_`. `bool` matches need both values or `_`. An arm that can never match (a
  repeated variant or literal, or anything after `_`) is N0257.
- **Bindings.** A variant pattern binds each named payload to a new immutable local in the arm:
  - **Scrutinee of type `E` (a value):** the binding is a *copy* of the payload. A non-Copy
    payload cannot be bound this way (N0258: "match on `borrow e` to bind it by reference").
    Moving payloads out of an enum is future work (it needs move paths into variants).
  - **Scrutinee of type `borrow E` / `borrow mut E`:** the binding is a `borrow T` /
    `borrow mut T` to the payload, and the borrow checker treats it like any reborrow of the
    scrutinee.
  - `_` binds nothing.
- The scrutinee is evaluated once. When it is a place it is not moved (only Copy payloads are
  read). A temporary scrutinee lives until the end of the `match` statement.
