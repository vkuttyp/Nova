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
