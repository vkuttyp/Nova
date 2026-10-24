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
In M1 a string literal was allowed **only** as a direct argument of `Console.write`/`writeLine`;
since M6c it is a `StringView` value (§22.2).

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
  - If some enclosing struct of `p.f` has a `drop` block, that whole struct must be initialised,
    `f` included: such a struct is never partially moved (N0252), so a missing field means the
    whole value was moved. This also covers a struct whose only field is `f` (found by the
    mutation suite, M2d).

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
`using x = e;` declares a local that cannot be moved out of (N0253) and is destroyed at the end
of its block, like any local (R3). It documents that the value's lifetime *is* the scope: a lock
guard, an arena, a file.

*Since M4b:* a `using` binding is **pinned, not immutable**. Its contents may be changed and it may
be borrowed mutably (`arena.reset()`, `frame.enter()`). It cannot be assigned as a whole (N0211)
or moved (N0253). This also lets scoped values that must be mutably borrowed, such as scratch
frames, live in a `using` binding, since they cannot be `var`.

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
Binding   = Identifier | 'var' Identifier | '_'        -- 'var': M3c (§18.4)
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
  - **Scrutinee of type `E` (a value):** the binding is a *copy* of the payload. Since M3c a
    non-Copy payload is moved out, and the match consumes its scrutinee (§18.4).
  - **Scrutinee of type `borrow E` / `borrow mut E`:** the binding is a `borrow T` /
    `borrow mut T` to the payload, and the borrow checker treats it like any reborrow of the
    scrutinee.
  - `_` binds nothing.
- The scrutinee is evaluated once. When it is a place it is not moved (only Copy payloads are
  read). A temporary scrutinee lives until the end of the `match` statement.

## 16. Arrays, slices and `for` (M2d)

### 16.1 Types
- `[T; N]` is a fixed array of `N` elements. `N` is an integer literal (no suffix or `usize`) with
  1 ≤ N ≤ 2^48 − 1, and the array is at most 2^31 − 1 bytes. It is laid out as `N` consecutive `T`s:
  size `N × size(T)`, the alignment of `T`.
  - **v0 restriction:** `T` must be a Copy type, so arrays are Copy and never need drop. Arrays of
    move types need element-wise move tracking (future). Slices may have move-type elements
    since M6b (§21.3).
  - Otherwise arrays may appear anywhere a value may: locals, parameters, results, struct fields,
    enum payloads and other arrays. Constants of array type are not supported yet (N0216).
- `Slice<T>` / `SliceMut<T>` are **borrowed views** `(pointer, length)` of a run of `T`s in an array
  or another slice. They follow every rule for references (§13), with the same scoping:
  - only in parameters, `let` locals with an initialiser, and return types with a source list;
  - never in fields, payloads, array elements, constants or `var` locals (N0246);
  - `Slice<T>` is Copy, and each use of a `SliceMut<T>` reborrows it; `SliceMut<T>` coerces to
    `Slice<T>`;
  - a slice holds a loan of the whole array (or slice) it was taken from (`lifetimes.md`);
  - a function returning a slice has no `borrow(...)` source list: it must have exactly one
    reference (or slice) parameter, which the result is derived from (§13.5's default rule);
  - the element type follows the array rules: Copy, never a reference.
- Internally, `Slice<T>` is `borrow [T]` and `SliceMut<T>` is `borrow mut [T]`, with `[T]` an
  unsized array type that only exists behind a slice. That is why every reference rule applies.

### 16.2 Values
- `[e1, …, en]`: all elements have the same type (the expected element type, or the first
  element's), n ≥ 1.
- `[v; N]`: N copies of `v`.
- `a.length` is a `usize`: the constant `N` for arrays, the stored length for slices. For an
  array, `a` must be a place (a variable, field or element), because the constant would skip
  evaluating it (N0216).

### 16.3 Indexing and slicing
- `a[i]`:
  - `a` is an array or slice place, or a reference to an array;
  - `i` is a `usize` (a flexible literal becomes `usize`);
  - `i ≥ a.length` **panics** ("index out of bounds", reported at the index expression);
  - `a[i]` is a place: it can be read (a copy), assigned (if mutable: an array rooted at a `var`
    or reached through `borrow mut`, or a `SliceMut`), and borrowed.
- `a[lo..hi]`, `a[lo..]`, `a[..hi]`, `a[..]` give a `Slice<T>` of elements `lo` up to but not
  including `hi` (default `0` and `a.length`). `lo ≤ hi ≤ a.length` is checked; otherwise the
  program **panics**.
- `borrow mut a[lo..hi]` gives a `SliceMut<T>` and needs `a` to be mutable. As a call argument
  for a `SliceMut<T>` parameter, `mut a[lo..hi]` means the same.
- Slicing needs a place (N0247): slice a local, not a call result.
- Evaluation order: the array place (with its own indexes), then `lo`, then `hi`. In an assignment
  `a[i] = v`, the target's indexes are evaluated and checked before `v`.
- The borrow checker treats all elements of an array as **overlapping** (no index reasoning in v0).
  Borrowing `a[0]` and then writing `a[1]` while the borrow is live is rejected.

### 16.4 `for`
The header is parenthesised, like `while` and `match`, so a name before the body is never read as a
struct literal.
- `for (i in lo..hi) { … }`: `i` takes each value from `lo` up to but not including `hi`. Both bounds
  are evaluated once, have the same integer type (flexible literals adapt), and `i` is immutable
  in the body.
- `for (x in seq) { … }`: `seq` is an array place, a slice, or a reference to an array. `x` is a copy
  of each element from index 0 upward. The element is read when the loop reaches it, and the
  length is read once at the start.
- `break` and `continue` behave as in `while`. `continue` proceeds to the next element.

- The loop variable is immutable. It has the bounds' type for a range, and the element type
  for a sequence. Like every local, it cannot shadow another (N0203).

Diagnostics: N0259 (an element type that is not Copy, an invalid length, or a too-large array).
The other errors reuse N0205, N0211, N0216, N0230, N0246, N0247 and N0248 (`diagnostics.md`).

## 17. Results and error propagation (M3a)

```nova
enum ParseError { Empty, BadDigit(u8), Overflow }

fn digit(c: u8) -> Result<u32, ParseError> {
    if (c < 48u8 || c > 57u8) { return Err(ParseError.BadDigit(c)); }
    return Ok((c - 48u8) as u32);
}

fn twoDigits(a: u8, b: u8) -> Result<u32, ParseError> {
    return Ok(digit(a)? * 10 + digit(b)?);
}
```

`Result`, `Ok`, `Err` and `AllocError` are built in (ADR 0003). User generics come in M6; the
names below then become library items with the same meaning.

### 17.1 Types
- `Result<T, E>` is a built-in enum with two variants, in this order: `Ok` with payload `T`, and
  `Err` with payload `E`. Everything in §15.1 applies: layout, Copy iff `T` and `E` are Copy,
  needs drop iff either does, and only the active variant's payload is destroyed.
- `T` may be `void`, in which case `Ok` has no payload. `E` must not be `void`. Neither may be a
  reference or slice (N0246, E3); arrays, structs, enums and results are fine.
- A type contains `T` and `E` by value: `struct S { r: Result<S, i32>; }` is recursive (N0227).
- `AllocError` is a built-in enum `{ OutOfMemory }` (Copy). The names `Result`, `Ok`, `Err` and
  `AllocError` cannot be redeclared as items (N0201).
- Two `Result` types are equal iff their `T` and their `E` are equal.

### 17.2 Values
- `Ok(e)`, `Ok()` (for `T = void`) and `Err(e)` need an **expected `Result` type** that fixes
  `T` and `E`. That type comes from a declared variable type, a parameter type, a return
  type (`return Ok(x);`), or a field or payload type. Without one, it is N0260 ("write the
  type").
- The payload is checked against `T` or `E` like a call argument against its parameter (§5).

### 17.3 Matching
- Patterns `Ok(x)`, `Ok(_)`, `Ok` (for `T = void`), `Err(x)` and `Err(_)` are written without a
  type prefix. The bindings and exhaustiveness rules of §15.2 apply unchanged: a non-Copy payload
  is bound by matching on `borrow r` (N0258 otherwise).

### 17.4 `e?` and `e!`
```text
Postfix = Primary ( … | '?' | '!' )*      -- '!' is postfix only when it follows an operand
```
- **`e?` (propagate).**
  - `e` must have a `Result<T, E>` type (N0262). The enclosing function must return
    `Result<U, E>` with the same `E` (N0261); `From` conversions come in M6.
  - If `e` is `Ok(v)`, the value of `e?` is `v` (of type `T`; `void` if `T` is `void`).
  - If `e` is `Err(x)`, the function returns `Err(x)` at once, exactly like a `return`
    statement. The statement's temporaries end, then every scope is left (drops and deferred
    statements), innermost first. Like `return`, `?` is not allowed in a deferred statement
    (N0254).
- **`e!` (unwrap).** `e` must have a `Result<T, E>` type (N0262). The value is `v` for `Ok(v)`;
  `Err(_)` **panics** with "unwrapped an Err value". Panic is abort, so nothing is destroyed.
- In both cases `e` is evaluated once and consumed. The payload moves out of it (or is copied);
  nothing else of `e` remains to be destroyed.
- As an expression statement: `f()?;` and `f()!;` are allowed when the operand is a call, like
  calls themselves (N0225 otherwise).
- Precedence: postfix, like a call or member access: `parse(a)?.value` and `-x?` work as
  expected (`-` applies to `x?`). `!` directly followed by `=` lexes as `!=`. Write `r! == 1`
  with a space.

### 17.5 Discarded values (correction to §14.5)
- An expression statement whose value needs drop (for example a call `make();` returning a
  `Res`) destroys the value at the end of the statement, like any other temporary. Before M3a
  the value was never destroyed: an O4 violation, found while specifying 3a.

Diagnostics: N0260 (no expected `Result` type for `Ok`/`Err`), N0261 (`?` in a function whose
return type is not a `Result` with the same error type), N0262 (`?` or `!` on a non-`Result`).

## 18. Boxes and allocators (M3b)

```nova
struct Account {
    id: u64;
    balance: i64;
    fn deposit(borrow mut this, amount: i64) { this.balance += amount; }
}

fn open(id: u64) -> Result<Box<Account>, AllocError> {
    return Box.create(Account { id: id, balance: 0 });
}

fn main() -> i32 {
    var acct = open(7)!;
    acct.deposit(100);       // through the box
    return 0;
}                            // the box is dropped: Account destroyed, memory freed
```

`Box`, `SystemHeap` and `FailingAllocator` are built in (ADR 0003).

### 18.1 Allocator types
- `SystemHeap` and `FailingAllocator` are built-in, field-less, Copy structs. They are the only
  **allocator types** in M3; user allocators come with interfaces in M6 (`memory-model.md` §3.2).
  Their values are written as struct literals: `SystemHeap {}`.
- `SystemHeap` is the platform heap (`malloc`/`free` in hosted and CosmoOS userland builds).
- `FailingAllocator` allocates from the platform heap, but can be told to fail, for testing
  error paths in the language itself:
  - `FailingAllocator.failAfter(n: u64)`: the next `n` allocations through `FailingAllocator`
    succeed, and every later one fails;
  - `FailingAllocator.reset()`: allocations never fail (the initial state).
  The countdown is process-wide; frees are never affected.
- Their names, and `Box`, cannot be declared as items (N0201).

### 18.2 `Box<T, A>`
- `Box<T, A>` owns exactly one value of type `T` in memory obtained from allocator type `A`
  (H1). `Box<T>` means `Box<T, SystemHeap>`. `A` must be an allocator type (N0263). `T` is any
  value type except `void` and references or slices (N0246).
- A box is a **move type** that needs drop, and is one pointer wide (8 bytes on LP64).
  Destroying it destroys `T` (its drop glue), then returns the memory to `A`.
- A type does **not** contain the target of a `Box` by value, so recursive types are built with
  boxes: `enum List { Nil, Cons(i32, Box<List>) }` is allowed (no N0227).
- **Creation** (built-in functions):
  - `Box.create(v)`: `Result<Box<T>, AllocError>`;
  - `Box.createIn(a, v)` with `a` a value of allocator type `A`: `Result<Box<T, A>, AllocError>`.
  `T` is the type of `v`. An expected type `Result<Box<X, _>, _>` provides the type hint for `v`
  (so `Box.create(5)` can make a `Box<u8>`). `v` is moved into the call. If the allocation
  fails, `v` is destroyed and the result is `Err(AllocError.OutOfMemory)`.
- **Access through a box** works like access through a reference (§13.3), with the box's own
  mutability:
  - fields `b.f`, elements `b[i]` and method calls `b.m()` reach `T` (auto-deref);
  - the contents are mutable iff the box is: the box variable is `var`, or the box itself is
    reached through `borrow mut` or another mutable place. `b.f = 1` needs a `var b`;
  - a `Box<T>` argument for a `borrow T` parameter borrows the contents, and `mut b` lends them
    for a `borrow mut T` parameter;
  - where a Copy `T` is expected, a `Box<T>` is read through (auto-deref);
  - a `borrow Box<T>` converts to `borrow T`, and a `borrow mut Box<T>` to `borrow mut T` or
    `borrow T`: a reborrow of the contents (for example, a payload bound by reference in a match);
  - `borrow b` borrows the box itself (`borrow Box<T>`).
- **Borrowing rules:** a place inside a box is part of the box. Moving, overwriting or dropping
  the box while a loan of its contents is live is rejected (N0240, N0243), as for any place and
  its fields. (Unlike a reference going out of scope, a box going out of scope destroys what it
  points to.)
- **Moving out of a box's contents**: see §18.3 (M3c).
- No null boxes exist. A `Box` is never Copy and has no `==`.

Diagnostics: N0263 (an allocator argument that is not an allocator type).

### 18.3 Moving out of a box (M3c)
- A box held in an **owned place** owns its contents as a part of that place: a local, or a
  field path of one, not reached through a reference. Its fields may be moved out
  (`let l = n.left;`, `move n.left`), exactly like fields of a struct local (§14.3–14.4):
  - the box cannot be used as a whole again (moved, passed, matched, borrowed) until every
    moved-out field is re-initialised by assignment (N0250);
  - a field of a `T` with a `drop` block cannot be moved out (N0252);
  - through a reference, a box's contents are as immovable as anything else (N0251).
- Destroying a partially moved box destroys the fields still initialised (in declaration order),
  then returns the memory to the allocator. `T`'s drop glue does not run, and `T` has no `drop`
  block by the rule above.
- There is no way to move the whole contents out in v0 (`T` would need an unbox operation, M6).

### 18.4 Moving payloads out in `match` (M3c; replaces N0258)
```text
Binding = Identifier | 'var' Identifier | '_'
```
- `var x` makes a mutable binding; `x` an immutable one. Both may bind any payload type.
- **Consuming match.** If the scrutinee is a value (not a reference) and some arm binds a
  non-Copy payload, the match **consumes** its scrutinee:
  - the scrutinee is moved when the match starts. A place scrutinee is left uninitialised, as by
    `move`; a `using` binding cannot be consumed (N0253);
  - in an arm whose pattern lists payloads (`E.V(…)`), every payload of the variant moves out:
    named ones into their bindings, and `_` ones into hidden locals of the arm. Payloads not bound
    by name are therefore destroyed when the arm ends, before the bindings' own scope ends;
  - in any other arm (`_`, `E.V` without a list, a variant without payload), the value is
    destroyed when the match statement ends.
- Otherwise (Copy payloads only, or a reference scrutinee) the rules of §15.2 apply unchanged.
- N0258 is no longer reported (since M3c).

### 18.5 Destroying deep structures (R-9)
- A box's drop glue is recursive in general: destroying a `T` destroys its fields, which may own
  further boxes. For a recursive type that would use stack in proportion to the structure's depth.
- **Tail iteration.** The compiler finds the *tail* of `T`: the last field (in destruction order) that
  needs drop, followed through structs, and through enum variants' last payloads, until it
  reaches a `Box<T>` again. If there is one, `Box<T>`'s glue becomes a loop:
  - destroy everything of the current `T` except the tail, in the normal order;
  - take the tail box's pointer (null for variants that end elsewhere);
  - free the current memory, then continue with the tail's contents.
- Only the moment the memory is freed changes: the current node is freed before the tail is
  destroyed instead of after. No program can observe that difference; every `drop` block runs in
  the order §14.5 defines.
- Consequence: lists (`Cons(x, Box<List>)`) are destroyed in constant stack depth. Trees use
  stack in proportion to the number of non-tail steps on a path (the left children in
  `{ left, right }`). Balanced trees therefore use logarithmic depth, and a left-degenerate tree
  still recurses (R-9 records this).

## 19. Regions (M4)

`Arena`, and in later increments `FixedBufferAllocator`, `ScratchFrame`, `Pool<T>` and
`PoolBox<T>`, are built in (ADR 0004). The safety rules are R1–R4 (`ownership.md`): an
allocation borrows its region.

### 19.1 Scoped types (M4a)
A type is **scoped** if a value of it may hold loans:
- references and slices (§13, §16);
- `Result<T, E>` whose `T` or `E` is scoped (a *scoped result*);
- the scoped region values: `FixedBufferAllocator` (§19.4), `ScratchFrame` (§19.5) and
  `PoolBox<T>` (§19.6).

Every rule for references applies to scoped types (N0246):
- they may appear only in parameters, locals with an initialiser, and return types;
- never in fields, payloads of user enums, array elements, box contents or constants.

References and slices must be `let` locals, because assigning to a reference writes through it.
*Since M4c,* other scoped values (scoped results and region values) may be `var`: assigning one
replaces it, like any value. For the borrow checker, the local then holds the loans of every value
it is assigned (a sound over-approximation).

A function returning a scoped type needs return sources: the default rule (one reference
parameter) or a `borrow(...)` list (§13.5). In this rule, "reference parameter" now means
"parameter of a scoped type", and the receiver `this` counts when it is borrowed.

The borrow checker treats every local of a scoped type as holding loans (`lifetimes.md` §3).
Moving or copying a scoped value moves or copies its loans, as for references. A scoped
result's loans reach the `Ok` payload through `?`, `!` and `match`.

### 19.2 `Arena` (M4a)
```nova
fn process(input: Slice<u8>) -> Result<u64, AllocError> {
    using arena = Arena.create(SystemHeap {}, 65536u64);
    let counts = arena.allocArray(256usize, 0u32)?;   // SliceMut<u32>, a loan of `arena`
    for (b in input) { counts[b as usize] += 1u32; }
    var distinct: u64 = 0;
    for (c in counts) { if (c != 0u32) { distinct += 1; } }
    return Ok(distinct);                              // a u64, not arena memory
}                                                     // the arena frees all its memory here
```
- `Arena` is a built-in move type that needs drop. It is not scoped: it owns its memory, which
  it obtains from a free-standing allocator.
- `Arena.create(a, chunkSize)`, with `a` an allocator value (`SystemHeap {}`, `FailingAllocator {}`)
  and `chunkSize: u64`, makes an empty arena. It cannot fail: memory is obtained in chunks of
  `chunkSize` bytes when needed. An allocation larger than a chunk gets a chunk of its own.
- **Allocation** (R1). Both methods take `borrow this` and return a scoped result that carries a
  **shared loan of the arena**:
  - `arena.alloc(v)`: `Result<borrow mut T, AllocError>`, with `v` moved into fresh arena memory;
  - `arena.allocArray(n, fill)` with `n: usize`: `Result<SliceMut<T>, AllocError>`, `n` copies
    of `fill`. `T` must be Copy. `n` may be 0 (an empty slice); `n × size(T)` beyond the address
    space is `Err(OutOfMemory)`.

  The returned references are mutable: each allocation is fresh memory that nothing else refers to.
- **R2:** `T` must not need drop, and must not be scoped (no references or slices in arena memory,
  E3). Both are N0264.
- `arena.reset()` takes `borrow mut this` and releases all memory. Because it is a mutable borrow,
  it is rejected while any allocation is live (B1/B2). Dropping or moving the arena is likewise
  rejected while allocations are live.
- Destroying an arena releases all its memory at once (bulk release). Since no allocation
  needs drop (R2), this equals destroying each allocation.
- Failure: if a chunk cannot be obtained from the allocator, the allocation returns
  `Err(AllocError.OutOfMemory)`; the arena stays usable.

### 19.3 What cannot escape (M4a, the R1 consequences)
- An allocation cannot be returned from the function that owns the arena: N0244, like any
  reference to a local.
- It cannot be stored in a box, a struct, an enum payload or an array: those cannot hold scoped
  values (N0246).
- It cannot outlive the arena: a `reset`, drop, move or scope end of the arena while the
  allocation is live is N0240–N0243.

Diagnostics: N0264 (a type that a region cannot hold).

### 19.4 `FixedBufferAllocator` (M4b)
```nova
fn countWords(text: Slice<u8>) -> Result<u32, AllocError> {
    var storage = [0u8; 4096];
    let fba = FixedBufferAllocator.over(mut storage);   // a mutable loan of `storage`
    let spans = fba.allocArray(64usize, Span { start: 0u32, len: 0u32 })?;
    ...
}
```
- `FixedBufferAllocator.over(mut s)` makes an allocator over caller-provided bytes. `s` is a
  mutable place of type `[u8; N]`, or a `SliceMut<u8>`. The allocator holds a **mutable loan**
  of the storage, so the storage cannot be read or written while the allocator or any of its
  allocations is live.
- `FixedBufferAllocator` is **scoped** (§19.1): a `let` local, a parameter or a return type with
  sources, never stored. It is a move type without drop; nothing is freed.
- `alloc(v)` and `allocArray(n, fill)` work as for `Arena` (§19.2), including R2 (N0264). Each
  allocation is aligned within the storage. When the remaining bytes are not enough, the result
  is `Err(AllocError.OutOfMemory)`; the storage is never grown. There is no `reset`: to reuse the
  storage, let the allocator end and make a new one.
- An allocation's loans include the allocator's loan of the storage, so the storage stays
  borrowed while any allocation is live, even after the allocator's last use.

### 19.5 Scratch frames (M4b; ADR 0004)
```nova
fn render(n: usize) -> Result<u64, AllocError> {
    using frame = Scratch.enter();               // a root frame
    let buf = frame.allocArray(n, 0u8)?;         // scoped to `frame`
    {
        using inner = frame.enter();             // a nested frame borrows `frame` mutably
        let tmp = inner.alloc(1u64)?;
        ...
    }                                            // `inner`'s memory is released here
    return Ok(buf.length as u64);
}                                                // `frame`'s memory is released here
```
- Scratch memory is a runtime-owned stack of heap chunks (`SystemHeap`); it is not the native stack.
- `Scratch.enter()` makes a **root frame**. Only one root frame may be live at a time (per
  thread; v0 is single-threaded): entering a second one **panics** ("a scratch frame is already
  active"). Code that needs scratch memory below a frame receives the frame
  (`borrow ScratchFrame`) and nests.
- `frame.enter()` makes a **nested frame**. For the borrow checker a frame has two parts: its
  *memory*, which allocations borrow, and its *allocation right*, which every allocation call
  uses briefly. The rules:
  - an allocation result carries a shared loan of the frame's memory;
  - each `alloc`/`allocArray` call needs a shared borrow of the allocation right for the call
    only;
  - `enter()` takes a **mutable** loan of the parent's allocation right, which the nested frame
    carries.

  While a nested frame is live, the parent therefore cannot allocate or nest again (B1), but
  allocations already made from the parent stay usable: they lie below the nested frame's
  memory. Destroying or moving a frame conflicts with both parts. This makes frames strictly LIFO.
- Calling a frame method needs a frame that is a place (`using frame = …`): N0247 otherwise.
- `ScratchFrame` is scoped (§19.1) and needs drop. Destroying a frame releases everything
  allocated from it (and from frames nested in it), and returns chunks to the heap.
- `alloc(v)` and `allocArray(n, fill)` work as for `Arena`, including R2. A failure to obtain a
  chunk is `Err(AllocError.OutOfMemory)`.
- Frames are bound with `using` (R3; §14.6): nesting borrows the frame mutably, and a scoped
  value cannot be a `var`. Since frames are scoped, they cannot be returned from the function
  that entered them.

### 19.6 `Pool<T>` and `PoolBox<T>` (M4c)
```nova
struct Particle { x: i32; v: i32; }

fn simulate() -> Result<i32, AllocError> {
    using pool: Pool<Particle> = Pool.create(SystemHeap {}, 64u32);
    var a = pool.take(Particle { x: 0, v: 2 })?;     // PoolBox<Particle>: a loan of `pool`
    let b = pool.take(Particle { x: 10, v: -1 })?;
    a.x += a.v;                                      // through the box
    return Ok(a.x + b.x);
}                                                    // b, a, then the pool are destroyed
```
- `Pool<T>` is a built-in allocator for up to `capacity` values of one type `T`.
  - `Pool.create(a, capacity)`, with `a` an allocator value and `capacity: u32`, makes an empty
    pool. `T` comes from the expected type (`let pool: Pool<T> = …`); without one it is N0260.
  - It cannot fail: the slots are allocated from `a` at the first `take`.
  - `Pool<T>` is a move type that needs drop; destroying it returns the slots to `a`. It is not
    scoped.
- `pool.take(v)` takes `borrow this` and returns `Result<PoolBox<T>, AllocError>`: `v` moves into
  a free slot. The result is `Err(AllocError.OutOfMemory)` when all slots are taken, or when the
  slots cannot be allocated; `v` is then destroyed.
- `PoolBox<T>` owns one slot and its value, like a `Box` (§18.2):
  - auto-deref, the box's own mutability, borrowing its contents, and moving fields out of it
    (§18.3) all work as for `Box`;
  - it is a move type that needs drop: destroying it destroys the `T` (its drop glue), then
    returns the slot to the pool. Values are destroyed one by one, so **`T` may need drop**: R2
    does not apply to pools;
  - it is **scoped**: it carries the shared loan of the pool from `take`. It cannot be returned
    past the pool, stored in a field or box, or outlive the pool (§19.3);
  - `T` must not be scoped (E3; N0264).
- Destroying the pool while a `PoolBox` from it is live is rejected (B2), so every slot is free
  when the pool is destroyed.

## 20. Generics (M6a)

```nova
struct Pair<T> {
    first: T;
    second: T;
    fn swap(this) -> Pair<T> => Pair { first: this.second, second: this.first };
}

enum Maybe<T> { Nothing, Just(T) }

fn largest<T>(p: borrow Pair<T>, pick: bool) -> borrow T {
    if (pick) { return borrow p.first; }
    return borrow p.second;
}

fn main() {
    let p = Pair { first: 1, second: 2 };        // Pair<i32>, inferred from the fields
    let q = p.swap();
    let m: Maybe<u8> = Maybe.Just(7u8);
    let r = largest(p, true);                    // T = i32, inferred from the argument
    let s = largest<i32>(q, false);              // explicit type argument
}
```

### 20.1 Declarations
```text
TypeParams = '<' Identifier (',' Identifier)* '>'
FunctionDecl = 'fn' Identifier TypeParams? '(' … ')' …
StructDecl   = 'struct' Identifier TypeParams? '{' … '}'
EnumDecl     = 'enum' Identifier TypeParams? '{' … '}'
```
- A top-level function, struct or enum may declare **type parameters**. Inside the declaration,
  each parameter is a type. Methods of a generic struct see the struct's parameters; methods
  cannot declare parameters of their own in v0.
- Names must be distinct, and must not shadow items or built-in types (N0201).

### 20.2 Using generic items
- **Types:** `Pair<i32>`, `Maybe<Pair<u8>>`. A generic struct or enum must always be given all its
  type arguments in a type (N0204).
- **Calls:** `largest<i32>(p, true)` gives the arguments explicitly. `largest(p, true)` infers
  them from the argument types: each parameter type is matched against its argument's type. An
  integer literal argument is `i32` unless the parameter type fixes it. A parameter that the
  arguments do not determine is N0265 ("write the type arguments").
- **Values:** `Pair { first: 1, second: 2 }` infers `T` from the field values, or takes it from the
  expected type. `Pair<u8> { … }` gives it. Likewise `Maybe.Just(7)` infers from the payload, and
  `Maybe.Nothing` needs an expected type or explicit arguments: `Maybe<u8>.Nothing`.
- **Static functions:** `Pair<i32>.make(…)`.

### 20.3 Checking generic code once
- A generic declaration is checked **once, for all type arguments**. Each type parameter is an
  opaque type, about which only what holds for every type is known:
  - it is a **move type** that **may need drop**. A value of it can be moved, borrowed,
    passed, returned, stored in fields of the declaring type, and matched as a payload;
  - nothing else is allowed: no operators, no fields, no methods, no literals, no
    `==` (N0207, N0234, N0205). Interfaces (§21) add operations through bounds.
- Move checking, borrow checking and reachability run on the generic code too. A body that
  would be wrong for some type argument is rejected at the definition, **whether or not it is ever
  instantiated** (for example, using a `T` twice is N0250).
- Generic declarations that are never used produce no C code.

### 20.4 Instantiation (monomorphisation)
- Every distinct use (`largest<i32>`, `Pair<Res>`) is an **instantiation**: a copy of the
  declaration with the type arguments substituted. It is checked again and compiled to C.
- An instantiated struct or enum is an ordinary type for every other rule. It is Copy iff its
  substituted fields or payloads are, and needs drop iff one of them does. `Pair<i32>` is Copy;
  `Pair<Res>` is a move type.
- The layout is that of the substituted declaration. Recursion is checked per instantiation
  (N0227).
- Instantiation is depth-limited: a chain of more than 64 nested instantiations (polymorphic
  recursion such as `f<T>` calling `f<Box<T>>`) is N0266.

Diagnostics: N0265 (type arguments that cannot be inferred), N0266 (instantiation too deep).

## 21. Interfaces (M6b)

```nova
interface Shape {
    fn area(borrow this) -> i64;
}

struct Square { side: i64; }
impl Shape for Square {
    fn area(borrow this) -> i64 => this.side * this.side;
}

fn total<S: Shape>(shapes: Slice<S>) -> i64 {
    var t = 0i64;
    for (i in 0..shapes.length) { t += shapes[i].area(); }
    return t;
}
```

### 21.1 Declarations
```text
InterfaceDecl = 'interface' Identifier TypeParams? '{' MethodSig* '}'
MethodSig     = 'fn' Identifier '(' Receiver (',' Param)* ')' ('->' Type)? ';'
ImplDecl      = 'impl' Type 'for' Type '{' FunctionDecl* '}'
```
- An **interface** lists method signatures. Every method has a receiver (`this`, `borrow this`,
  `borrow mut this`). An interface may be generic (`interface Comparable<T>`). Its methods
  cannot have type parameters of their own, and v0 has no `Self` type.
- **`impl I for T { … }`** implements interface `I` (with its type arguments, `I<X>`) for a
  concrete type `T`: a struct, an enum, or a built-in type such as `i32`. `T` must not be generic
  in v0 (`impl<T> I for Pair<T>` is future work).
  - The block must define exactly the interface's methods, each with the same receiver kind,
    parameter types and return type, after the interface's type arguments are substituted
    (N0267). It may not define other functions.
  - Each type may implement each interface instantiation at most once (N0267).
  - An `impl` block's methods are ordinary methods of `T`. `x.area()` works on any `Square`, and
    their names must not clash with `T`'s other methods (N0228).
- Interfaces are not types: there is no `dyn Shape` value in v0 (OD-10). They are used only as
  bounds.

### 21.2 Bounds
- A type parameter may have bounds: `<T: Shape>`, `<T: Comparable<T> + Copy>`.
- **Inside generic code**, a value whose type is `T` (or a reference to one) may call the methods
  of `T`'s bounds, with the signatures the interfaces give (after substitution). A call that no
  bound provides is N0234. If two bounds provide the same method name, the call is ambiguous
  (N0267).
- **`Copy`** is a built-in bound: a `T: Copy` is a Copy type in the generic code (it can be used
  twice and stored in arrays) and never needs drop. Only Copy types satisfy it.
- **At every instantiation** (a call, type or value that gives `T`), each bound must hold: the
  argument implements the interface (for a type parameter argument, its own bounds include it),
  or is Copy for `Copy`. Otherwise it is N0268, reported where the instantiation is written.
- Dispatch is static: an instantiation calls `T`'s `impl` method directly (monomorphisation).

### 21.3 Slices of move types (correction to §16.1)
- `Slice<T>` and `SliceMut<T>` may have any non-scoped element type, not just Copy ones. A slice
  only views elements: reading one by value still needs a Copy type (N0251 otherwise), but
  borrowing one (`borrow s[i]`, `s[i].method()`) does not. Arrays still need Copy elements
  (§16.1), since an array owns its elements and v0 tracks no per-element moves.

Diagnostics: N0267 (an `impl` that does not match its interface, a duplicate `impl`, or an
ambiguous bound method), N0268 (a type argument that does not satisfy a bound).

## 22. Optionals and string views (M6c)

### 22.1 Optionals `T?`
```nova
fn find(xs: Slice<i32>, wanted: i32) -> usize? {
    for (i in 0..xs.length) { if (xs[i] == wanted) { return i; } }   // i converts to usize?
    return null;
}

fn firstTwo(xs: Slice<i32>) -> usize? {
    let a = find(xs, 1)?;                  // null propagates
    return a + 1;
}

fn main() {
    let numbers = [3, 1, 2];
    match (find(numbers[..], 2)) {
        Some(i) => { Console.writeLine(i); }
        null => { Console.writeLine("absent"); }
    }
    let i = find(numbers[..], 3)!;         // panics on null
}
```
- `T?` (postfix `?` in a type) is an optional `T`, for any non-`void` `T`. It is a built-in enum with
  two variants, in this order: `null` (no payload) and `Some(T)`. Everything in §15.1 applies:
  it is Copy iff `T` is, needs drop iff `T` does, and is scoped iff `T` is (`borrow T?`).
- **Values.**
  - `null` needs an expected optional type (N0260).
  - `Some(e)` makes a present value; with an expected `T?`, `e` is checked against `T`, otherwise
    `T` is `e`'s type.
  - Where a `T?` is expected (an initialiser, an argument, a return value, a field), a value of
    type `T` converts to `Some(value)` implicitly.
- **Matching.** Patterns `null`, `Some(x)` and `Some(_)` are written without a type prefix; the
  rules of §15.2 and §18.4 apply.
- **Comparisons.** `o == null` and `o != null` test for absence, for any `T` (the value is only
  read, not consumed). Other comparisons of optionals are not defined (N0207).
- **`o!`** is the value, or **panics** with "unwrapped null".
- **`o?`** is the value, or returns `null` from the enclosing function, which must return an
  optional (N0261). `?` on an optional in a function returning a `Result` is N0261 in v0
  (`language-spec.md` Ex11).
- **Layout (v0):** like any enum, `{ tag; T }`. The niche optimisation for `Box` and references
  (`language-spec.md` §2) is future work; it changes no semantics.

### 22.2 `StringView`
```nova
fn greeting(loud: bool) -> StringView {
    if (loud) { return "HELLO"; }
    return "hello";
}

fn main() {
    let s: StringView = "Nova language";
    Console.writeLine(s);
    Console.writeLine(s.length);                 // 13 (bytes)
    let first = s[0..4];                         // "Nova": a view of s
    Console.writeLine(first == "Nova");
    for (b in s.bytes()) { … }                   // Slice<u8>
}
```
- `StringView` is a **shared, read-only view of UTF-8 text** `(pointer, length in bytes)`. It is
  scoped like `Slice<T>` (§16.1), and Copy.
- **String literals** have type `StringView` (their bytes are static data). A literal's view holds
  no loans. A function may therefore return a `StringView` without scoped parameters (like
  `greeting` above). The return check still rejects any view that borrows a local (N0244).
- **Operations.**
  - `s.length` is the length in bytes (`usize`);
  - `s[i]` is the byte at `i` (`u8`, bounds-checked);
  - `s.bytes()` is the same bytes as a `Slice<u8>`;
  - `s[lo..hi]` (any bound may be omitted) is a `StringView` of bytes `lo` up to `hi`. Besides the
    range check of §16.3, both bounds must fall on character boundaries (not inside a UTF-8
    multi-byte sequence). Otherwise it **panics** with "string slice not on a character
    boundary";
  - `==` and `!=` compare the bytes;
  - `Console.write(s)` and `Console.writeLine(s)` print it.
- `for (b in s)` is not defined: iterate over `s.bytes()` (characters come with `String`
  methods later).

Diagnostics: none new. `null` without an expected type is N0260; `?` and `!` on non-optional,
non-result values are N0262.

## 23. `Vector<T, A>` and `String<A>` (M6d)

### 23.1 `Vector<T, A>`
```nova
fn sum(xs: Slice<i32>) -> i32 {
    var total = 0;
    for (x in xs) { total += x; }
    return total;
}

fn main() -> i32 {
    var v = Vector<i32>.create();          // no allocation until the first push
    v.push(1)!;                             // Result<void, AllocError>
    v.push(v.length as i32)!;               // two-phase borrow (B5)
    let r = borrow v[0];
    // v.push(3)!;                          // error[N0240]: `v` is borrowed by `r`
    Console.writeLine(r);
    v.push(3)!;                             // fine: `r` is dead
    Console.writeLine(sum(v));              // a vector argument for a slice parameter
    return 0;
}
```
- `Vector<T, A>` owns a growable, contiguous buffer of `T` values obtained from allocator type `A`
  (H2). `Vector<T>` means `Vector<T, SystemHeap>`. `A` must be an allocator type (N0263). `T` is
  any value type except `void` and scoped types (N0246; references in aggregates are OD-3).
- A vector is a **move type** that needs drop and is never Copy. It is not scoped. Destroying it
  destroys the elements from index 0 upward, then returns the buffer to `A`.
- **Creation** (built-in functions on the type; the type arguments come from the explicit list or,
  without one, from an expected `Vector<T, A>` or `Result<Vector<T, A>, _>`; N0260 otherwise):
  - `Vector<T>.create()`: an empty vector. It allocates nothing;
  - `Vector<T>.withCapacity(n)` (`n: usize`): `Result<Vector<T, A>, AllocError>`, an empty vector
    with room for `n` elements (`n == 0` allocates nothing).
- **Members:**
  - `v.length` and `v.capacity` (`usize`);
  - `v[i]` is element `i`, bounds-checked (panic "index out of bounds"). It is a place, like an
    element of a slice: it can be read (Copy `T`), borrowed (`borrow v[i]`, `borrow mut v[i]`) and
    assigned (`v[i] = x` destroys the old element). Writing needs a mutable vector: a `var`, or
    one reached through `borrow mut`. Moving an element out is rejected (N0251), as for a slice;
  - `v.push(x)`: `Result<void, AllocError>`. It appends `x`, growing the buffer when it is full
    (new capacity: twice the old, at least 4). If the allocation fails, `x` is destroyed, the
    vector is unchanged, and the result is `Err(AllocError.OutOfMemory)`;
  - `v.pop()`: `T?`, the last element moved out, or `null` if the vector is empty;
  - `v.clear()`: destroys every element; the capacity stays;
  - `v.view()`: the elements as a `Slice<T>`, and `v.viewMut()` as a `SliceMut<T>`;
  - a `Vector<T>` argument for a `Slice<T>` parameter is borrowed as `v.view()`, and `mut v` for a
    `SliceMut<T>` parameter as `v.viewMut()` (like a box's contents, §18.2);
  - `for (x in v)` is `for (x in v.view())` (§16.4).
- **Calls take `this` like methods:** `push`, `pop` and `clear` take `borrow mut this`, and their
  arguments are evaluated before the mutable loan starts (two-phase, B5, `lifetimes.md` §5).
  `length`, `capacity`, `view` and element reads take `borrow this`.
- **Invalidation (H2, acceptance criterion 1 of M6):** an element borrow, a view, or anything
  derived from them is a loan of the vector. While one is live, `push`, `pop`, `clear`, element
  writes, `viewMut`, moving the vector and destroying it are rejected (N0240, N0241, N0243),
  exactly as for a place and its fields. The vector makes **no address-stability promise**:
  growth moves the elements.
- No `==`, no `Copy`.
- **Layout (v0):** `{ T *ptr; u64 length; u64 capacity }` (24 bytes on LP64). An empty vector's
  pointer is a valid, aligned, never-dereferenced address (not null), so views of it are valid.

### 23.2 `String<A>`
```nova
fn greet(name: StringView) -> Result<String, AllocError> {
    var s = String.withCapacity(name.length + 7)?;
    s.append("Hello, ")?;
    s.append(name)?;
    return Ok(s);
}

fn main() -> i32 {
    let msg = greet("Nova")!;
    Console.writeLine(msg.view());          // a StringView borrowed from msg
    return 0;
}
```
- `String<A>` owns UTF-8 text in a growable buffer (`String` means `String<SystemHeap>`). It is a
  move type that needs drop, like `Vector<u8, A>`, but a distinct type.
- **UTF-8 invariant:** text only enters a string through `append` of a `StringView`, which is
  always valid UTF-8 (§22.2), so a string's bytes are always valid UTF-8. Its bytes cannot be
  written: `s[i] = b` is rejected (N0211), and `borrow mut s[i]` and mutable slices of a string
  (N0248).
- **Creation:** `String.create()` (no allocation) and `String.withCapacity(n)`:
  `Result<String<A>, AllocError>`; `String<A>.create()` for another allocator.
- **Members:**
  - `s.append(text)` (`text: StringView`): `Result<void, AllocError>`, two-phase like `push`.
    Growth is as for vectors. On failure the string is unchanged;
  - `s.clear()`, `s.length` (bytes), `s.capacity`;
  - `s.view()`: the text as a `StringView`, a loan of `s`; a `String` argument for a `StringView`
    parameter is borrowed the same way;
  - read-only access as for a view: `s[i]` (`u8`), `s[lo..hi]` (a `StringView`, boundary-checked),
    `s.bytes()`;
  - printing: `Console.writeLine(s)` prints the text of a string place.
- Appending a view of the string itself (`s.append(s.view())`) is rejected (N0240): the view is a
  live shared loan of `s` when the call's mutable loan starts.
- **Layout (v0):** as `Vector<u8, A>`.

Diagnostics: none new. The errors reuse N0205, N0209, N0211, N0240–N0251, N0260 and N0263.

## 24. `SlotMap<T, A>`, `Handle<T>` and `StableVector<T, A>` (M6e)

### 24.1 `SlotMap<T, A>` and `Handle<T>`
```nova
struct Node { value: i32; next: Handle<Node>?; }

fn main() {
    var nodes = SlotMap<Node>.create();
    let a = nodes.insert(Node { value: 1, next: null })!;
    let b = nodes.insert(Node { value: 2, next: a })!;   // a handle is a plain value, not a loan
    match (nodes.get(b)) {
        Some(node) => { Console.writeLine(node.value); }
        null => { }
    }
    let removed = nodes.remove(a);                       // `a` is now stale
    Console.writeLine(nodes.get(a) == null);              // true: generation mismatch
}
```
- `SlotMap<T, A>` owns values of `T` in reusable slots from allocator type `A` (`SlotMap<T>` means
  `SlotMap<T, SystemHeap>`; N0263). `T` is any value type except `void` and scoped types (N0246).
  A slot map is a move type that needs drop, and is not scoped. Destroying it destroys the values
  it holds, then frees its storage.
- `Handle<T>` names one inserted value: a slot index and the slot's generation. It is a **Copy
  value, not a loan**: it can be stored in structs, vectors and other slot maps (unlike a
  reference, E3), and it outlives inserts, removals and moves of the map. `==` and `!=` compare
  handles. Handles are only made by `insert`.
- **Operations:**
  - `SlotMap<T>.create()`: an empty map; it allocates nothing (type arguments as for vectors,
    §23.1);
  - `m.insert(v)`: `Result<Handle<T>, AllocError>`, taking `borrow mut this` (two-phase, B5). If the
    storage cannot grow, `v` is destroyed and the result is `Err(AllocError.OutOfMemory)`;
  - `m.get(h)`: `borrow T?` (a loan of `m`), `null` if `h` does not name a live value;
  - `m.getMut(h)`: `borrow mut T?` (a mutable loan of `m`; needs a mutable map);
  - `m.remove(h)`: `T?`, the value moved out (or `null`), taking `borrow mut this`;
  - `m.contains(h)`: `bool`; `m.length`: the number of live values (`usize`).
- **Validation:** every access compares the handle's generation with the slot's. A removed value's
  handles never match again: a slot's generation changes on every insert and removal, and a slot
  whose generation would wrap around is retired instead of reused. A handle used with another map
  of the same `T` is memory-safe: it yields that map's value in that slot, or `null` (a logic
  error, as with any index).
- **Invalidation:** references from `get`/`getMut` are loans of the map, so `insert` and `remove`
  are rejected while one is live (N0241). The map may reallocate its slots: it promises stable
  **identity** (handles), not stable addresses.
- **Layout (v0):** `{ slot *ptr; u64 length; u64 capacity; u64 count; u32 free }` (40 bytes), each
  slot `{ u32 generation; u32 nextFree; T value }`; `Handle<T>` is `{ u32 index; u32 generation }`.

### 24.2 `StableVector<T, A>`
```nova
fn main() {
    let names = StableVector<i32>.create();   // `let`: push needs only `borrow this`
    names.push(10)!;
    let first = borrow names[0];
    names.push(20)!;                           // fine: elements never move
    Console.writeLine(first);
}
```
- `StableVector<T, A>` is a **grow-only** sequence whose elements never move (`memory-model.md` §4):
  it allocates chunks of 8, 16, 32, … elements and never reallocates them. Ownership, element
  types, `create()` and destruction are as for `Vector` (§23.1).
- `sv.push(x)`: `Result<usize, AllocError>` (the new element's index). It takes **`borrow this`**:
  appending changes only the length and the chunk table, never an existing element, so element
  borrows stay valid across it (interior mutability; like region allocation, §19.2). On failure
  `x` is destroyed and the vector is unchanged.
- `sv.length` (`usize`) and `sv[i]` (a bounds-checked place, as for vectors: read, `borrow`,
  `borrow mut` and assignment, the last two needing a mutable vector).
- No `pop`, `clear`, views or `for` (the elements are not contiguous): iterate with
  `for (i in 0..sv.length)`. A `borrow mut sv[i]` is a mutable loan of `sv`, so `push` is rejected
  while one is live (N0241).
- Not `Sync` (concurrency is later work). **Layout (v0):** `{ void *chunks[32]; u64 length }` (264
  bytes); element `i` is in chunk `⌊log2(i + 8)⌋ − 3`. At most `8 · (2³² − 1)` elements; beyond that
  `push` fails with `OutOfMemory`.

Diagnostics: none new. The errors reuse N0205, N0209, N0240–N0251, N0260 and N0263.

## 25. Region parameters (M6x; OD-3, ADR 0006)

```nova
enum Kind { Word, Number }
struct Token<region src> { kind: Kind; text: StringView<src>; }

struct Lexer<region src> { input: StringView<src>; position: usize; }

fn next<region src>(lexer: borrow mut Lexer<src>) -> Token<src>? { … }   // the token views the input

fn split<region r>(line: StringView<r>) -> Result<Vector<StringView<r>>, AllocError> { … }
```

### 25.1 Declaring regions
- Structs, enums and functions may declare **region parameters** before their type parameters:
  `struct Lexer<region src, T>`, `fn f<region a, region b, T>(…)`. In a parameter list, `region`
  followed by a name declares a region; `region` alone is still an ordinary name.
- A method may use its struct's regions. `static` is the built-in region of static data (string
  literals); it outlives everything.

### 25.2 Region arguments
- A type with region parameters takes region arguments first, then its type arguments:
  `Token<src>`, `Pair<r, i32>`. The built-in scoped types take one region optionally:
  `StringView<r>`, `Slice<r, T>`, `SliceMut<r, T>`, and references `borrow(r) T`,
  `borrow mut(r) T`.
- Region arguments may be **omitted** (all of them, never some): in function bodies they are
  inferred, in signatures elided (§25.6), and in fields they are simply not checked (regions are
  erased, §25.5).
- A written region must be in scope: a region parameter of the enclosing item or function, or
  `static`. In a function's result, `borrow(p)` may also name a parameter, as in v0 (§13.5).
  Otherwise, or with the wrong number of arguments: N0273.

### 25.3 Scoped types
A type is **scoped** (§19.1) iff it is a reference, slice or `StringView`, a scoped region value,
a `PoolBox`, an optional or result with a scoped part, a struct or enum that declares region
parameters, an instance of a generic struct or enum with a scoped type argument, or a `Vector`,
`StableVector` or `SlotMap` with a scoped element. `Handle<T>` is not scoped (it holds no loan).

### 25.4 Where scoped values may be stored
- **Fields and payloads:** a struct field or enum payload may have a scoped type only if the
  struct or enum declares at least one region parameter, or the type is a type parameter (its
  instances are scoped then, §25.3). Otherwise: N0270.
- **Generic instances:** a user generic type or function may be instantiated with scoped type
  arguments. If the generic needs an unscoped type there (it boxes `T`, makes an array of `T`, …),
  the instantiation is rejected where it is written: N0274.
- **Collections:** `Vector`, `StableVector` and `SlotMap` elements may be scoped; so may the value
  passed to a region's `alloc`, except a bare reference (N0264: region memory holds values).
- **Assignment:** assigning to a reference *local* still writes through it (§13.3). Assigning to a
  field or element of reference type replaces the reference stored there (`token.text = view`).
- **Still rejected (N0246):** `Box`, `Pool`, statics and constants, array and slice elements, and
  written references to references. **Region values and pool boxes** (`FixedBufferAllocator`,
  `ScratchFrame`, `PoolBox<T>`) stay out of every place this section opens up (fields, payloads,
  collection elements, type arguments of user types): they must stay pinned or LIFO in locals.

### 25.5 Regions are erased
Regions do not change types: `Token<a>` and `Token<b>` are the same type, and a value's loans are
one set (ADR 0006). Region arguments matter only for the checks of §25.2–§25.4 and for signatures.

### 25.6 Signatures: flows
Each parameter and the result of a function have **flow names**:
- the regions written in their types; `borrow(p)` in the result contributes `p`'s flow names;
- the type parameters their types mention;
- for a parameter of scoped type without written regions: a fresh name of its own (elision);
- for a result of scoped type without written regions: the flow names of the single scoped
  parameter (v0 elision); with several scoped parameters and none written, N0245 (as in v0).

`static` is never a flow name. A function's **flows** are then:
- **result ← parameter `i`** iff they share a flow name: the result may carry `i`'s loans (this
  generalises v0's return sources, §13.5);
- **parameter `i` ← parameter `j`**, for `i` of type `borrow mut T` whose `T` shares a flow name
  with `j`: the callee may store `j`'s loans into what `i` refers to.

A v0 signature (no regions) has exactly its v0 return sources and no parameter flows.

### 25.7 Borrow checking (`lifetimes.md` §3)
With one loan set per value:
1. Writing a scoped value into a place of a local (a field, an element, a payload) adds its
   loans to that local.
2. Writing it through a reference adds its loans to the reference and to what the reference was
   borrowed from, followed back through reborrows, calls (by their result flows) and region
   allocation. If that reaches a `borrow mut` parameter `p` of the current function, the value's
   loans must all come from parameters that flow into `p` (§25.6): otherwise N0272.
3. `push`, `insert` and `append` write their argument through their receiver (rule 2).
4. At a call, an argument that flows into a `borrow mut` parameter adds its loans to what that
   argument refers to.
5. In generic code, values whose type mentions a type parameter carry loans like scoped values.
6. Destroying a value that is scoped and needs drop is a use of its loans: its `drop` may read
   them, so they must still be live (N0243 otherwise).

Diagnostics: N0270, N0272, N0273, N0274 (`diagnostics.md`).

## 26. Program arguments, files and sockets (C3)

```nova
fn main() -> i32 {
    let argument = Process.argument(1);
    if (argument == null) { Console.writeLine("usage: size FILE"); return 2; }
    let path = argument!;
    match (File.open(path)) {
        Ok(file) => {
            var buffer = [0u8; 4096];
            var total: u64 = 0;
            loop {
                match (file.read(mut buffer[..])) {
                    Ok(n) => { if (n == 0) { break; } total += n as u64; }
                    Err(e) => { Console.writeLine(e.message()); return 1; }
                }
            }
            Console.writeLine(total);
            return 0;
        }
        Err(e) => { Console.write(path); Console.write(": "); Console.writeLine(e.message()); return 1; }
    }
}
```

### 26.1 Program arguments
- `Process.argumentCount()`: `usize`, the number of command-line arguments including the program
  name (argument 0).
- `Process.argument(i)`: `StringView?`, argument `i` as a view of static text (no loans), or `null`
  if `i` is out of range or the argument's bytes are not valid UTF-8 (the `StringView` invariant,
  §22.2). Arguments are never copied.

### 26.2 `IoError`
A built-in enum, like `AllocError`, with the variants `NotFound`, `PermissionDenied`,
`AlreadyExists`, `IsDirectory`, `NotDirectory`, `InvalidInput`, `BrokenPipe`,
`ConnectionRefused`, `AddressInUse`, `OutOfMemory` and `Other(i32)` (the platform's error number).
`e.message()` is a `StringView` of static text describing it (the C library's wording for the
common cases: "No such file or directory", "Permission denied", …).

### 26.3 Files and the console
- `File` owns an open file. It is a **move type that needs drop**: destroying it closes the file
  (an error from closing is ignored). It is not scoped and not Copy.
- `File.open(path)`: `Result<File, IoError>` for reading; `File.create(path)`: for writing, created
  or truncated; `File.remove(path)`: `Result<void, IoError>`. `path` is a `StringView`; a path that
  contains a zero byte or is longer than 4095 bytes is `InvalidInput`.
- `f.read(mut buffer)` (`buffer: SliceMut<u8>`, `f` taken as `borrow mut this`): `Result<usize,
  IoError>`, the number of bytes read, `0` at the end of the file. Interrupted reads are retried.
- `f.write(bytes)` (`Slice<u8>`): `Result<void, IoError>`; it writes all the bytes or fails.
- `Console.read(mut buffer)`: reads standard input like `f.read`. `Console.writeBytes(bytes)`
  writes raw bytes to standard output, in order with `Console.write`.
- `Console.writeError(text)` and `Console.writeErrorLine(text)` (`text: StringView`) write to
  standard error; standard output is flushed first, so the two keep their relative order.

### 26.4 TCP sockets
- `TcpListener.bind(address, port)` (`address: StringView`, dotted IPv4 such as `"127.0.0.1"` or
  `"0.0.0.0"`; `port: u16`, `0` for any free port): `Result<TcpListener, IoError>`, listening.
  `listener.port()`: `Result<u16, IoError>`, the port it is bound to. `listener.accept()`:
  `Result<TcpStream, IoError>`.
- `TcpStream.connect(address, port)`: `Result<TcpStream, IoError>`. `s.read(mut buffer)` and
  `s.write(bytes)` as for files; `s.shutdownWrite()`: `Result<void, IoError>`, so the peer reads
  the end of the stream.
- Both are move types that need drop; destroying one closes the socket.

### 26.5 Processes
- `Process.run(program, arguments)` (`program: StringView`, found on `PATH`; `arguments: borrow
  Vector<StringView>`, passed after the program name): `Result<i32, IoError>`. It starts the
  program with the caller's standard input, output and error, waits for it, and returns its exit
  status, or 128 plus the signal number if a signal ended it. Standard output is flushed first, so
  output keeps its order. A program that cannot be started is an error (`NotFound`, …); an argument
  with a zero byte is `InvalidInput`; if copying the arguments for the C library fails, `OutOfMemory`.

### 26.6 Runtime (Profile A and hosted)
Implemented in `runtime/hosted/nova_rt_io.c` over the C library's `open`, `read`, `write`,
`close`, `unlink`, `socket`, `bind`, `listen`, `accept`, `connect`, `getsockname`, `shutdown`,
`waitpid`, and `posix_spawnp` (hosted) or `spawnvp` (CosmoOS) (L1, OD-15: public libc functions
only). CosmoOS's `struct sockaddr` (host-order port, 16 address bytes), its spawn API and its wait
status encoding differ from POSIX; the runtime selects them at compile time.
Generated code only calls functions declared in `nova/rt.h`, so it still compiles freestanding.

Diagnostics: none new.

## 27. Raw pointers and `unsafe` (M8a)

The contract that unsafe code must keep, and what the compiler and the interpreter check, is in
`unsafe.md`. This section gives the typing rules.

```nova
/// Safety: `p` points to `n` initialised `u32`s that nothing else writes while this runs.
unsafe fn sum(p: *u32, n: usize) -> u64 {
    var total: u64 = 0;
    for (i in 0..n) { total += p.add(i).read() as u64; }
    return total;
}

/// Safety: the pointer comes from a live array and stays inside it.
fn main() -> i32 {
    var a = [1u32, 2, 3, 4];
    let p = borrow mut a[0] as *mut u32;
    unsafe { p.add(3).write(10); }
    Console.writeLine(unsafe { sum(p, a.length) });   // 16
    return 0;
}
```

### 27.1 Types
- `*T` (read-only) and `*mut T` (writable) are **raw pointers** to `T`. `T` is any value type, or
  `void` for an untyped pointer (`*void`, C's `void *`). It may not be a reference, slice,
  `StringView` or other scoped type (§19.1, §25.3), or `never` (N0281). Since M8d it may be a type
  parameter: an instantiation with a scoped type argument is then N0274, as for a box of `T`.
- A raw pointer is one machine address: 8 bytes, aligned to 8 on LP64. It is **Copy**, needs no
  drop, holds no loans and is not scoped (U3). So it may appear anywhere a Copy value may: locals,
  parameters, results, fields, payloads and array elements. Constants of pointer type are not
  supported (N0216).
- `*mut T` coerces to `*T` wherever a `*T` is expected (like `SliceMut<T>` to `Slice<T>`).
- `==` and `!=` compare two pointers of the same type (after that coercion) by address. Ordering
  comparisons are not defined on pointers (N0207); compare `p as usize` instead.

### 27.2 Making pointers (safe)
Making and converting a pointer is safe; only using one is not (§27.3).
- `r as *T` for a reference `r: borrow T` or `borrow mut T`, and `r as *mut T` for `r: borrow mut T`.
  `borrow x as *T` parses as `(borrow x) as *T` (grammar §2.1). The borrow ends at once: the pointer
  holds no loan, so later uses of `x` are not checked against it.
- `s.ptr` for a slice `s`: `*T` for `Slice<T>`, `*mut T` for `SliceMut<T>`; for a `StringView`, `*u8`.
  It is the address of element 0 (of where it would be, for an empty slice).
- `p as *U` and `p as *mut U` for any raw pointer `p`, whatever its pointee and mutability.
- `p as usize` for any raw pointer, and `n as *T` / `n as *mut T` for `n: usize` (also `*void`).
  An integer literal converts too: `0 as *mut T` is the **null pointer** (there is no `null`
  pointer literal: `null` is the empty optional, §22.1).
- `p.isNull()`: `bool`.

### 27.3 Using pointers (unsafe)
These are allowed only in an unsafe context (§27.4; N0280 otherwise). `p` is a raw pointer
expression, evaluated once.
- `p.read()`: the `T` at `p`, copied out. `T` must be Copy (N0282).
- `p.write(v)`: stores `v` at `p` without reading or destroying what was there. `p` must be a
  `*mut T` and `T` Copy (N0282). It returns `void`.
- `p.add(n)` and `p.sub(n)` (`n: usize`) and `p.offset(n)` (`n: isize`): the pointer `n` elements
  (`n × size(T)` bytes) after (`sub`: before) `p`, with the type of `p`. The arithmetic wraps modulo
  2^64; it never panics.
- `Ptr.toBorrow(p)`: a `borrow T` to the `T` at `p`; `Ptr.toBorrowMut(p)`: a `borrow mut T`, for
  `p: *mut T` (N0282 for `*T`). The reference holds **no loans** (U3): the borrow checker cannot
  protect it, so the unsafe code that makes it must bound its use, typically by returning it from a
  function whose signature ties it to a parameter (§13.5).
- `Slice.fromRaw(p, n)`: a `Slice<T>` of the `n` elements from `p`; `SliceMut.fromRaw(p, n)`: a
  `SliceMut<T>`, for `p: *mut T`. Like `Ptr.toBorrow`, the slice holds no loans.
- Calling an `unsafe fn` (§27.4).
- None of these is defined for `*void`: cast it to a typed pointer first (N0282).

### 27.4 Unsafe contexts
- `unsafe { statements }` is a statement; `unsafe { e }` is an expression whose value is `e`'s
  (grammar §2). In statement position, `unsafe {` always starts the statement form.
- `unsafe fn` (a modifier after the visibility): calling the function is an unsafe operation, and its
  body is an unsafe context. `main` may not be `unsafe` (N0219). Methods may be `unsafe`; since C5,
  interface methods may be too (§30.1), and an `impl` must match (N0267).
- Everything lexically inside an unsafe block, an unsafe expression or the body of an `unsafe fn`
  is in an unsafe context, including deferred statements. A nested `unsafe` is allowed.
- **The Safety rule** (U2): an `unsafe fn`, and every function whose body contains an `unsafe`
  block or expression, must carry a doc comment (grammar §1.2) with a line that starts with
  `Safety:`. It states the contract: what callers must guarantee (for an `unsafe fn`) or why the
  unsafe operations inside are sound (for a safe function). Without one, N0283.
- Safe code is checked exactly as before. An unsafe context only *permits* the operations of §27.3;
  it never relaxes the type, move, borrow or region rules for anything else.

### 27.5 Lowering
- **NIR**: `p.read()` and `p.write(v)` are a copy from, and an assignment to, the place `(*t)` of a
  temporary `t` holding `p`: the same `Deref` projection as references, through a pointer.
  `Ptr.toBorrow`, `Ptr.toBorrowMut` and the pointer casts are `Cast`s; the arithmetic, `isNull`,
  `.ptr` and `fromRaw` are built-ins. The move and borrow checkers ignore places through raw
  pointers (no loans, no moves, no mutability check beyond §27.3).
- **C** (c-backend §2): see `unsafe.md` §4. Pointer arithmetic is done on `uintptr_t`, so generated
  C never computes an out-of-object pointer.
- **Placement** (M5, `memory-model.md` §5.1 rule 1c): a function that makes a raw pointer from a
  reference or calls `Ptr.toBorrow`/`toBorrowMut` or `fromRaw` keeps all its boxes on the heap
  ("address exposed").

Diagnostics: N0280 (an unsafe operation outside an unsafe context), N0281 (an invalid pointee
type), N0282 (a pointer operation the pointer or its pointee does not allow), N0283 (no `Safety:`
doc comment). Invalid pointer casts are N0208.

## 28. C interoperability (M8b)

```nova
extern "C" {
    fn strlen(s: *CChar) -> usize;
    fn puts(s: *CChar) -> i32;
}

@repr(C)
struct Point { x: i32; y: i32; }

/// The Manhattan length of a point, callable from C as `geo_length(struct Point)`.
@export("geo_length")
fn length(p: Point) -> i32 => abs32(p.x) + abs32(p.y);

fn abs32(v: i32) -> i32 { if (v < 0) { return -v; } return v; }

/// Safety: the literal ends in a zero byte, as `puts` and `strlen` need.
fn main() -> i32 {
    let text = "hello from C\0";
    let p = text.ptr as *CChar;
    unsafe { puts(p); }
    return truncate<i32>(unsafe { strlen(p) });
}
```

### 28.1 Attributes
An item may be preceded by attributes `@name` or `@name(arguments)` (grammar §2). The known ones:

| Attribute | On | Meaning |
|---|---|---|
| `@export`, `@export("symbol")` | a top-level, non-generic function | callable from C under `symbol` (default: the function's name), §28.4 |
| `@repr(C)` | a non-generic struct | a C-compatible struct, §28.5 |
| `@repr(u8)` … `@repr(i64)` | a non-generic enum without payloads | passed to and from C as that integer type, §28.5 |
| `@effects(…)` | a function or an `extern` function | context effects; accepted and ignored until C7 (`cosmoos/profiles.md` §1: ignored outside the kernel profile) |
| `@align(n)` | a non-generic struct | at least `n`-byte alignment (M8d, §29.4) |
| `@noncopy` | a struct | a move type even if every field is Copy (OD-7; M8d) |

An unknown attribute, a known one in the wrong place or with the wrong arguments, or the same
attribute twice is N0290.

### 28.2 `extern "C"` blocks
- `extern "C" { … }` at the top level declares functions defined in C: `fn name(parameters) ->
  Result;`, each optionally preceded by attributes and a visibility, with no body, receiver, type
  parameters or region parameters (N0290). `"C"` is the only ABI (N0290).
- Calling an extern function is an unsafe operation (U1, N0280): Nova cannot check what C does.
- Its C name is its Nova name. It must be a valid C identifier that is not a C keyword (or `bool`,
  `true`, `false`, `NULL`, `main`), not reserved by C (starting with `__`, or `_` and an uppercase
  letter), and not owned by the runtime and generated code (starting with `nova_`, `nv_`, `NV_`, or
  `nv` and an uppercase letter; c-backend §2.1): N0292.
  Two declarations, or a declaration and an `@export`, with one C name are N0201.
- Parameter and result types must be **FFI-safe** (§28.3), or N0291.

### 28.3 FFI-safe types and their C types
| Nova | C, in prototypes and in `nova_exports.h` |
|---|---|
| `i8`…`i64`, `u8`…`u64` | `int8_t`…`int64_t`, `uint8_t`…`uint64_t` |
| `usize`, `isize` | `size_t`, `ptrdiff_t` |
| `bool` | `bool` |
| `*T`, `*mut T` | `T const *`, `T *`, where `T` is an integer, `bool`, a `@repr(C)` struct, or (recursively) such a pointer |
| `*void`, `*mut void` | `const void *`, `void *` |
| `*CChar`, `*mut CChar` | `const char *`, `char *` |
| a `@repr(C)` struct `S` | `struct S` |
| a `@repr(uN)` / `@repr(iN)` enum | that integer type |
| `void` (results only) | `void` |

Nothing else crosses: references, slices, string views, boxes, arrays, optionals, results and other
structs and enums are N0291. (A slice crosses as its `.ptr` and `.length`; a pointer to any other
Nova value crosses as `*void`.) A `@repr` enum crosses by value only, not behind a pointer or as a
field of a `@repr(C)` struct: inside Nova it is not a plain integer.

`CChar` is C's `char`, whose signedness differs between targets. It may only be a pointee
(`*CChar`), never a value (N0281); `p as *u8` and `q as *CChar` convert. It exists so that
declarations of C library functions match the C library's own prototypes exactly.

### 28.4 Exports
- `@export` makes a top-level, non-generic, non-method function callable from C, with the C types of
  §28.3 (N0291 otherwise). `main` cannot be exported (N0290). The symbol must be a valid, unreserved
  C name (N0292) and unique (N0201).
- The function stays an ordinary Nova function too. Its body is checked like any other; an
  exported function is not unsafe to call from Nova.
- A C caller must call it with the declared types. A C value for a `@repr` enum parameter that is
  not one of its variants panics (`invalid enum value from C`) before the body runs.

### 28.5 C-compatible structs and enums
- `@repr(C)` on a struct fixes its layout to C's rules (fields in declaration order, each aligned,
  the size rounded up to the alignment; Nova's layout already is this, and M9 may reorder only
  structs without it). Its fields must be integers, `bool`, raw pointers as in §28.3, or `@repr(C)`
  structs, and their names valid, unreserved C identifiers (N0291, N0292). In C it is `struct S`
  with members named as in Nova, of the types Nova uses inside its own C (`usize` is `uint64_t`,
  pointers have no `const`), so that the generated code and C code that includes `nova_exports.h`
  declare the same struct type. It is otherwise an ordinary struct.
- `@repr(uN)` / `@repr(iN)` on an enum without payloads gives each variant its index (0, 1, …) as
  that integer type, which must hold every index (N0290). Across the boundary the enum is that
  integer; a value from C that is not a variant's index panics (`invalid enum value from C`).

### 28.6 Building with C
- `novac build` accepts C source files (`.c`) besides Nova files. They are compiled with the generated
  C and linked into the program, with the same C compiler and flags. With them, a program may have
  its `main` in C instead of in Nova; without a Nova `main`, the Nova runtime is not started, so
  allocation-failure injection (`NOVA_FAIL_ALLOC_AT`) and program arguments are not available to
  exported functions.
- When a program has `@export` functions or `@repr` types, novac also writes `nova_exports.h`: the
  `@repr` structs and enums, and a prototype for each export. C files include it as
  `#include "nova_exports.h"`.
- `run --interp` runs no C code. Calls to extern functions are interpreted only for a model of a few
  C library functions (`intermediate-representation.md` §5.4); any other is an interpreter fault.

Diagnostics: N0290 (attribute or extern declaration misuse), N0291 (a type that is not FFI-safe),
N0292 (a C name that is invalid, a C keyword, or reserved). Calls to extern functions outside an
unsafe context are N0280.

## 29. Systems capabilities (M8d)

```nova
@repr(C)
struct UartRegs { data: Volatile<u32>; status: Volatile<u32>; }

struct Uart {
    regs: MmioPtr<UartRegs>;

    /// Safety: `base` is the mapped address of a PL011-compatible UART that no other `Uart` aliases.
    public unsafe fn attach(base: usize) -> Uart => Uart { regs: MmioPtr<UartRegs>.at(base) };

    public fn putByte(borrow mut this, b: u8) {
        while ((this.regs.get().status.read() & 0x20) != 0) { Cpu.spinHint(); }
        this.regs.getMut().data.write(b as u32);
    }
}
```

### 29.1 Volatile accesses (unsafe)
`p.readVolatile()` and `p.writeVolatile(v)` are `read` and `write` (§27.3, same rules: unsafe, Copy
pointee, `*mut T` to write) as **volatile** accesses: each is performed exactly once, in program
order with every other volatile access and call, with the access width of `T`, never merged,
split, cached or removed. In C they are `*(volatile T *)p`. They are for memory-mapped registers
and memory shared with hardware; they do not order accesses between processors (that needs
atomics, C7).

### 29.2 `lib/core`: Nova sources bundled with the compiler
Some core types are written in Nova itself, over raw pointers and `unsafe` (ADR 0007 decision 1).
Their sources live in `lib/core/*.nova`, are embedded in `novac`, and are part of every package, in
both profiles. Their names are reserved like built-in names (N0201). Code from `lib/core` is
type-, move- and borrow-checked like user code; functions and types the package does not use are
not emitted, and `--dump-hir`/`--dump-nir` leave them out.

| Type | What it is |
|---|---|
| `Volatile<T: Copy>` | A cell whose value is read and written only by volatile accesses: `v.read()` (`borrow this`), `v.write(x)` (`borrow mut this`). One field of type `T`, so it has `T`'s layout and may be a `@repr(C)` field |
| `MmioPtr<T>` | The address of a memory-mapped `T` (typically a `@repr(C)` struct of `Volatile` registers). `unsafe MmioPtr<T>.at(address: usize)` makes one; `p.get()` is a `borrow T` and `p.getMut()` (`borrow mut this`) a `borrow mut T`, tied to `p` by their signatures (§13.5), so register access needs no `unsafe`; `p.address()` is the `usize` |
| `PhysAddr`, `VirtAddr` | Distinct address types (`cosmoos/kernel-memory-and-lifetimes.md`): `PhysAddr.of(bits: u64)`, `a.bits()`, `a.offset(n: u64)` (checked). No conversion between them, or to and from integers, except through `of` and `bits` |

The contract of `MmioPtr<T>.at` (its `Safety:` line): the address is mapped, aligned for `T`, and no
other `MmioPtr` or reference aliases the registers; the program then reaches them only through this
value.

### 29.3 CPU and target queries
- `Cpu.spinHint()`: a hint inside a busy-wait loop (`pause` on x86-64, `yield` on AArch64, nothing
  elsewhere). `void`, safe, allowed in both profiles.
- `Target.pointerBits()`: `u32`, the target's pointer width (64 on every target Nova supports).
  `Target.isX86_64()` and `Target.isAArch64()`: `bool`, the architecture the generated C is
  compiled for. They are decided by the C compiler, not by `novac` (the generated C is the same for
  every LP64 target), so they are not constants: use them in ordinary conditions. The interpreter
  answers for the machine it runs on.

### 29.4 `@align(n)`
`@align(n)` on a non-generic struct, with `n` a power of two from 1 to 4096, raises the struct's
alignment to at least `n` (its size is rounded up to it). In C, the first member is declared
`_Alignas(n)`. It may be combined with `@repr(C)`. N0290 for other `n` or places.

### 29.5 C function pointers
- `extern "C" fn(T1, …, Tn) -> R` is the type of a pointer to a C function (grammar §2). Its
  parameter and result types must be FFI-safe (§28.3) and not `@repr` enums (N0291). It is one
  address, Copy, holds no loans, and is FFI-safe itself, so C callbacks can be passed to and
  returned from C.
- **Values.** The name of an `extern "C"` function or of an `@export` function is a value of that
  type where such a type is expected (a typed `let`, an argument, a field, a return value) and the
  signatures are equal. Only those functions have a C address; naming another function there is
  N0291. `0 as extern "C" fn(…) -> R` is the null pointer, `f.isNull()` tests for it, and `f as usize`
  is its address.
- **Calls.** `f(args)`, with `f` a local, parameter or field of such a type, calls through the
  pointer. It is unsafe (N0280): the pointer may be null or point to anything.
- Nova functions that are not exported have no C address; Nova-level function values and closures
  are OD-9.

### 29.6 Entry points
A hosted program's entry point is `main`, called by the generated C `main` (§11). A freestanding
package has no `main` (`cosmoos/profiles.md` §4.1): its entry points are its `@export` functions,
which the environment calls (a kernel's `init`, a boot stub's `kmain`, a callback). An export's
symbol may start with one `_` and a lowercase letter (`_start`), the names platforms use for entry
symbols (§28.2 reserves only `__` and `_` with an uppercase letter); other reserved C names stay N0292.

Diagnostics: no new codes. Volatile accesses outside an unsafe context are N0280; a function pointer
type or value that cannot cross to C is N0291; a misused `@align` is N0290.

## 30. User-defined allocators (C5)

```nova
extern "C" {
    fn kmalloc(size: usize, flags: u32) -> *mut void;
    fn kfree(p: *mut void);
}

/// CosmoOS's kernel heap: kmalloc and kfree (integrations/cosmoos/c5/kernel.nova).
struct KernelHeap {}

impl Allocator for KernelHeap {
    /// Safety: kmalloc returns null or a block of `layout.size` bytes, 16-byte aligned.
    fn allocate(borrow this, layout: Layout) -> Result<*mut u8, AllocError> {
        if (layout.align > 16) { return Err(AllocError.OutOfMemory); }
        let p = unsafe { kmalloc(layout.size, 0) } as *mut u8;
        if (p.isNull()) { return Err(AllocError.OutOfMemory); }
        return Ok(p);
    }
    /// Safety: the caller passes a block this allocator returned (Box guarantees it).
    unsafe fn deallocate(borrow this, p: *mut u8, layout: Layout) { kfree(p as *mut void); }
}

fn make() -> Result<Box<u64, KernelHeap>, AllocError> => Box.createIn(KernelHeap {}, 42u64);
```

### 30.1 `Layout` and `Allocator` (`lib/core/alloc.nova`)
- `struct Layout { size: usize; align: usize; }`: a block's size in bytes and its alignment (a power of
  two).
- `interface Allocator`, with two methods:
  - `fn allocate(borrow this, layout: Layout) -> Result<*mut u8, AllocError>`: a block of at least
    `layout.size` bytes aligned to `layout.align`, or `Err` (never a null `Ok`);
  - `unsafe fn deallocate(borrow this, p: *mut u8, layout: Layout)`: returns a block. Its contract:
    `p` came from `allocate` of this allocator type with the same layout, and is returned once.
- Interface methods may be `unsafe` (lifting §27.4's earlier rule); an `impl` must match the
  interface's `unsafe` (N0267).

### 30.2 Allocator types
- A **user-defined allocator** is a non-generic struct **with no fields** that implements `Allocator`.
  It may be the `A` of `Box<T, A>`, and `Box.createIn(a, v)` takes a value of it. Having no fields, it
  has no state that a box would need to carry: a `Box<T, A>` stays one pointer, and dropping it
  needs no allocator value (the memory-model's free-standing allocators, §3.2 there). State that an
  allocator needs (counters, a C handle) lives on the C side or in global C state.
- The built-in allocators are still `SystemHeap` and `FailingAllocator`. `Vector`, `String`,
  `SlotMap`, `StableVector`, `Arena` and `Pool` take only those in v0 (N0263): their growth is in the C
  runtime, which cannot call Nova methods.
- A type named as a box's allocator that is not a struct without fields implementing `Allocator` is
  N0263.

### 30.3 Semantics
- `Box.createIn(a, v)` with a user allocator calls `allocate(Layout { size: size(T), align: align(T) })`
  once. On `Err`, it destroys `v` and returns `Err(AllocError.OutOfMemory)`; on `Ok(p)`, it moves `v`
  to `p`. Destroying the box destroys the contents, then calls `deallocate(p, the same layout)`
  once (H1). The compiler only calls `deallocate` with blocks `allocate` returned, so the contract
  holds for every safe program.
- A `size(T)` of 0 is never requested: such boxes still allocate one byte (as `SystemHeap` does).
- User allocators are not placement-transparent (M5): their boxes stay on the heap.
- They are allowed in the freestanding profile (`cosmoos/profiles.md` §4.1): `Box` with a user
  allocator needs no hosted runtime. `Box.create` (`SystemHeap`) stays hosted-only.

Diagnostics: N0263 (a box allocator that is not one), N0267 (an `impl` whose `unsafe` differs).
