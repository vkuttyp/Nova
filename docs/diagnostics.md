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
