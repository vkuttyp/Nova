# Nova Language Specification — Initial Draft (v0)

Status: Milestone 0. Defines the **MVP surface** (milestones 1–4) and marks
*future proposals* explicitly. Semantics of ownership and borrowing are in
`ownership.md` (normative); storage and allocators in `memory-model.md`.
The formal grammar will be written in `grammar.md` during M1. Until then this
document is the reference.

Legend used in every example heading:
- **[MVP-M1]** … **[MVP-M6]**: accepted for that milestone.
- **[FUTURE]**: proposal only; not to be implemented without an approved ADR.

## 1. Lexical structure (summary)

- UTF-8 source. Identifiers `[A-Za-z_][A-Za-z0-9_]*` (ASCII in v0).
- Comments `//`, `/* */` (non-nesting), doc comments `///`.
- Integer literals: decimal, `0x`, `0b`, `0o`, `_` separators, optional suffix
  (`10u8`, `3i64`). An unsuffixed literal takes its type from context; with no
  context it defaults to `i32`. A literal that doesn't fit its type is a compile error.
- Float literals `1.0`, `1e9`, suffix `f32`/`f64` (default `f64`).
- String literals `"..."` (escapes `\n \t \\ \" \0 \u{...}`) have type
  `StringView` with static region. Char literals `'a'` have type `char`
  (Unicode scalar value, 32-bit).
- Keywords: `namespace import public private internal fn let var struct enum
  interface impl static const if else while for in loop break continue return
  match using defer own borrow mut move unsafe extern true false null this Self
  as type where drop`. Reserved: `class async await yield macro`.

## 2. Types (MVP)

| Type | Notes |
|---|---|
| `i8 i16 i32 i64 u8 u16 u32 u64` | Fixed-width two's complement; overflow panics (`ownership.md` §0) |
| `isize usize` | Pointer-sized, per *target* |
| `f32 f64` | IEEE 754; C `float`/`double` |
| `bool` | `true`/`false`; C `_Bool` |
| `char` | Unicode scalar value (u32 with validity invariant) |
| `void` | Unit return type |
| `Array<T, N>` / `[T; N]` | Fixed-size inline array (Copy if T is Copy) |
| `Slice<T>` | Scoped: borrowed `(ptr, len)` view; `SliceMut<T>` exclusive |
| `StringView` | Scoped: borrowed UTF-8 `(ptr, len)` |
| `String<A>` | Owned UTF-8, allocator `A` |
| `Box<T, A>` | Owned single heap object |
| `Vector<T, A>` | Owned growable array |
| `T?` | Optional. Niche-optimised for `Box` and references, otherwise `{bool, T}` |
| `Result<T, E>` | Tagged union |
| `borrow T`, `borrow mut T` | References (scoped). Never null |
| `*T`, `*mut T` | Raw pointers (unsafe to dereference) |
| `fn(A, B) -> R` | Plain function pointer (no closure environment) |

Default allocator type parameter: in the **userland** profile `A` defaults to
`SystemHeap`. In the **kernel** profile there is no default; it must be explicit
(`cosmoos/profiles.md`).

Conversions: no implicit numeric conversions except widening of an integer
**literal**. `as` performs only lossless numeric conversions (`u8 as u32`,
`i32 as i64`, `i32 as f64`). Lossy conversions use `truncate<T>()`,
`checkedCast<T>()` or `f.toIntSaturating<T>()`.

## 3. Declarations and statements (MVP)

- `let x = e;` immutable binding. `var x = e;` mutable binding. A type annotation
  is optional (`let x: u64 = 0;`). Bindings must be definitely initialised before
  use (`var x: i32;` followed by assignment on all paths is allowed).
- `fn name(p: T, ...) -> R { ... }`, `fn name(...) -> R => expr;`
- Parameter modes: *by value* (default: copy if Copy, otherwise move),
  `borrow T`, `borrow mut T`. `own T` is an explicit spelling of by-value for
  move types, accepted for readability.
- Call-site borrowing: an argument for a `borrow T` parameter is borrowed
  implicitly. An argument for a `borrow mut T` parameter must be written
  `mut x` (as with C# `ref`), so mutation is visible at the call site.
  Explicit borrow expressions elsewhere: `borrow x`, `borrow mut x`.
- `move x` explicitly moves (optional; moves of move types are implicit).
- `if`/`else`, `while`, `loop`, `for i in a..b` (half-open integer range),
  `for x in slice` (borrowing iteration), `break`, `continue`, `return`.
- `match` over enums and integers, exhaustive.
- `using x = e;`: pinned scoped binding (`ownership.md` R3).
- `defer stmt;`: runs `stmt` at scope exit on every path (in reverse order of
  `defer` statements). The deferred statement may not `return`/`break`/`?`.
- Error propagation `e?`: on `Result<T,E>` yields `T` or returns `Err(e)`
  converted by `From<E>` (M6; v0 requires identical `E`); on `T?` yields
  `T` or returns `null`. Postfix `!` unwraps or panics.
- Evaluation order: strictly left to right, receiver before arguments,
  arguments before call, and right-hand side of assignment **after** the
  left-hand side place is evaluated (as in C#). This is specified and the
  C backend must not delegate it to C's unspecified order (`c-backend.md` §4).

## 4. Example programs

### Ex1 — Functions and arithmetic [MVP-M1]

```nova
namespace Demo;

fn gcd(a: u64, b: u64) -> u64 {
    var x = a;
    var y = b;
    while (y != 0) {
        let t = x % y;
        x = y;
        y = t;
    }
    return x;
}

fn square(x: i64) -> i64 => x * x;      // overflow panics

fn main() -> i32 {
    Console.writeLine(gcd(1071, 462));  // 21
    return 0;
}
```

### Ex2 — Structs and methods (Copy value type) [MVP-M2]

```nova
struct Point {
    public x: f64;
    public y: f64;

    public fn distance(this, other: Point) -> f64 {
        let dx = this.x - other.x;
        let dy = this.y - other.y;
        return Math.sqrt(dx * dx + dy * dy);
    }
}

fn main() -> i32 {
    let p = Point { x: 3.0, y: 4.0 };
    let q = p;                            // Copy: p still usable
    Console.writeLine(p.distance(Point { x: 0.0, y: 0.0 }));
    return 0;
}
```

`this` alone = by value (copy for Copy types); `borrow this` and
`borrow mut this` are the reference receivers.

### Ex3 — Move-only resource with `drop` [MVP-M2]

```nova
struct Fd {
    raw: i32;

    drop {                      // runs exactly once, when the owner dies
        unsafe { Sys.close(this.raw); }
    }
}

fn consume(fd: own Fd) -> void { }        // fd dropped at end of consume

fn main() -> i32 {
    let fd = Fd { raw: 3 };
    consume(fd);
    // consume(fd);   // error[O3]: use of moved value `fd`
    return 0;
}
```

A struct with `drop` is never Copy. Fields are dropped after the `drop`
block, in declaration order.

### Ex4 — Equivalent of classes: owned heap reference type [MVP-M3]

Nova v0 has **no `class` keyword** (reserved). The C#/TS "object on the heap with
identity" idiom is written as a `struct` behind `Box`:

```nova
struct Account {
    id: u64;
    balance: i64;

    public fn deposit(borrow mut this, amount: i64) -> void {
        this.balance += amount;
    }
}

fn open(id: u64) -> Result<Box<Account>, AllocError> {
    return Box.create(Account { id: id, balance: 0 });   // A = SystemHeap (userland)
}

fn main() -> i32 {
    var acct = open(7)!;          // panics on allocation failure
    acct.deposit(100);            // auto-deref + implicit mut reborrow of *acct
    return 0;
}                                  // Box dropped: Account dropped, memory freed
```

*[FUTURE]* `class Account { ... }` as sugar for a non-Copy type that is always
`Box`-allocated (OD-4).

### Ex5 — Heap allocation with an explicit allocator [MVP-M3]

```nova
struct User { id: u64; age: u32; }

fn createUser<A: Allocator>(alloc: A) -> Result<Box<User, A>, AllocError> {
    return Box.createIn(alloc, User { id: 100, age: 25 });
}
```

`A` is an *allocator type* (`memory-model.md` §3). For free-standing allocators
(`SystemHeap`, `KernelHeap`) the value is a zero-sized token, so
`Box<User, KernelHeap>` is one pointer wide.

### Ex6 — Borrowing [MVP-M2]

```nova
fn inspect(buffer: borrow Buffer) -> usize {
    return buffer.length;
}

fn modify(buffer: borrow mut Buffer) -> void {
    buffer.clear();
}

fn main() -> i32 {
    var b = Buffer.create()!;
    let n = inspect(b);             // implicit shared borrow
    modify(mut b);                  // explicit mutable borrow at call site
    return n as i32;                // error: usize → i32 is not lossless; use checkedCast
}
```

### Ex7 — Returning a borrow [MVP-M2]

```nova
fn first(values: Slice<i32>) -> borrow i32 {     // elided: derived from `values`
    return borrow values[0];                     // bounds-checked
}

fn longer(a: StringView, b: StringView) -> borrow(a, b) StringView {
    return if (a.length >= b.length) a else b;   // region ⊆ both loans
}
```

### Ex8 — Fixed arrays and slices [MVP-M2]

```nova
fn sum(values: Slice<i64>) -> i64 {
    var total: i64 = 0;
    for v in values { total += v; }
    return total;
}

fn main() -> i32 {
    var data: [i64; 4] = [1, 2, 3, 4];
    data[2] = 10;
    Console.writeLine(sum(data[..]));      // whole-array slice
    Console.writeLine(sum(data[1..3]));    // checked sub-slice
    // data[4] = 0;                        // compile error: constant index out of range
    return 0;
}
```

### Ex9 — Vectors and invalidation [MVP-M6]

```nova
fn main() -> i32 {
    var v = Vector<i32>.create();         // no allocation until first push
    v.push(1)!;                            // push returns Result<void, AllocError>
    v.push(v.length as i32)!;              // two-phase borrow (B5)

    let r = borrow v[0];
    // v.push(3)!;                         // error[B1]: `v` is borrowed by `r`
    Console.writeLine(r);
    v.push(3)!;                            // fine: `r` is dead (NLL)
    return 0;
}
```

### Ex10 — Strings [MVP-M6]

```nova
fn greet(name: StringView) -> Result<String, AllocError> {
    var s = String.withCapacity(name.length + 7)?;
    s.append("Hello, ")?;
    s.append(name)?;
    return Ok(s);
}

fn main() -> i32 {
    let msg = greet("Nova")!;
    Console.writeLine(msg.view());         // StringView borrowed from msg
    return 0;
}
```

### Ex11 — Error handling [MVP-M3]

```nova
enum ParseError { Empty, BadDigit(u8), Overflow }

fn parseU32(text: StringView) -> Result<u32, ParseError> {
    if (text.length == 0) { return Err(ParseError.Empty); }
    var value: u32 = 0;
    for c in text.bytes() {
        if (c < '0' as u8 || c > '9' as u8) { return Err(ParseError.BadDigit(c)); }
        value = value.checkedMul(10)?.checkedAdd((c - '0' as u8) as u32)
                     .okOr(ParseError.Overflow)?;
    }
    return Ok(value);
}
```

(`checkedMul` returns `u32?`; `?` on an optional inside a function returning
`Result` is an error in v0. The example converts it with `okOr` and then uses `?`.)

### Ex12 — Generics with interface constraints [MVP-M6]

```nova
interface Comparable<T> {
    fn compare(borrow this, other: borrow T) -> i32;
}

fn max<T: Comparable<T>>(a: borrow T, b: borrow T) -> borrow(a, b) T {
    return if (a.compare(b) >= 0) a else b;
}
```

Generics are monomorphised. Interfaces are static constraints in v0. Dynamic
dispatch (`dyn Interface`) is *[FUTURE]* (OD-10).

### Ex13 — Interfaces and implementations [MVP-M6]

```nova
interface Shape {
    fn area(borrow this) -> f64;
}

struct Circle { r: f64; }
struct Rect { w: f64; h: f64; }

impl Shape for Circle { fn area(borrow this) -> f64 => 3.141592653589793 * this.r * this.r; }
impl Shape for Rect   { fn area(borrow this) -> f64 => this.w * this.h; }

fn total<S: Shape>(shapes: Slice<S>) -> f64 {
    var t = 0.0;
    for s in shapes { t += s.area(); }
    return t;
}
```

### Ex14 — Recursive structure (owned tree) [MVP-M3]

```nova
enum Tree {
    Leaf,
    Node(Box<TreeNode>),
}

struct TreeNode { value: i32; left: Tree; right: Tree; }

fn insert(t: own Tree, v: i32) -> Result<Tree, AllocError> {
    match (t) {
        Tree.Leaf => return Ok(Tree.Node(Box.create(TreeNode {
            value: v, left: Tree.Leaf, right: Tree.Leaf })?)),
        Tree.Node(var n) => {
            if (v < n.value) { n.left = insert(move n.left, v)?; }
            else             { n.right = insert(move n.right, v)?; }
            return Ok(Tree.Node(n));
        }
    }
}
```

Moving out of `n.left` (a field of a uniquely owned `Box` contents whose type
has no `drop`) is a partial move (O2) immediately repaired by assignment.
The checker requires the field to be re-initialised before `n` is used whole.
On the `?` error path `n` is partially moved: drop elaboration drops only `n.right`
and `n.value` (trivial), then frees the box. This is a key M3 test.

Destruction of a deep tree is recursive in v0. A stack-depth concern is recorded
in `risks.md` (R-9).

### Ex15 — Scoped arena [MVP-M4]

```nova
fn process(input: Slice<u8>) -> Result<u64, AllocError> {
    using arena = Arena.create(SystemHeap, chunkSize: 64 * 1024)?;

    let counts = arena.allocArray<u32>(256)?;     // scoped to `arena`, zero-filled
    for b in input { counts[b as usize] += 1; }

    var distinct: u64 = 0;
    for c in counts { if (c != 0) { distinct += 1; } }
    return Ok(distinct);                          // returns a u64, not arena memory
}                                                 // arena freed here; no live loans
```

`allocArray<T>` requires `T` without `drop` (R2).

### Ex16 — Custom allocator [MVP-M4]

```nova
// A fixed-buffer allocator over caller-provided storage.
fn parseHeaders(raw: Slice<u8>) -> Result<usize, AllocError> {
    var storage: [u8; 4096] = [0; 4096];
    using fba = FixedBufferAllocator.over(mut storage);  // scoped to `storage`
    let fields = fba.allocArray<Slice<u8>>(32)?;          // error[E3]: arena element type is scoped
    ...
}
```

The error is intentional: storing borrowed slices inside region memory needs
lifetime-parameterised containers (OD-3). The accepted v0 form stores
`(offset, length)` pairs:

```nova
struct Span { start: u32; len: u32; }
let fields = fba.allocArray<Span>(32)?;
```

A user-defined allocator implements the `Allocator` interface
(`memory-model.md` §3.2) inside `unsafe` code with a documented contract.

### Ex17 — C interoperability [M8b; `type-system.md` §28]

```nova
extern "C" {
    @effects(sleep)
    fn write(fd: i32, buf: *u8, count: usize) -> isize;
}

/// Safety: `bytes` is a valid borrowed slice; write() does not retain the pointer.
public fn writeAll(fd: i32, bytes: Slice<u8>) -> bool {
    var off: usize = 0;
    while (off < bytes.length) {
        let rest = bytes[off..];
        let n = unsafe { write(fd, rest.ptr, rest.length) };
        if (n < 0) { return false; }
        off += truncate<usize>(n); // n >= 0 here
    }
    return true;
}

/// Safety: the caller passes `len` readable bytes at `data` (the C side's contract).
@export("geo_checksum")             // C-callable symbol; `nova_` names belong to the runtime
public unsafe fn checksum(data: *u8, len: usize) -> u32 {
    let s = Slice.fromRaw(data, len);
    var h: u32 = 2166136261;
    for (b in s) { h = (h ^ b as u32).wrappingMul(16777619); }
    return h;
}
```

M8b changed this example from its M0 form: `@export` alone makes a function C-callable (no
`extern "C"` on definitions), and an export that reads through a pointer is `unsafe fn`, since a
safe Nova caller could pass any pointer. `IoError.fromErrno()`, `checkedCast` and `wrappingMul` are
not built in yet. `run-pass/ffi-libc.nova` is the running form of this example, with `puts`, `strlen`
and `memcpy` in place of `write`, which is not in every C library (MSVC calls it `_write`).

### Ex18 — Unsafe systems operations: MMIO [M8d; `type-system.md` §29]

```nova
@repr(C)
struct UartRegs {
    data: Volatile<u32>;
    status: Volatile<u32>;
}

struct Uart {
    regs: MmioPtr<UartRegs>;

    /// Safety: `base` must be the mapped address of a PL011-compatible UART that no other `Uart`
    /// value aliases.
    public unsafe fn attach(base: usize) -> Uart => Uart { regs: MmioPtr<UartRegs>.at(base) };

    public fn putByte(borrow mut this, b: u8) -> void {
        while ((this.regs.get().status.read() & 0x20) != 0) { Cpu.spinHint(); }
        this.regs.getMut().data.write(b as u32);
    }
}
```

`Volatile<T>`, `MmioPtr<T>`, `PhysAddr` and `VirtAddr` come from `lib/core`, Nova sources bundled
with the compiler (§29.2). The address types are distinct, with no implicit conversions
(`cosmoos/kernel-memory-and-lifetimes.md`); `DmaAddr` comes with the kernel profile. M8d changed
this example from its M0 form: `attach` takes the address as a `usize`, and register access goes
through `get()` / `getMut()`, whose results borrow the `MmioPtr`. It runs as
`run-pass/mmio.nova` and, freestanding, as `freestanding/uart.nova`.

### Ex19 — Kernel-context effects [FUTURE, C7]

```nova
@context(irq)            // implies @noalloc @nosleep, transitively checked
fn timerIrq(ctx: borrow mut IrqContext) -> void {
    ctx.ackTimer();
    // KernelHeap.create(...);   // error: allocation not permitted in irq context
}
```

## 5. Modules and visibility (MVP-M1 subset)

- One `namespace X.Y;` per file. `import X.Y;` brings public names into scope.
- `public` (exported from the package), `internal` (default: visible within the package),
  `private` (visible within the declaring type or file).
- A *package* is a directory with `nova.toml` (format to be specified in M1, OD-11).

## 6. Not in the language (v0) — deliberate omissions

Exceptions, implicit reference counting, tracing GC, inheritance, implicit
numeric conversions, null references, `async/await` (reserved; post-M9),
closures that capture (M6+, OD-9), operator overloading (M6+), macros.
