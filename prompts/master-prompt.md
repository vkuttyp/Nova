# MASTER PROMPT — PROJECT NOVA
## Build a Memory-Safe, High-Performance Systems Programming Language with a C Backend

> **Status (2026-10-22):** Milestone 0 (the §23 first response) is delivered in `docs/`; start from
> `docs/README.md`. Later sessions continue from the "Next highest-priority task" in
> `docs/roadmap.md`, and must not redo M0. Accepted decisions live in `docs/decisions/`
> (ADR 0002 accepted the whole M0 decision set); the register is `docs/open-decisions.md`. Repository working rules: `AGENTS.md`.

### ROLE

Act as a principal compiler engineer, programming language designer, formal methods researcher, runtime engineer, and systems performance specialist.

You have deep expertise in:

- Compiler architecture, parsing, semantic analysis, intermediate representations, and code generation
- C11/C17 and C ABI compatibility
- Rust ownership, moves, borrowing, lifetime inference, and non-lexical lifetimes
- Goose's stack-oriented memory model and C backend
- Zig's allocator-oriented memory management
- Region-based memory management
- Escape analysis and allocation optimization
- LLVM concepts, SSA, and optimization passes
- Memory safety, data-race prevention, and concurrency
- Operating systems, embedded systems, and freestanding environments
- Compiler fuzzing, differential testing, and formal reasoning

Your assignment is to research, design, and incrementally implement a new systems programming language, temporarily named **Nova**.

This is a real language implementation project, not a syntax demonstration or toy interpreter.

Do not assume any proposed optimization is sound without documenting its prerequisites and testing its implementation.

---

# 1. PRIMARY VISION

Build a general-purpose systems programming language combining:

1. Syntax inspired by C# and TypeScript.
2. Memory safety through compile-time ownership and lifetime analysis.
3. Efficient stack allocation and compiler-managed temporary regions.
4. Safe heap allocation with pluggable allocators.
5. Deterministic resource management without requiring garbage collection.
6. Ahead-of-time compilation into portable C.
7. Native performance and compatibility with existing C libraries.
8. Predictable object representation and memory layout.
9. Optional low-level control for OS kernels, drivers, and embedded systems.
10. Clear compiler diagnostics and a productive developer experience.

The language must support both short-lived and long-lived data without forcing every dynamic object onto a stack or every object onto a heap.

### Fundamental principle

**Memory safety is a semantic guarantee. Allocation placement is an optimization and resource-management decision.**

Compiler optimizations must never weaken the language's memory-safety rules.

### Project priorities

In order:

1. Correctness and memory safety
2. Sound ownership and lifetime semantics
3. Deterministic resource management
4. Predictable performance
5. Efficient stack and heap utilization
6. C interoperability and portability
7. Language ergonomics
8. Advanced optimization

Never prioritize benchmark performance over correctness.

---

# 2. REFERENCE IMPLEMENTATIONS AND RESEARCH

Study the following before finalizing the architecture.

### Goose

https://github.com/aardappel/goose

Read, at minimum:

- README.md
- docs/goose_spec.md
- docs/implementation.md
- docs/reference.md
- Relevant runtime and code-generation sources
- Benchmark methodology and results

Investigate:

- Native call-stack usage
- Compiler-managed data stacks
- Stack assignment analysis
- Resizable value restrictions
- Reference-root and lifetime inference
- Bounds-check elimination
- Contiguous object layouts
- Construction directly into destination storage
- C code generation
- Runtime architecture
- Memory safety guarantees and limitations

Identify which techniques can be generalized to support heap allocation.

Do not copy Goose's implementation blindly. Treat it as a research reference.

### Rust

https://github.com/rust-lang/rust

Investigate:

- Ownership and moves
- Shared and exclusive borrowing
- Borrow checking
- Lifetime inference
- Non-lexical lifetimes
- Drop semantics
- Send/Sync-style concurrency restrictions
- Unsafe-code boundaries
- Owned heap allocations

### Zig

https://github.com/ziglang/zig

Investigate:

- Explicit allocator interfaces
- General-purpose allocators
- Arena allocators
- Fixed-buffer allocators
- Page allocators
- Allocation failure handling
- Freestanding compilation

### Cyclone

https://cyclone.thelanguage.org/

Investigate region-based allocation, lifetime checking, safe pointers, and C interoperability.

### Other research

Study relevant aspects of:

- Swift ownership and noncopyable types
- Nim's C backend
- Vale's generational references
- Escape analysis in JVM and native compilers
- Region inference research
- Static single assignment form
- Pointer provenance and C undefined behavior
- Memory sanitizers and compiler fuzzing

For every substantial technique, record:

- Source and citation
- Safety guarantee
- Implementation complexity
- Runtime overhead
- Compilation overhead
- Limitations
- Suitability for Nova

Distinguish demonstrated properties from benchmark claims and speculative designs.

---

# 3. LANGUAGE PHILOSOPHY

Nova must be:

- Statically typed
- Compiled ahead of time
- Memory-safe in its safe subset
- Ownership-aware
- Allocation-conscious
- Suitable for native and freestanding targets
- Friendly to C# and TypeScript developers
- Capable of low-level programming without a mandatory garbage collector

Avoid introducing a tracing garbage collector into the initial architecture.

Do not require a managed virtual machine.

Avoid implicit reference counting in ordinary objects.

Heap ownership must be explicit or statically inferable.

Support mutable programming, but impose the restrictions required for memory safety.

Avoid excessive lifetime annotations where inference is sound.

Where annotations are necessary, prefer clear and predictable syntax.

Do not permit memory-unsafe behavior merely to make the language easier to use.

---

# 4. PROPOSED LANGUAGE SYNTAX

Use familiar C#/TypeScript-style syntax:

- Braces for blocks
- Semicolon-terminated statements
- `let` for immutable bindings
- `var` for mutable bindings
- `struct` for value types
- `class` for reference-oriented types, if the ownership model supports them safely
- `interface` for behavioral contracts
- `enum` for algebraic data types
- `namespace` and `import`
- `public`, `private`, and `internal`
- `async` and `await` as future capabilities
- Generic types and functions
- Nullable/optional types using `?`
- Arrow-style expression bodies where appropriate

These are design preferences, not permission to implement unsupported features prematurely.

### Example: Basic program

```typescript
namespace Demo;

struct Point {
    public x: f64;
    public y: f64;

    public fn distance(other: Point) -> f64 {
        let dx = this.x - other.x;
        let dy = this.y - other.y;

        return Math.sqrt(dx * dx + dy * dy);
    }
}

fn main() -> i32 {
    let point = Point { x: 10.0, y: 20.0 };

    Console.writeLine(point.x);

    return 0;
}
```

### Example: Explicit heap allocation

Proposed illustrative syntax:

```typescript
struct User {
    id: u64;
    age: u32;
}

fn createUser(allocator: Allocator) -> Result<Box<User>, AllocError> {
    return Box.create(
        allocator,
        User { id: 100, age: 25 }
    );
}
```

The compiler must establish that `Box<User>` owns its allocation and that its destructor runs exactly once.

### Example: Scoped arena

```typescript
fn process() -> void {
    using arena = Arena.create();

    let values = arena.allocArray<i32>(1000)?;

    values[0] = 42;
}
```

The syntax and error propagation rules require formal definition before implementation.

Arena-backed references must never survive arena destruction.

A returned arena-backed value must either:

1. Carry valid ownership of its backing region;
2. Be copied or moved into suitably long-lived storage; or
3. Be rejected at compile time.

### Example: Explicit ownership transfer

```typescript
fn consume(buffer: own Buffer) -> void {
    // This function takes ownership.
}
```

### Example: Borrowing

```typescript
fn inspect(buffer: borrow Buffer) -> usize {
    return buffer.length;
}

fn modify(buffer: borrow mut Buffer) -> void {
    buffer.clear();
}
```

These examples are proposed surface syntax.

Before implementing them, define their precise operational and type-system semantics.

---

# 5. CORE MEMORY ARCHITECTURE

Design a hybrid memory system with the following storage categories.

## 5.1 Native stack

Use the native C call stack and registers for suitable values:

- Scalars
- Fixed-size structs
- Local temporaries
- Small aggregates
- Non-escaping values
- Compiler-generated temporaries

Support stack allocation only when size, alignment, lifetime, and target stack limits permit it.

Do not assume large objects should always occupy the native stack.

Avoid unbounded native-stack consumption.

## 5.2 Compiler-managed stack regions

Investigate Goose-inspired data stacks.

Potential capabilities:

- Bump-pointer allocation
- Scoped reset
- Stable-address allocation
- Temporary buffers
- Efficient nested object construction
- Contiguous layouts
- Bulk deallocation

Document the restrictions required to make growth and aliasing safe.

Do not assume a compiler-managed data stack has the same semantics as the native call stack.

Do not reuse storage while live safe references can still access it.

## 5.3 Heap allocation

Implement a safe owned heap abstraction.

Required capabilities:

- Allocation
- Deallocation
- Alignment
- Allocation failure reporting
- Owned object construction
- Deterministic destruction
- Ownership transfer
- Dynamic arrays
- Long-lived objects
- Recursive data structures

Support allocation through allocator interfaces.

The safe language must not expose unrestricted `malloc` or `free` operations.

## 5.4 Arena allocation

Support region-lifetime allocation.

Arena allocations should be individually cheap.

Arena destruction may release all associated allocations together.

Enforce that borrowed references cannot outlive the arena.

Handle destructors and nontrivial resource ownership explicitly. Bulk memory release alone is not sufficient for objects holding file handles, locks, or other resources.

## 5.5 Custom allocators

Design an allocator interface capable of supporting:

- System allocator
- General-purpose heap allocator
- Arena allocator
- Fixed-buffer allocator
- Pool/slab allocator
- Page allocator
- User-defined allocator

Document allocator lifetime, allocation ownership, and thread-safety requirements.

Allocation failure must be observable and testable.

Do not assume an allocator can safely be destroyed while outstanding allocations depend on it.

## 5.6 No-GC baseline

The initial runtime must not require tracing garbage collection.

Long-lived ownership must be explicit.

Cycles in owning structures must be addressed through carefully designed ownership and non-owning reference mechanisms.

Do not silently introduce leaks as a substitute for sound cycle handling.

---

# 6. OWNERSHIP AND MEMORY SAFETY

Design a sound ownership system.

At minimum, investigate and formally define:

### Ownership

- Every resource has a valid owner.
- Move operations transfer ownership.
- Moved-from values cannot be used.
- Destruction occurs exactly once.
- Partial initialization is tracked.
- Partial moves are supported only when sound.

### Borrowing

Support:

- Shared immutable borrows
- Exclusive mutable borrows
- Lifetime inference
- Reborrowing
- Borrowing through function arguments
- Borrowing of struct fields
- Safe slices
- Sound return-reference constraints

The compiler must reject conflicting mutable access unless a separately specified safe abstraction permits it.

### Lifetime analysis

Track whether references can outlive their storage.

Examples of prohibited behavior:

- Returning a reference to an expired local
- Accessing a freed heap allocation
- Using a moved value
- Retaining an invalidated vector-element reference
- Accessing a destroyed arena
- Using a dangling FFI pointer as though it were safe
- Accessing an incompatible object representation
- Creating data races through safe shared mutable references

### Required safety properties

The safe language should prevent:

1. Use-after-free
2. Double-free
3. Dangling-reference dereference
4. Out-of-bounds memory access
5. Use of uninitialized memory
6. Type-confused memory access
7. Invalid ownership transfer
8. Unsafe concurrent mutable aliasing

Null handling, integer overflow, resource exhaustion, stack overflow, and logical errors must each have separately defined behavior.

Memory safety does not imply immunity to every runtime failure.

### Unsafe code

Provide a restricted `unsafe` capability for:

- Raw pointer manipulation
- MMIO
- Native kernel interfaces
- C interoperability
- Certain lock-free structures
- Platform-specific operations

Unsafe code must not weaken guarantees of unrelated safe code.

Define explicit safety contracts for unsafe abstractions.

---

# 7. ESCAPE ANALYSIS AND ALLOCATION SELECTION

Implement an allocation analysis that can distinguish:

- Local non-escaping values
- Values escaping their lexical scope
- Values returned by ownership transfer
- Values borrowed by a callee
- Values retained in heap-owned structures
- Values captured by closures
- Values crossing asynchronous suspension
- Values crossing thread boundaries

Use conservative analysis initially.

### Important semantic rule

Do not automatically heap-promote an object merely because a borrow escapes.

A borrowed stack reference returning beyond its valid lifetime must remain a compile-time error.

Ownership transfer into a heap allocation is a different operation from extending the lifetime of an invalid reference.

### Allocation policy

Investigate the following strategy:

1. Prefer registers and native stack for eligible values.
2. Use compiler-managed regions for suitable scoped dynamic storage.
3. Use explicitly owned heap allocations for independently long-lived values.
4. Permit optional compiler-selected allocation placement only where language semantics explicitly allow it.
5. Use arena allocation when requested by the programmer or proven compatible with allocation policy.
6. Never make allocator choice depend on unsound lifetime assumptions.

### Allocation diagnostics

Design optional developer diagnostics such as:

```text
nova build --explain-allocations
nova build --allocation-report
nova build --check-lifetimes
```

These commands are proposed interfaces.

They should explain:

- Where values are allocated
- Why an allocation is needed
- Whether a heap allocation was optimized away
- Whether a copy or move occurs
- Which safety checks remain
- What prevented an optimization

---

# 8. COMPILER ARCHITECTURE

Design a production-oriented compiler architecture:

```text
Source
  ↓
Lexer
  ↓
Parser
  ↓
AST
  ↓
Name Resolution
  ↓
Type Checking
  ↓
Ownership and Borrow Analysis
  ↓
Typed High-Level IR
  ↓
Control-Flow and Dataflow Analysis
  ↓
Memory Placement and Escape Analysis
  ↓
Lowered IR
  ↓
Optimization
  ↓
C Code Generation
  ↓
C Compiler
  ↓
Native Binary
```

A multi-IR architecture is preferable to directly emitting C from the syntax tree.

### Required compiler properties

- Deterministic compilation
- Source-location preservation
- Actionable diagnostics
- Defined evaluation order
- Clearly specified integer behavior
- Explicit ownership operations in an IR
- Sound lifetime analysis before code generation
- Target-aware layout calculations
- Testable intermediate stages

Investigate incremental compilation, but do not require it for the first milestone.

---

# 9. C BACKEND

Implement a portable C backend as the primary native compilation route.

Target a documented subset of C11 or C17.

Generated C must support:

- Native functions
- Value types
- Owned heap objects
- Safe slices
- Generic specialization
- Structured control flow
- Deterministic resource cleanup
- Error propagation
- Runtime safety checks
- C function declarations
- Native linking

### C correctness requirements

Generated C must avoid undefined behavior, including:

- Signed integer overflow unless handled explicitly
- Invalid pointer arithmetic
- Strict-aliasing violations
- Invalid lifetime assumptions
- Unaligned memory access
- Invalid object representations
- Reading uninitialized objects
- Unsafe shifts
- Invalid free operations

Use well-defined helpers where direct C operations cannot preserve source-language semantics.

### Resource cleanup

Implement correct cleanup across:

- Normal returns
- Early returns
- Error propagation
- Loops
- Nested scopes
- Partially initialized objects

Do not depend on C++ destructors or exceptions.

### Backend testing

Compile generated C with:

- Clang
- GCC
- MSVC, where supported

CosmoOS profiles are **clang/lld only** and link no compiler-rt or libgcc. Generated C for those
profiles must therefore need no builtin-library calls (see `docs/c-backend.md`).

Enable strict warnings and sanitizers when available.

Preserve source mappings for debugging.

---

# 10. TYPE SYSTEM

Define the initial type system around:

- Signed and unsigned fixed-width integers
- Boolean
- Floating-point numbers
- Character and byte types
- Fixed-size arrays
- Slices
- Strings
- Structs
- Enums
- Optional values
- Result values
- Functions
- Owned and borrowed references

Introduce generic types and functions after the initial type checker is sound.

Determine:

- Copyable versus noncopyable types
- Struct layout
- Alignment
- Padding
- Field visibility
- Generic specialization
- Integer promotion rules
- Casting rules
- Nullability
- Error handling
- Destruction behavior

Do not infer hidden object copies for noncopyable owned resources.

Avoid implicit conversions that undermine type or memory safety.

---

# 11. COLLECTIONS

Design:

- `Array<T, N>` — fixed-size array
- `Slice<T>` — borrowed contiguous view
- `Vector<T>` — owned resizable array
- `String` — owned text
- `StringView` — borrowed text
- `Box<T>` — owned heap object

For each collection, define:

- Ownership
- Allocation behavior
- Copy and move behavior
- Iterator invalidation
- Reallocation semantics
- Bounds checking
- Lifetime restrictions
- Thread-safety properties

Pay particular attention to vector growth.

A reference into a vector must not silently become dangling after reallocation.

Provide one or more sound solutions:

- Borrow rules preventing growth during live element borrows
- Stable-storage collection types
- Handle/slot abstractions with validation
- Other proven-safe representations

Do not claim ordinary contiguous vectors have permanently stable element addresses.

---

# 12. ERRORS AND RESOURCE MANAGEMENT

Prefer explicit, typed error propagation.

Design types such as:

```typescript
Result<T, E>
Option<T>
```

Investigate ergonomic syntax for propagation, potentially using `?`.

Avoid mandatory exception-based control flow.

Define:

- Allocation failure
- Bounds violations
- Invalid casts
- Arithmetic failure
- Destruction failures, if supported
- Program panic behavior
- Freestanding failure handling

Resource management must work with files, sockets, locks, and memory.

Investigate `using` or `defer` syntax for scoped cleanup.

---

# 13. CONCURRENCY

Concurrency is a later milestone, but its safety model must be considered early.

Plan for:

- Native threads
- Thread-local storage
- Channels
- Ownership transfer between threads
- Immutable shared data
- Explicit synchronization
- Atomic operations
- Async functions

The initial safe subset should not permit unsynchronized mutable shared-state access.

Distinguish memory safety from data-race freedom and define both guarantees.

Do not implement advanced concurrency before ownership and lifetime checking are sufficiently mature.

---

# 14. SYSTEMS PROGRAMMING CAPABILITIES

The long-term language must support:

- Freestanding executable targets
- Kernel development
- Device drivers
- MMIO
- Interrupt handlers
- Explicit alignment
- Packed structures
- C-compatible structure layouts
- Function pointers
- Hardware intrinsics
- Conditional compilation
- Static linking
- Custom entry points
- Custom allocators
- No-standard-library builds

For freestanding compilation, do not assume:

- A hosted C runtime
- Operating-system allocation services
- Filesystems
- Threads
- Standard input/output

Define a minimal core library independent of the hosted runtime.

Separate hosted and freestanding capabilities cleanly.

---

# 15. C INTEROPERABILITY

Design a safe and explicit C FFI.

Support:

- Importing C-compatible declarations
- Exporting C-callable functions
- Calling native C libraries
- C-compatible structs and enums
- Raw pointers in unsafe contexts
- Ownership transfer contracts
- Buffer length contracts

Never treat arbitrary C pointers as automatically safe references.

Do not assume C libraries obey Nova's ownership model.

Provide safe wrappers around unsafe FFI operations.

---

# 16. OPTIMIZATION STRATEGY

After implementing correct semantics, investigate:

1. Constant folding
2. Dead-code elimination
3. Dead-store elimination
4. Copy propagation
5. Move elimination
6. Inlining
7. Escape analysis
8. Stack allocation of eligible objects
9. Arena-based temporary storage
10. Bounds-check elimination
11. Common-subexpression elimination
12. Loop optimization
13. Struct scalar replacement
14. Allocation coalescing
15. Devirtualization
16. Data-layout optimization

Each optimization requires:

- Safety preconditions
- Test cases
- Correctness checks
- Debugging support
- Performance measurements

Performance improvements must be measured against a correct baseline.

---

# 17. IMPLEMENTATION LANGUAGE

Evaluate implementing the Nova compiler in:

- C++20
- Rust
- C#/.NET

Compare:

- Memory usage
- Build times
- Runtime performance
- Compiler implementation complexity
- Library ecosystem
- Cross-platform support
- Bootstrap requirements
- Maintainability
- Debugging and testing tools

My preference is **C# with modern .NET**, provided the analysis demonstrates that it is technically suitable.

Use efficient compiler-oriented design:

- Compact token representations
- Arena-style AST storage where appropriate
- Stable symbol identifiers
- Interned identifiers
- Explicit intermediate representations
- Efficient diagnostics
- Minimal unnecessary allocation
- Reproducible builds

Do not select the compiler implementation language based solely on its similarity to Nova's surface syntax.

The generated Nova programs must remain independent of the .NET runtime.

Document the eventual compiler-bootstrap strategy, but do not attempt self-hosting prematurely.

---

# 18. TESTING AND VALIDATION

Build an extensive automated test system.

### Parser tests

- Valid syntax
- Invalid syntax
- Operator precedence
- Generic syntax
- Recovery from malformed input

### Type checker tests

- Valid assignments
- Invalid conversions
- Generic constraints
- Invalid calls
- Type inference

### Memory-safety tests

Include compile-fail tests for:

- Use-after-move
- Dangling stack references
- Dangling arena references
- Conflicting mutable borrows
- Double ownership
- Invalid field borrowing
- Invalid return references
- Unsafe vector reference invalidation
- Incompatible aliasing
- Thread ownership violations

### Runtime tests

- Stack values
- Heap allocation
- Arena allocation
- Allocation failure
- Nested ownership
- Recursive data structures
- Resource destruction
- Error propagation
- Bounds checking

### Differential and fuzz tests

Investigate:

- Parser fuzzing
- Type-checker fuzzing
- Generated-C compilation fuzzing
- Sanitizer-assisted testing
- Differential execution across C compilers
- Property-based collection tests
- Optimization equivalence tests

Use assertions only for properties that the test infrastructure can actually establish.

A sanitizer passing is supporting evidence, not a proof of language memory safety.

---

# 19. PERFORMANCE BENCHMARKS

Develop reproducible benchmarks covering:

- Small object creation
- Large object creation
- Stack versus heap allocation
- Arena allocation
- Vector growth
- String construction
- Recursive structures
- Linked lists
- Binary trees
- Hash maps
- Parsing
- Memory copying
- Numerical loops
- Data-oriented workloads

Compare Nova with:

- Optimized C
- C++
- Rust
- Zig
- Goose where the workload is expressible

Measure:

- Wall-clock execution time
- Throughput
- Peak resident memory
- Allocation count
- Allocated bytes
- Cache behavior where tooling permits
- Compilation time
- Output binary size

Use equivalent algorithms and data structures.

Distinguish algorithmic advantages from allocator, layout, and language-runtime advantages.

Never claim Nova is faster than another language without reproducible benchmark evidence.

---

# 20. DOCUMENTATION

Create and maintain:

```text
docs/
  vision.md
  architecture.md
  language-spec.md
  grammar.md
  type-system.md
  ownership.md
  lifetimes.md
  memory-model.md
  allocators.md
  escape-analysis.md
  intermediate-representation.md
  c-backend.md
  runtime.md
  ffi.md
  unsafe.md
  diagnostics.md
  testing.md
  benchmarks.md
  roadmap.md
  risks.md
  open-decisions.md
  milestone-<n>.md      # detailed spec of the milestone in progress
  decisions/            # ADRs
  research/             # primary-source studies (Goose, CosmoOS audits, comparative)
  cosmoos/              # CosmoOS integration designs (profiles, ABI, kernel memory, build)
```

`docs/README.md` is the index and status page. Keep it current.

Maintain architecture decision records for significant choices.

Every unresolved design issue must have:

- Description
- Candidate solutions
- Safety implications
- Performance implications
- Selected solution, if decided
- Rationale
- Follow-up work

Avoid letting implementation behavior become the undocumented language specification.

---

# 21. IMPLEMENTATION MILESTONES

## Milestone 0 — Research and architecture

Deliver:

- Comparative language research
- Formal memory-model proposal
- Ownership and borrowing proposal
- Allocation strategy
- Compiler architecture
- C backend design
- Risks and unresolved questions
- MVP scope
- Initial language specification
- Acceptance criteria for each milestone

Do not implement a large compiler during this phase.

## Milestone 1 — Minimal compiler

Implement:

- Lexer
- Parser
- AST
- Primitive types
- Local variables
- Functions
- Arithmetic
- Conditions
- Loops
- Basic semantic validation
- Minimal typed IR
- C generation
- Native executable creation

Acceptance criterion:

A Nova source program can compile into C and execute correctly.

## Milestone 2 — Ownership and borrowing

Implement:

- Move checking
- Borrow checking
- Lifetime validation
- Immutable and mutable references
- Use-after-move diagnostics
- Dangling-reference rejection
- Destruction semantics

Acceptance criterion:

A meaningful compile-fail suite demonstrates enforcement of the documented safety rules.

## Milestone 3 — Safe heap allocation

Implement:

- Allocator abstraction
- Owned heap objects
- Allocation failure handling
- Deterministic cleanup
- Ownership transfer
- Dynamic collections

Acceptance criterion:

Programs can safely create, return, transfer, mutate, and destroy heap-owned structures.

## Milestone 4 — Region allocation

Implement:

- Scoped regions
- Arena allocation
- Region ownership
- Lifetime enforcement
- Bulk cleanup
- Region-aware borrows

Acceptance criterion:

Arena-backed memory cannot be accessed after its lifetime ends through safe code.

## Milestone 5 — Escape analysis

Implement:

- Control-flow analysis
- Allocation classification
- Conservative escape analysis
- Allocation diagnostics
- Proven-safe placement optimizations

Acceptance criterion:

Eligible temporary allocations can use cheaper storage without changing program behavior.

## Milestone 6 — Generics and collections

Implement:

- Generic functions
- Generic structs
- Safe vectors
- Strings
- Slices
- Standard collection interfaces

## Milestone 7 — Production C backend

Implement:

- Cross-platform ABI handling
- Runtime library
- Robust cleanup lowering
- Source mapping
- Diagnostics
- Generated-C validation

## Milestone 8 — Systems and freestanding support

Implement:

- Core library
- Freestanding compilation
- C FFI
- Raw pointer unsafe operations
- Custom entry points
- Target-specific capabilities

## Milestone 9 — Performance engineering

Implement optimization passes only after their correctness foundations exist.

Measure each improvement.

## Milestone 10 — Developer tooling

Plan and implement, as appropriate:

- Formatter
- Package manager
- Language server
- Debugging integration
- Documentation generator
- Build system improvements

---

# 22. DEVELOPMENT RULES

Follow these mandatory rules:

1. Do not implement the entire language in one pass.
2. Do not write placeholder implementations that falsely claim to provide memory safety.
3. Do not make unproven performance claims.
4. Do not silently weaken lifetime checks.
5. Do not depend on undefined C behavior.
6. Do not bypass compile-time validation to satisfy tests.
7. Do not implement unsupported syntax without specifying its semantics.
8. Do not introduce a garbage collector without a documented architecture decision.
9. Do not confuse an arena allocation with a native stack allocation.
10. Do not confuse escape analysis with borrow checking.
11. Do not equate automatic heap promotion with sound lifetime extension.
12. Do not allow allocation optimizations to change observable ownership or destruction behavior.
13. Do not expose unsound safe APIs through the standard library.
14. Do not claim concurrency is data-race-free without enforcing the required rules.
15. Do not proceed to advanced optimization while fundamental safety invariants remain unresolved.

For every milestone:

- Implement actual working code.
- Add positive and negative tests.
- Run relevant validation.
- Document limitations.
- Update the roadmap.
- Record unresolved correctness risks.
- Explain what was completed.
- Recommend the next highest-priority task.

Never mark a milestone complete solely because a demonstration program runs.

---

# 23. REQUIRED FIRST RESPONSE

Start with research and architecture.

I want your first deliverable to contain:

### A. Language vision

Explain exactly what Nova is and is not.

Identify its intended users and workloads.

### B. Goose technical analysis

Explain Goose's:

- Stack model
- Safety model
- Compiler pipeline
- Allocation restrictions
- C backend
- Performance strengths
- Limitations for general-purpose systems development

### C. Hybrid memory model proposal

Compare at least three viable architectures:

1. Ownership-based stack and heap allocation
2. Region-based allocation with owned heap objects
3. A hybrid design combining ownership, regions, and conservative escape analysis

Evaluate safety, ergonomics, implementation complexity, and runtime costs.

Recommend one architecture with detailed reasoning.

### D. Language syntax

Provide at least 15 small example programs covering:

- Functions
- Structs
- Classes or equivalent owned reference types
- Heap allocation
- Borrowing
- Arrays
- Vectors
- Strings
- Error handling
- Generics
- Interfaces
- Recursive structures
- Custom allocators
- C interoperability
- Unsafe systems operations

Clearly distinguish accepted MVP syntax from future proposals.

### E. Formal safety design

Define the initial ownership and reference invariants.

Explain how the compiler will reject invalid programs.

Identify behaviors requiring runtime checks.

### F. Compiler design

Provide the major compiler passes and IR representations.

Explain why the chosen representations support ownership checking and C code generation.

### G. Implementation language decision

Compare C#, Rust, and C++ and recommend one.

### H. Repository structure

Propose a maintainable source tree with compiler, runtime, tests, benchmarks, examples, and documentation.

### I. Milestone roadmap

Provide a practical, incremental implementation plan with measurable acceptance criteria.

### J. Technical risk register

Identify the ten most serious risks, especially around lifetime soundness, unsafe FFI, heap allocation, generated-C semantics, and optimization correctness.

### K. Open architecture decisions

List the questions that require explicit design approval before implementation.

### L. First executable milestone

Specify the smallest end-to-end compiler capable of compiling a useful Nova program into portable C.

Conclude by identifying the recommended architecture, essential unresolved choices, and the exact first implementation increment.

**Do not launch directly into a large implementation. Complete the research, specification, and architecture review first.**

The goal is to build a genuinely new, memory-safe systems programming language with sound semantics, deterministic memory management, efficient allocation strategies, and a portable C backend—not merely a compiler that translates attractive syntax into unsafe C.