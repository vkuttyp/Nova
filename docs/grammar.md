# Nova Grammar — M1 subset

Status: normative for Milestone 1 (increment 1a implements the lexer and parser for exactly
this grammar). Later milestones extend it. Any syntax not listed here is rejected
by the parser, even if `language-spec.md` shows it for a later milestone.

Notation: EBNF. `'x'` is a literal token, `X?` optional, `X*` zero or more, `X+` one or more.

## 1. Lexical grammar

### 1.1 Source text
- UTF-8. A leading BOM is ignored. Line terminators: `\n`, `\r\n`, `\r`.
- Columns in diagnostics are 1-based and count UTF-16 code units. Lines are 1-based.
- Non-ASCII characters may appear only inside comments, string literals and char
  literals.

### 1.2 Whitespace and comments
- Whitespace: space, tab, line terminators, form feed.
- `// ...` to end of line. `/* ... */` block comments do **not** nest. An unterminated block
  comment is error N0003.
- Comments and whitespace are discarded (no trivia retained in M1).

### 1.3 Identifiers and keywords
```
Identifier = IdentStart IdentContinue*      (not a keyword)
IdentStart = 'A'..'Z' | 'a'..'z' | '_'
IdentContinue = IdentStart | '0'..'9'
```
A lone `_` is the token `Underscore` (reserved; no meaning in M1).

Keywords (all reserved, whether or not M1 uses them):
```
as borrow break const continue defer drop else enum extern false fn for if impl
import in interface internal let loop match move mut namespace null own private
public return Self static struct this true type unsafe using var where while
class async await yield macro
```
Primitive type names (`i32`, `u64`, `bool`, `void`, …) are **identifiers**,
resolved by name resolution, not keywords.

### 1.4 Integer literals
```
IntLiteral = (Decimal | '0x' HexDigits | '0b' BinDigits | '0o' OctDigits) IntSuffix?
IntSuffix  = 'i8'|'i16'|'i32'|'i64'|'isize'|'u8'|'u16'|'u32'|'u64'|'usize'
```
- `_` separators are allowed only *between* digits (`1_000`, `0xFF_FF`). A separator
  at the start or end of the digit sequence, or doubled, is error N0010.
- The value must fit in 64 bits (unsigned), otherwise N0004. Whether it fits the
  *type* is checked later (type checking).
- A digit invalid for the radix is N0008. A missing digit after a prefix is N0008. An unknown suffix is N0009.
- Decimal literals have no leading-zero restriction, and there is no octal-by-leading-zero:
  `010` is ten.

### 1.5 Float literals (lexed; rejected by the M1 type checker)
```
FloatLiteral = Decimal '.' Decimal Exponent? FloatSuffix? | Decimal Exponent FloatSuffix?
Exponent = ('e'|'E') ('+'|'-')? Decimal
FloatSuffix = 'f32' | 'f64'
```
`1.` and `.5` are not float literals. `1..2` lexes as `1` `..` `2`.

### 1.6 String and char literals
- `"..."` on one line. Escapes: `\n \r \t \\ \" \' \0 \u{H..H}` (1–6 hex digits,
  a Unicode scalar value). Other escapes are N0006. A newline or EOF before the closing quote is N0005.
- `'c'`: exactly one Unicode scalar value or one escape. Otherwise N0007.

### 1.7 Punctuation and operators
```
( ) { } [ ] , ; : . .. @ ? -> =>
+ - * / % ! & | ^ ~ && || << 
= == != < <= > >=
+= -= *= /= %= &= |= ^= <<=
```
The lexer **never** produces `>>` or `>>=`. It emits single `>` (and `>=`) tokens.
The parser forms the shift operator `>>` from two *adjacent* `>` tokens, and `>>=` from `>`
immediately followed by `>=`. That keeps nested generic arguments
(`A<B<C>>`) unambiguous in later milestones.

Any other character is N0001 (unexpected character).

## 2. Syntactic grammar (M1)

```
CompilationUnit = NamespaceDecl? Item* EOF
NamespaceDecl   = 'namespace' QualifiedName ';'
QualifiedName   = Identifier ('.' Identifier)*

Item       = Visibility? (FunctionDecl | ConstDecl)
Visibility = 'public' | 'internal' | 'private'

FunctionDecl = 'fn' Identifier '(' ParamList? ')' ('->' Type)? FunctionBody
ParamList    = Param (',' Param)* ','?
Param        = Identifier ':' Type
FunctionBody = Block | '=>' Expr ';'

ConstDecl = 'const' Identifier ':' Type '=' Expr ';'

Type = QualifiedName          (* M1: named types only *)

Block = '{' Statement* '}'
Statement = Block
          | LocalDecl
          | IfStmt
          | 'while' '(' Expr ')' Block
          | 'loop' Block
          | 'break' ';'
          | 'continue' ';'
          | 'return' Expr? ';'
          | Expr AssignOp Expr ';'        (* assignment: a statement, not an expression *)
          | Expr ';'
LocalDecl = ('let' | 'var') Identifier (':' Type)? ('=' Expr)? ';'
IfStmt    = 'if' '(' Expr ')' Block ('else' (IfStmt | Block))?
AssignOp  = '=' | '+=' | '-=' | '*=' | '/=' | '%=' | '&=' | '|=' | '^=' | '<<=' | '>>='
```

Notes:
- Braces are **mandatory** after `if`, `else`, `while` and `loop`, so there is no dangling-else ambiguity.
- Assignment is a statement, so `if (x = 1)` is a syntax error. The assignment target
  must be a name or member access (N0104 otherwise). Whether it is *assignable*
  (a `var`) is checked later.
- A `LocalDecl` without an initialiser must have a type annotation (enforced by the
  checker, not the parser).

### 2.1 Expressions and precedence

From lowest to highest binding. All binary levels are left-associative except
comparisons, which are **non-associative**: `a < b < c` is error N0103.

| Level | Operators | Notes |
|---|---|---|
| 1 | `\|\|` | short-circuit |
| 2 | `&&` | short-circuit |
| 3 | `== != < <= > >=` | non-associative |
| 4 | `\|` | |
| 5 | `^` | |
| 6 | `&` | |
| 7 | `<< >>` | |
| 8 | `+ -` | |
| 9 | `* / %` | |
| 10 | `as` | `Expr 'as' Type`, left-assoc (`x as u32 as u64`) |
| 11 | unary `- !` | prefix, right-assoc (`- -x`) |
| 12 | postfix: call `(...)`, member `.name` | |

The ordering puts bitwise operators **above** comparisons (as in Rust and Swift), so
`x & 1 == 0` parses as `(x & 1) == 0`. This avoids a well-known C/C# pitfall.

```
Primary = IntLiteral | FloatLiteral | StringLiteral | CharLiteral | 'true' | 'false'
        | Identifier GenericArgs? | '(' Expr ')'
GenericArgs = '<' Type (',' Type)* '>'      (* only when followed by '(' — see below *)
Postfix = Primary ( '(' ArgList? ')' | '.' Identifier )*
ArgList = Expr (',' Expr)* ','?
```

**Generic arguments in expressions** (needed for `truncate<i64>(x)`): after an
identifier, if the tokens form `'<' QualifiedName (',' QualifiedName)* '>' '('`,
they are parsed as generic arguments. Otherwise `<` is the less-than operator.
`f<T>(x)` is always a generic call. Write `(f < T) > (x)` to mean the comparison.

## 3. Error recovery (normative for diagnostics tests)

- Expected-token errors (N0100) are reported at the current token. When the missing
  token is `;`, they are reported at the end of the previous token instead. The missing token is
  *not* consumed.
- After an error, the parser suppresses further diagnostics until it re-synchronises:
  - in a block: at the next `;` (consumed), `}` (not consumed), or statement keyword
    (`let var if while loop return break continue`). Balanced `{ }` groups are skipped
    whole. An item keyword (`fn`, visibility) ends the block, which reports the missing `}`;
  - at the top level: at the next declaration keyword (`fn const public internal private
    namespace struct class enum interface impl let var static extern import using type`)
    outside any skipped `{ }` group, or at EOF. Each misplaced declaration is therefore reported
    once (N0101).
- Every parse loop consumes at least one token per iteration or terminates, so
  parsing always terminates.
