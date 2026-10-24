# Diagnostics

Status: M1 increment 1a. Lists every diagnostic code the compiler can emit. Codes are stable:
a retired code is never reused. Source of truth in code: `DiagnosticCodes`
(`src/Nova.Compiler/Diagnostics/Diagnostic.cs`).

## Format

```text
<path>:<line>:<column>: error[N0100]: expected ';', found 'let'
  |
2 |     let x = 1
  |              ^
```

- `<path>` is the path as given on the command line (never made absolute), so output is reproducible.
- Line and column are 1-based. Columns count UTF-16 code units (grammar.md §1.1).
- Diagnostics are emitted in the order they are found. The lexer finishes before the parser starts,
  so all lexical errors of a file come before its syntax errors.
- Planned: `--diagnostics-format=json` (M1, later increment) and `novac --explain <code>` (M7).

## Recovery policy

The parser reports **one** diagnostic per broken statement or item, then re-synchronises
(grammar.md §3). The parse-fail corpus enforces this: each test lists exactly the expected
diagnostics, so any cascade fails the test.

## Codes

### Lexer (N0001–N0099)

| Code | Meaning | Example |
|---|---|---|
| N0001 | Unexpected character (including non-ASCII outside literals and comments) | `let a = 1 # 2;` |
| N0002 | *(unassigned)* | |
| N0003 | Unterminated block comment | `/* ...` at EOF |
| N0004 | Integer literal does not fit in 64 bits | `18446744073709551616` |
| N0005 | Unterminated string literal (newline or EOF before `"`) | `"abc` |
| N0006 | Invalid escape sequence, or `\u{...}` that is not a Unicode scalar value | `"\q"`, `"\u{D800}"` |
| N0007 | Invalid char literal: empty, more than one character, or unterminated | `''`, `'ab'` |
| N0008 | Invalid digit for the radix, or no digits after a radix prefix | `0b102`, `0x` |
| N0009 | Unknown literal suffix | `12abc`, `1f32` (float suffix needs a float literal) |
| N0010 | Misplaced digit separator `_` (must sit between two digits) | `1__0`, `0x_FF`, `1_.5` |

### Parser (N0100–N0199)

| Code | Meaning | Example |
|---|---|---|
| N0100 | Expected a specific token | missing `;`, `(` after `if`, `}` before `fn` |
| N0101 | Expected an item (`fn` or `const`) at the top level | `let x = 1;`, `struct S {}` (not in M1) |
| N0102 | Expected an expression | `let a = ;` |
| N0103 | Comparison operators chained | `a < b < c` |
| N0104 | Invalid assignment target | `1 = 2;`, `f() = 3;` |
| N0105 | Expected a type | `fn f(x: ) {}` |
| N0106 | Namespace declaration not first in the file | `fn a() {}` then `namespace X;` |
| N0107 | Expected a field or `fn` inside a struct body | `struct A { 42; }` |
| N0108 | Expected a `match` pattern (`_`, a literal, or `E.V(…)`) | `match (n) { + => { } }` |

### Names, types and flow (N0200–N0299)

Semantic checks run only when every file parsed without errors.

| Code | Meaning | Example |
|---|---|---|
| N0200 | Name not found (variable, function, or built-in member) | `missing`, `Console.print(1)` |
| N0201 | Item defined twice in the package | two `fn twice()` |
| N0202 | Parameter declared twice | `fn f(a: i32, a: i32)` |
| N0203 | Local redeclares a local or parameter in scope (no shadowing of locals) | `let x = 1; let x = 2;` |
| N0204 | Unknown type name | `fn f(a: int)` |
| N0205 | Type mismatch against the expected type | `let a: bool = 1;`, `if (n) {}` |
| N0206 | Integer literal out of range for its type | `256u8`, `let c: i8 = 128;` |
| N0207 | Operator not applicable to the operand types (includes mixed types, signed bitwise ops, negating unsigned values) | `a + b` with `u8`/`i32`, `s & s` with `i32` |
| N0208 | `as` conversion that can lose information | `a as u32` with `a: i32` |
| N0209 | Wrong number of arguments | `two(1)` |
| N0210 | Not callable, or a function or built-in used as a value | `local()`, `let f = two;` |
| N0211 | Assignment to an immutable binding (`let`, parameter, constant, function) | `param = 3;` |
| N0212 | Local read before it is definitely assigned | `var x: i32; return x;` |
| N0213 | End of a non-`void` function is reachable | `fn f() -> i32 { }` |
| N0214 | `break`/`continue` outside a loop | `break;` at function level |
| N0215 | `return` with or without a value contradicts the return type | `return 1;` in a `void` function |
| N0216 | Construct not supported in M1 (floats, `char`, string values, member access, qualified types, generic arguments other than `truncate<T>`) | `let a = 1.5;` |
| N0217 | Not a constant expression, or constant evaluation would panic | `const C: u8 = 200 + 100;` |
| N0218 | Constant depends on itself | `const A: i32 = A + 1;` |
| N0219 | Invalid `main` signature | `fn main(args: i32)` |
| N0220 | `let` without initializer | `let a: i32;` |
| N0221 | `var` without type annotation or initializer | `var c;` |
| N0222, N0223 | *(unassigned)* | |
| N0224 | Value of type `void` used as a value | `let d = nothing();` |
| N0225 | Expression statement that is not a call | `1 + 2;` |
| N0226 | `void` used other than as a return type | `var e: void;` |
| N0227 | Struct contains itself by value (infinite size); reported at the field that closes the cycle | `struct Node { next: Node; }` |
| N0228 | Duplicate member (field or function) in a struct | `struct D { a: i32; a: i64; }` |
| N0229 | Struct literal does not initialise every field | `Point { x: 1 }` |
| N0230 | Unknown field (in a literal or an access), or a function named where a field was expected | `p.z`, `p.sum` |
| N0231 | Field initialised twice in a literal | `Point { x: 1, x: 2, y: 3 }` |
| N0232 | `this` outside a method, or a `this` parameter outside a struct | `fn f() -> i32 => this;` |
| N0233 | Method called as a static function, or a static function called on a value | `Point.sum()`, `p.origin()` |
| N0234 | Field access, method call or literal on something that is not a struct | `n.x` with `n: i32` |

### References and borrows (N0240–N0249; `lifetimes.md`)

N0240–N0245 come from the NIR borrow checker. It runs only on programs that type-check, and its
errors carry notes ("borrowed here", "the borrow is used later here").

| Code | Meaning | Example |
|---|---|---|
| N0240 | Assignment to a place while it is borrowed (shared or mutable) | `let r = borrow x; x = 2; use(r)` |
| N0241 | Borrow conflicting with a live borrow | two live `borrow mut x`, or `borrow mut x` while `borrow x` is live |
| N0242 | Read of a place while it is mutably borrowed | `let m = borrow mut x; print(x); m = 1;` |
| N0243 | A place or temporary goes out of scope while borrowed | `let r = same(five()); use(r)` |
| N0244 | Returning a reference to a local or temporary (E1) | `return borrow x;` with `x` local |
| N0245 | Return-source errors (E2): missing or invalid `borrow(...)` list, or a result derived from an unlisted parameter | `fn f(a: borrow T, b: borrow T) -> borrow T` |
| N0246 | Reference in a disallowed position: struct field, constant, `var` local, reference to reference, source list outside a return type. Since M4a this covers every scoped type, scoped results included (`type-system.md` §19.1) | `struct S { r: borrow i64; }` |
| N0247 | `borrow` of something that is not a place | `borrow 5`, `take(mut five())` |
| N0248 | `borrow mut` (or a `borrow mut this` call) on an immutable place | `borrow mut param`, `shared.grow()` |
| N0249 | `mut` argument marker missing or misplaced | `take(x)` for `borrow mut`, `look(mut x)` for `borrow` |

### Moves and destruction (N0250–N0259; `type-system.md` §14)

N0212 and N0250–N0253 come from the NIR move checker (since M2c), which also runs only on
programs that type-check. A move while borrowed is reported by the borrow checker as N0240.

| Code | Meaning | Example |
|---|---|---|
| N0250 | Use of a value that was moved (on at least one path), including reading a partially moved struct as a whole | `consume(r); consume(r);` |
| N0251 | Move out of a place reached through a reference | `let a = r.a;` with `r: borrow Pair` and `Pair.a` a move type |
| N0252 | Move of a field out of a value whose type has a `drop` block | `consume(o.res)` with `struct Owner { res: Res; drop {} }` |
| N0253 | Move out of a `using` binding | `using u = make(); consume(u);` |
| N0254 | `return`, `break`, `continue` or `defer` inside a deferred statement | `defer return 1;` |
| N0256 | `match` that does not cover every value (add the missing variants or `_`) | `match (s) { Shape.Empty => { } }` |
| N0257 | Unreachable `match` arm: a duplicate pattern, or any arm after `_` | `1 => { } 1 => { }` |
| N0258 | *Retired in M3c:* a non-Copy payload bound from a value scrutinee now moves out, and the match consumes the scrutinee (`type-system.md` §18.4) | — |
| N0259 | Invalid array (`type-system.md` §16): a length that is not an integer literal from 1 to 2^48 − 1, an element type that is not Copy, or an array larger than 2^31 − 1 bytes | `[Res; 2]`, `[i32; 0]` |

Arrays and slices otherwise reuse codes: indexing or slicing a non-array, a non-`usize` index,
or a bad `for` sequence or range is N0205; writing an element of a `let` array or through a
`Slice` is N0211; a slice in a field, a `var` local or an array element is N0246;
`borrow mut a[..]` of an immutable array is N0248; slicing a non-place is N0247. Slices are
references, so the borrow checker reports their conflicts as N0240–N0245.

### Region parameters (N0270–N0279; `type-system.md` §25, M6x)

| Code | Meaning | Example |
|---|---|---|
| N0270 | A scoped field or payload in a struct or enum without region parameters | `struct S { v: StringView; }` |
| N0272 | Storing a value through a `borrow mut` parameter whose loans the signature does not let flow into it | `fn put(v: borrow mut Vector<StringView>, s: StringView) { v.push(s)!; }` |
| N0273 | An unknown region name, region arguments on a type without region parameters, or the wrong number of them | `StringView<nowhere>`, `Plain<r>` |
| N0274 | A generic used with a scoped type argument where it needs an unscoped one (it boxes `T`, makes an array of `T`, …) | `boxed<StringView>(s)` with `fn boxed<T>(v: T) -> Box<T>` |

N0271 is unassigned: destroying a value that holds loans while their referents are gone reports N0243.

### Unsafe and raw pointers (N0280–N0289; `type-system.md` §27, `unsafe.md`, M8a)

| Code | Meaning | Example |
|---|---|---|
| N0280 | An unsafe operation outside an unsafe context: a read, write or arithmetic through a raw pointer, `Ptr.toBorrow`/`toBorrowMut`, `Slice.fromRaw`/`SliceMut.fromRaw`, or a call to an `unsafe fn` (U1) | `let v = p.read();` in a safe function |
| N0281 | An invalid raw-pointer pointee: a reference, slice, `StringView` or other scoped type, or `never` (a type parameter is allowed since M8d; a scoped argument for it is N0274) | `*borrow i32`, `*Slice<u8>` |
| N0282 | A pointer operation that the pointer or its pointee does not allow: reading or writing a non-Copy type, writing through a `*T`, `toBorrowMut` or `SliceMut.fromRaw` from a `*T`, any access through `*void` | `p.write(1)` with `p: *i32`; `q.read()` with `q: *void` |
| N0283 | An `unsafe fn`, or a function containing `unsafe`, without a doc comment with a `Safety:` line (U2) | `unsafe fn f() {}` with no `/// Safety: …` above it |

### C interoperability and profiles (N0290–N0299; `type-system.md` §28, M8b; `cosmoos/profiles.md` §4, M8c)

| Code | Meaning | Example |
|---|---|---|
| N0290 | An unknown attribute, an attribute in the wrong place or with wrong arguments, a repeated attribute, an ABI other than `"C"`, or an extern declaration with a body, receiver or type parameters; `@export` on `main` | `@inline fn f() {}`, `@repr(C) enum E { A }`, `extern "Rust" { … }` |
| N0291 | A type that cannot cross the C boundary in an extern declaration, an export, or a `@repr(C)` struct's field | `fn f(s: Slice<u8>);` in `extern "C"`, `@export fn g() -> Box<i32>` |
| N0292 | A C name that is not a valid C identifier, is a C keyword (or `bool`, `true`, `false`, `NULL`, `main`), is reserved by C, or starts with `nova_`, `nv_`, `NV_` or `nv` and an uppercase letter | `@export("int")`, `fn nova_alloc();` in `extern "C"`, `@repr(C) struct S { default: i32; }` |
| N0293 | A hosted-only name (`Console`, `Process`, `File`, the heap allocators and every collection or box that needs them, …) or a `main` function in a package built with `--profile freestanding` (`cosmoos/profiles.md` §4.1) | `Console.writeLine(1);`, `let b = Box.create(5)!;` |
| N0294 | An imported C type (type-system.md §31), or a C-laid-out type (§33), used where Nova would need its size or would move it: as a local, parameter, result, field, element, optional or type argument, in a struct literal, or with `read`, `write`, `add`, `sub`, `offset` or `fromRaw` through a pointer; or a borrow of a pointer, `usize` or `isize` field of one | `fn f(i: cosmoboot_info) {}`, `p.add(1)` with `p: *cosmoboot_info` |
| N0295 | An operation whose effect (`sleep`, `alloc`) the context forbids: in an `@context(atomic)` or `@context(irq)` function, while an `@context(atomic)` value (a spin-lock guard) is live, or beyond a Nova function's `@effects` bound (type-system.md §32) | `mutex_lock(m)` while a `SpinGuard` is live |

Calling an extern function outside an unsafe context is N0280; two extern declarations or exports
with one C name are N0201; `CChar` as a value type is N0281.

Raw pointers reuse codes: an invalid pointer cast is N0208, ordering comparisons on pointers are
N0207, a constant of
pointer type is N0216, and an `unsafe main` is N0219. (An `unsafe` interface method was N0216 until C5.)

### Results and the heap (N0260–N0269; `type-system.md` §17–§18)

| Code | Meaning | Example |
|---|---|---|
| N0260 | `Ok(...)` / `Err(...)` (or, since M4c, `Pool.create(...)`; since M6c, `null`) without an expected type that fixes its type arguments, or where another type is expected | `let a = Ok(1);`, `let p = Pool.create(SystemHeap {}, 4u32);`, `let n = null;` |
| N0261 | `?` in a function that does not return a `Result` with the same error type; `?` on an optional in a function that does not return an optional (M6c) | `?` on `Result<i32, F>` in a function returning `Result<i32, E>` |
| N0262 | `?` or `!` on something that is not a `Result` or optional value (a reference to one included) | `n?` with `n: i32` |
| N0263 | An allocator argument that is not an allocator type (`SystemHeap`, `FailingAllocator`, or for `Box` since C5 a struct without fields implementing `Allocator`) | `Box<i32, Point>`, `Box.createIn(point, 1)` |
| N0265 | Type arguments that cannot be inferred: a generic call, struct literal or enum value whose arguments, fields or expected type do not determine every type parameter | `let d = Maybe.Nothing;`, `let e = make();` |
| N0266 | Generic instantiation nested more than 64 deep (polymorphic recursion) | `fn deep<T>(x: Maybe<T>) { deep(Maybe.Just(x)); }` |
| N0267 | An `impl` that does not match its interface (receiver, parameter or return type, a missing or extra method), a duplicate `impl`, an `impl` for a generic or scoped type, an interface method without a receiver, or a method call that two bounds both provide | `impl Shape for Square { fn area(this) -> i64 … }` |
| N0268 | A type argument that does not satisfy a bound of its type parameter (an interface it does not implement, or `Copy`) | `total(plain)` with `fn total<S: Shape>(…)` |
| N0264 | A type a region cannot hold (R2): one that needs drop, a scoped type, or a non-Copy fill value for `allocArray` | `arena.alloc(Res { id: 1 })`, `arena.alloc(someReference)` |

Generic code (M6a) reuses codes:
- an operator on a type parameter is N0207, a field or method on one is N0234, and a literal for
  one is N0205;
- a generic type without its arguments, or with the wrong number of them, is N0204;
- a duplicate or shadowing type parameter is N0201;
- a method with type parameters of its own is N0216;
- a generic `main` is N0219;
- naming something that is not an interface as a bound or in an `impl` is N0204;
- a method on a type parameter that no bound declares is N0234;
- an `impl` method clashing with a type's own method is N0228.

Region values reuse codes:
- assigning a `using` binding as a whole is N0211;
- nesting a frame that is not a `using` binding is N0248;
- a method call on a frame that is not a local is N0247;
- a storage that is not `[u8; N]` or `SliceMut<u8>` is N0205;
- returning a frame or fixed-buffer allocator without sources is N0245.

`?` inside a deferred statement is N0254, and `Ok`/`Err` with the wrong number of values is
N0209. The names `Result`, `Ok`, `Err`, `AllocError`, `Box`, `SystemHeap` and `FailingAllocator`
cannot be declared (N0201). Moving out of a box's contents is N0251 until M3c.

### Build (N0300–N0399)

| Code | Meaning | Example |
|---|---|---|
| N0300 | `novac build --emit exe` without a `main` function | a library-only file |
