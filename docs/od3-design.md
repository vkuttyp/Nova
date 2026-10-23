# OD-3 design: lifetime-parameterised aggregates

- Status: **accepted (ADR 0006) and implemented in M6x (2026-10-23)** (`milestone-6x.md`). The normative
  rules are `type-system.md` §25, which refines this design (e.g. regions are erased; N0271 is not used). Until then E3 holds: user structs and enums, and the elements of built-in
  collections, cannot be scoped (`ownership.md` E3/E4, N0246).
- Normative once accepted: this document, then `ownership.md` E1–E4 and `lifetimes.md` §7–§8 as
  amended by it.

## 1. Problem

v0 keeps borrow checking intraprocedural by never storing a reference (or any scoped value) inside
an aggregate. That covers parameters, slices, string views, local iteration, arena-scoped work and
guard-scoped lookups. It does not cover programs whose data *is* borrowed:

- a tokenizer or parser that keeps `StringView`s into its input (zero-copy parsing);
- `Vector<StringView>` of fields split from a line, or `SlotMap<borrow Config>`;
- structures in an arena that point to each other (`Node { next: borrow Node? }` with every node in
  one `Arena`), the main use of regions besides bulk release;
- kernel views: a struct holding a `borrow` of a lock-protected object for the guard's lifetime.

Today these are written with indices or `Span` offsets (`language-spec.md` Ex16), which is safe but
moves the checking to run time and pushes users toward `unsafe` once that exists (risk R-10).

## 2. Goals and non-goals

Goals:
1. **Soundness first.** No new run-time checks; the borrow checker proves every new form.
2. **v0 programs stay valid and mean the same.** No annotation becomes necessary where none was.
3. **Intraprocedural.** Signatures still summarise callees completely; no whole-program inference.
4. **Zero cost.** Regions are erased before NIR lowering's output reaches C; layouts do not change.
5. **Small surface.** One new declaration form; everything else reuses `borrow(a)` and generics.

Non-goals (rejected for v1 of OD-3; see §8): variance annotations, higher-ranked regions
(`for<'a>`), region subtyping beyond "outlives", self-referential structs, and regions on
`impl` blocks beyond those of the type.

## 3. Surface syntax

### 3.1 Declaring region parameters
A struct, enum or function may declare **region parameters** before its type parameters, with the
keyword `region`:

```nova
struct Token<region src> {
    kind: TokenKind;
    text: StringView<src>;          // a view into the input, not a copy
}

struct Lexer<region src> {
    input: StringView<src>;
    position: usize;
}

enum Json<region src> {
    Null,
    Text(StringView<src>),
    Items(Vector<Json<src>>),
}

struct Node<region a> {             // nodes in one arena, linked by shared references
    value: i32;
    next: (borrow(a) Node<a>)?;
}
```

### 3.2 Using region arguments
Region arguments are written first in a type's argument list and are names of regions in scope.
The built-in scoped types accept one optionally:

| v0 form (still valid) | With a region | Meaning |
|---|---|---|
| `borrow T`, `borrow mut T` | `borrow(r) T`, `borrow mut(r) T` | a reference whose loans are in region `r` |
| `StringView` | `StringView<r>` | a view whose loans are in `r` |
| `Slice<T>`, `SliceMut<T>` | `Slice<r, T>`, `SliceMut<r, T>` | |
| — | `Token<r>`, `Vector<Token<r>>`, `Json<r>?` | an aggregate holding loans in `r` |

The region name `static` is built in: the region of string literals and other static data, which
outlives everything (`ownership.md` E2). `StringView<static>` is a view of a literal.

`borrow(a)` already names *parameters* in return types (`lifetimes.md` §7). Under OD-3 the name
in parentheses may be a parameter or a region; a parameter name means "the region of that
parameter's loans", so every v0 signature keeps its meaning.

### 3.3 Region parameters of functions and elision
```nova
fn next<region src>(lexer: borrow mut Lexer<src>) -> Token<src>? { … }
fn firstField<region r>(line: StringView<r>) -> StringView<r> { … }
```
- A function may declare region parameters and mention them in parameter and result types.
- **Elision** (unchanged from v0, extended): in a signature, a scoped type with no region argument
  gets a fresh region per parameter; in the result, it means "the single scoped parameter" when
  there is exactly one, and otherwise must be written (N0245, as today). So `fn f(s: StringView)
  -> StringView` keeps working, and `fn next(lexer: borrow mut Lexer) -> Token?` is accepted with
  both elided to the lexer's region (one scoped parameter).
- In a function *body*, region arguments may always be omitted: types of locals are inferred.

## 4. Static semantics

### 4.1 Scoped-ness
A type is **scoped** (`type-system.md` §19.1) iff it is a reference, slice or view, a region
handle, or *mentions a region argument* — including a user aggregate with region parameters,
and a built-in collection or optional whose element is scoped (`Vector<StringView<r>>`). E3
becomes: an aggregate may contain a scoped field **only if** that field's regions are region
parameters of the aggregate (N0270 otherwise). E4 becomes: a collection may hold scoped elements;
the collection is then scoped itself.

Scoped values follow the existing placement rules: they cannot be returned except through the
signature (E1/E2), cannot be stored in `Box` or a static (N0246), and are `let`/`var` locals,
parameters, fields of scoped aggregates, or elements of scoped collections.

### 4.2 Fields, construction and access
- **Construction** `Token { kind: k, text: t }` gives the value the loans of every scoped field.
- **Reading** a scoped field (`tok.text`) gives a value with the loans of `tok`.
- **Borrowing** a field (`borrow tok.text`) is a loan of the place `tok.text` *and* carries the
  loans of `tok` (a reference to a view still depends on what the view borrows).
- **Assigning** a scoped field of a mutable value (`lexer.input = other`) or pushing a scoped
  element (`fields.push(view)`) adds the new value's loans to the target's root local (§5.2).

### 4.3 Generics
- A user generic may be instantiated with scoped type arguments (lifting today's N0246 on scoped
  type arguments): `Pair<StringView>` is scoped.
- In generic bodies, a type parameter is treated as **possibly scoped**: values of type `T` carry
  loans through moves, copies and calls exactly like a view would. This needs no new syntax and
  keeps "checked once, generically" (§20) sound for every instantiation.
- A bound `T: Free` (new built-in marker interface) asserts that `T` is not scoped. It is needed
  only where a body stores a `T` somewhere a scoped value cannot go (a `Box<T>`, a static).

### 4.4 `drop` and regions (drop check)
A type with region parameters may have a `drop` block that reads its scoped fields. Destroying such
a value is therefore a **use** of its loans. The checker treats the `Drop` of a value whose type
both mentions a region and needs drop as a use of every loan the value holds, so the referents must
still be live then (N0271: "`x` is destroyed here while it still refers to `y`, which ends
earlier"). Values whose types have no `drop` anywhere inside keep v0's rule: going out of scope
is not a use.

## 5. Borrow checking

### 5.1 One loan set per value
The v0 checker (`lifetimes.md` §3) gives each scoped local a set of loans, and computes it as the
union over every assignment and propagation edge. OD-3 keeps exactly this model: **a value of a
region-parameterised type has one loan set, the union over all its regions.** This is less precise
than one set per region parameter (a `Pair<r1, r2>` keeps both regions' loans alive while either
field is used) but it is sound, needs no change to liveness or conflict detection, and is what the
checker already does for scoped results and optionals (M4a). Per-region sets are a later
refinement (§8).

### 5.2 New propagation edges
1. Construction: from each scoped field operand to the aggregate (as for any aggregate rvalue today).
2. Field read and field borrow: from the root local to the result (as for reads through a reference).
3. **Write through a mutable path**: assigning a scoped value to a place `p.f…` (or pushing it into a
   collection through `borrow mut p`) adds an edge from the value to `root(p)`. If `p` is reached
   through a `borrow mut` *reference parameter* (not a local), the written value must instead carry
   only loans of the signature's regions that the parameter's pointee mentions (N0272); the caller
   applies the edge at the call site (rule 4).
4. **Calls**: for a call `f(a1…an)`, the result receives the loans of every argument whose declared
   type mentions a region that the result type mentions (this replaces and generalises the v0
   `borrow(a, b)` return sources). For every argument `borrow mut p` whose pointee type mentions a
   region `r`, `root(p)` receives the loans of every other argument mentioning `r` (the callee may
   have stored them).

### 5.3 Why this is sound (sketch)
A loan can only reach a value through the edges above, which over-approximate every data flow the
type rules allow (construction, field and element writes, calls by their signatures, moves and
copies). A loan is live while any local holding it is live (NLL liveness, unchanged), and every
access that conflicts with a live loan is rejected (unchanged). Region names add no permissions:
they only *restrict* which flows a callee may perform (rule 3) and let callers know which flows
happen (rule 4). Mutable references to scoped values are effectively invariant, because writes
through them add loans to the referent's whole set rather than relying on subtyping. Destruction
is covered by §4.4.

## 6. Run-time and C

Nothing changes. Regions are erased after checking; `Token<r>` is the same C struct as a
`Token` with a plain view field would be. No new runtime functions, no new checks.

## 7. Examples (expected verdicts)

```nova
fn fields<region r>(line: StringView<r>) -> Result<Vector<StringView<r>>, AllocError> {
    var out = Vector<StringView<r>>.create();
    var start: usize = 0;
    for (i in 0..line.length) {
        if (line[i] == 44u8) { out.push(line[start..i])?; start = i + 1; }
    }
    out.push(line[start..])?;
    return Ok(out);                       // ok: out's loans are line's (region r)
}

fn main() {
    var text = String.create();
    text.append("a,b,c")!;
    let parts = fields(text.view())!;     // parts holds a loan of text
    // text.append("d")!;                 // error[N0241]: text is borrowed by parts
    Console.writeLine(parts[1]);
}

fn leak() -> Token<static> {              // error[N0244]: a token of a local string
    let s = makeString();
    return Token { kind: TokenKind.Word, text: s.view() };
}
```

Arena-linked nodes: each `alloc` returns `borrow mut(a) Node<a>`; linking stores a shared reborrow
into `next`. The arena cannot be reset or dropped while any node reference is live (M4 rules,
unchanged), so the graph lives exactly as long as the arena's loan.

## 8. Alternatives considered

| Option | Why not (now) |
|---|---|
| Rust-style `'a` lifetimes with variance and subtyping | Largest annotation and checker surface; OD-3 explicitly prefers a smaller form. The design above can grow toward it (per-region sets, variance) without breaking programs |
| Whole-program region inference (no annotations) | Breaks separate checking and the "signatures summarise callees" property that keeps M2's checker tractable |
| Generational references at run time (Vale) | Run-time cost on every dereference; Nova already has `SlotMap` handles for the cases that need dynamic validity |
| Keep E3 forever; indices and `Span`s only | Safe, but zero-copy parsing and arena graphs stay awkward, which is risk R-10 |
| Per-region loan sets in v1 | More precise, but needs region-indexed loan sets throughout the checker; deferred until real programs show the imprecision matters |

## 9. Implementation plan (proposed; needs scheduling)

Proposed as its own increment after M6 (before C3, whose parsers and utilities benefit from it):

1. **Syntax and types:** `region` parameters on structs, enums and functions; region arguments;
   `StringView<r>`, `Slice<r, T>`, `borrow(r) T`; scoped-ness of region-mentioning types; E3/E4
   as amended (§4.1); new diagnostics N0270–N0273 (reserved here).
2. **Checker:** propagation edges of §5.2; call-site rules replacing return sources; type
   parameters as possibly scoped (§4.3) and `Free`.
3. **Drop check** (§4.4).
4. **Tests:** for each rule, ≥ 2 compile-fail and ≥ 1 near-miss compile-pass test (the M2
   standard); the mutation suite extended with region programs; property tests with
   `Vector<StringView>` and an arena graph; every v0 corpus program unchanged.
5. **Docs:** `ownership.md` E3/E4, `lifetimes.md` §7–§8, `type-system.md`, `diagnostics.md`.

Reserved diagnostics: N0270 (scoped field whose region is not a parameter of the aggregate), N0271
(destroyed while still referring to a shorter-lived value), N0272 (writing a value with foreign
loans through a `borrow mut` parameter), N0273 (unknown region name or wrong number of region
arguments).

## 10. Open questions for the owner

1. Schedule: implement as the next language increment (recommended) or after C3?
2. Syntax: `StringView<r>` and `borrow(r) T` as above, or a distinct marker such as `StringView in r`?
3. Is the precision of one loan set per value acceptable for v1 (§5.1)?
