# PROJECT NOVA — COSMOOS-NATIVE LANGUAGE ARCHITECTURE

> **Status (2026-10-22):** C0 is delivered. See `docs/cosmoos/` and the read-only audits in
> `docs/research/cosmoos-kernel-audit.md` and `docs/research/cosmoos-platform-audit.md`
> (CosmoOS main `4527d16d`, v1.0.9). Re-audit the interfaces you touch at the start of each
> C-milestone, because CosmoOS keeps moving.

## Mission

We are designing Nova, a new memory-safe systems programming language with C#/TypeScript-style syntax and a portable C backend.

**Nova's primary long-term target is CosmoOS in addition to multi-platform general native systems programming:**

~/dev/CosmoOS (https://github.com/vkuttyp/CosmoOS)

Its research reference for stack-oriented allocation and C generation is Goose:

https://github.com/aardappel/goose

The language must support:

1. Writing native CosmoOS user-space applications.
2. Writing safe components that interact with the CosmoOS kernel.
3. Eventually implementing selected kernel subsystems and drivers.
4. Compiling without a mandatory garbage collector or managed runtime.
5. Integrating with existing stack, slab, buddy, and heap allocation mechanisms.
6. Preserving CosmoOS's kernel architecture, security invariants, ABI, and existing C implementation.

Do not attempt to rewrite the operating system.

Treat the language as a new toolchain and a gradually integrated platform capability.

## 1. Mandatory CosmoOS research

Read the repository's current default branch.

Start with:

- `AGENTS.md`
- `README.md`
- The governing prompts under `prompts/`
- `docs/development.md`
- `docs/backlog.md`
- `docs/plan.md`
- `docs/roadmap-1.2.md`, the governing roadmap per CosmoOS `AGENTS.md` (1.0 and 1.1 are history)
- Any newer roadmap
- Kernel memory subsystem documentation
- Kernel object-lifetime and quiescence documentation
- Kernel module design and ABI
- Kernel synchronization and lockdep documentation
- Native libc and syscall documentation
- Build-toolchain configuration
- Relevant source code

Determine the current architecture rather than relying on older descriptions.

Respect the repository's ownership boundaries and development instructions.

Keep the language implementation separate until its integration interfaces are documented and approved.

CosmoOS's own `AGENTS.md` restricts work in that tree to the unit the owner has chosen. So
treat `~/dev/CosmoOS` as read-only from Nova work. Building its existing targets into its
`out/` directory is allowed. Prove each integration step out of tree first (stage S0 in
`docs/cosmoos/build-integration.md`), then propose an in-tree unit to the owner with the evidence.

## 2. Two compilation profiles

Design two initially separate profiles.

### Profile A: Nova Userland

Target native CosmoOS applications.

Requirements:

- Native ELF output through generated C.
- CosmoOS libc integration, through public libc functions only (libc invariant L1), unless the owner rules otherwise (OD-15).
- Always link CosmoOS `crt0.o`. Without its `.note.cosmo`, a binary silently runs under the Linux personality.
- Existing startup and entry-point conventions.
- Native system-call interfaces.
- Heap allocation through an approved user-space allocator.
- Stack allocation and local regions.
- Safe strings, arrays, slices, and collections.
- File, socket, and process APIs.
- Deterministic cleanup.
- Cross-compilation for x86-64 and AArch64.
- No dependency on .NET in generated applications.

The initial integration milestone must be a working Nova program that builds and executes inside CosmoOS.

### Profile B: Nova Kernel

Target selected kernel components.

Requirements:

- Freestanding compilation.
- No standard C library dependency.
- No garbage collector.
- No implicit allocation.
- Explicit allocation-failure handling.
- Explicit unsafe operations.
- Kernel ABI compatibility.
- Predictable stack usage.
- Existing kernel allocator integration.
- Interrupt-context restrictions.
- SMP and synchronization awareness.
- Correct kernel object lifetimes.
- Compatibility with the existing module system.

Do not implement the kernel profile merely by disabling the userland standard library.

Define its permitted language features and runtime dependencies precisely.

## 3. Kernel memory integration

Design a memory abstraction capable of using existing CosmoOS facilities.

Investigate:

- `kmalloc`
- `kzalloc`
- `krealloc`
- `kfree`
- Slab caches
- Physical page allocation
- Virtual memory mapping
- DMA-compatible memory
- Per-CPU storage

Propose safe wrappers that preserve existing allocation contracts.

Example conceptual syntax:

```typescript
fn createNode(
    allocator: Allocator
) -> Result<Box<Node>, AllocError> {
    return Box.create(allocator, Node {});
}
```

Provide distinct allocation capabilities where required.

For example:

```typescript
let node = KernelHeap.create<Node>()?;
let page = PhysicalPages.allocate(1)?;
let temporary = ScratchRegion.allocate(4096)?;
```

These examples do not establish final API names.

Do not equate ordinary heap memory with physical pages, DMA mappings, or virtual address ranges.

### Allocation constraints

The type system or an effect system must distinguish operations that:

- Can allocate
- Can block
- Can sleep
- May acquire locks
- Are permitted in interrupt context
- Require special CPU or scheduler state

Investigate whether such effects should be explicit, inferred, or checked through function annotations.

Prefer compile-time checks where practical.

Do not claim that compile-time effect checking alone proves all kernel concurrency properties.

## 4. Ownership, quiescence, and kernel objects

CosmoOS uses reference-counted kernel objects, capability-oriented handles, and epoch-based quiescence.

Nova must integrate with these mechanisms.

Research and specify:

- Owned references
- Borrowed references
- Shared kernel-object references
- Weak references or handles
- Deferred reclamation
- Epoch guards
- Module unload lifetimes
- Callback lifetimes
- Thread-local and per-CPU borrowing
- Cross-CPU ownership transfer

A normal owned heap object and a reference-counted kernel object do not necessarily have the same destruction semantics.

Do not implement generic automatic `free` behavior for kernel objects with specialized release protocols.

Design safe wrappers around existing object-lifetime interfaces.

### Epoch example

Investigate a safe API with semantics similar to:

```typescript
using guard = Quiescence.enter();

let object = table.lookup(guard, key);

if (object != null) {
    object.inspect();
}
```

The compiler must prevent an epoch-protected borrow from escaping the protection interval.

The implementation must also preserve CosmoOS's actual quiescence and scheduling rules.

## 5. Interrupt and synchronization safety

Investigate the kernel's existing rules for:

- Spinlocks
- Mutexes
- Interrupt masking
- Preemption
- Lock ordering
- Callback waits
- Sleepable contexts
- Per-CPU state

Design language-level abstractions that make these constraints visible.

Potential future APIs:

```typescript
@noalloc
@nosleep
fn interruptHandler(context: borrow mut IrqContext) -> void {
    // Explicitly restricted execution context.
}
```

These annotations are proposals.

Define their transitive semantics before implementing them.

Integrate with existing lockdep validation instead of claiming to replace it.

## 6. DMA and hardware programming

Support safe higher-level wrappers around unsafe hardware operations.

Investigate:

- MMIO
- DMA address spaces
- Physical and virtual addresses
- Cache coherency
- Alignment
- Memory barriers
- Volatile access
- Device register layouts
- Interrupt handlers

Keep these address categories distinct.

An integer, physical address, virtual pointer, DMA address, and capability handle must not be freely interchangeable.

Provide carefully reviewed unsafe boundaries.

## 7. Native code generation and ABI

Generated C must compile with the existing CosmoOS cross-toolchain. Today that is clang (LLVM ≥ 15) and ld.lld, with
`-ffreestanding -nostdinc`, no compiler-rt, and in the kernel no FP/SIMD, 16 KiB stacks and a 2 KiB frame limit. Take
flags from the CosmoOS build, never from a copy.

Support:

- x86-64
- AArch64
- ELF
- Explicit target layouts
- Freestanding compilation
- Static linking
- Kernel-compatible calling conventions
- C ABI exports and imports
- Correct alignment and structure layout

Do not assume the host architecture equals the target architecture.

Do not rely on host C# object layouts to determine native type layouts.

A compiler implemented in .NET must produce target-correct code independent of the host runtime.

Validate generated code with the same compiler flags and build constraints used by CosmoOS.

## 8. Kernel module integration

Investigate the existing signed, unloadable kernel module format.

Determine whether Nova can generate compatible object files through the C backend without changing CosmoOS's module loader.

Document requirements involving:

- Relocations
- Exported and imported symbols
- Section layout
- Initialization and teardown
- Module signing
- Lifetime of callbacks
- Outstanding references
- Deferred reclamation during unload

Do not assume that creating a valid object file is sufficient to create a safe unloadable module.

## 9. Build integration

Maintain clear separation between:

- Host compiler
- Target runtime
- Generated C
- Native object files
- Linked applications and modules

The initial compiler may run under .NET on the development host.

It must not introduce a .NET dependency into CosmoOS itself.

Design reproducible build integration.

Propose optional commands or Make targets for:

- Compiling Nova source
- Generating C
- Compiling user-space applications
- Running QEMU integration tests
- Checking ownership diagnostics
- Inspecting allocations

Preserve the existing build workflow.

## 10. Integration milestones

### C0 — Architecture and compatibility

- Audit CosmoOS interfaces.
- Document compiler and runtime requirements.
- Define the userland and kernel profiles.
- Identify mandatory ABI contracts.
- Establish safety invariants.
- Produce a compatibility and risk matrix.

### C1 — Native userland Hello World

Compile a simple Nova program to C.

Link it against CosmoOS's existing user-space facilities.

Boot CosmoOS in QEMU and execute it.

Validate x86-64 and AArch64.

Do this first out of tree (stage S0), leaving the CosmoOS source tree unmodified.

### C2 — Safe allocation

Implement:

- Stack locals
- Owned heap objects
- Allocator interfaces
- Borrow checking
- Allocation failure
- Deterministic cleanup

Run allocation and destruction tests inside CosmoOS.

### C3 — Collections and system calls

Implement safe:

- Strings
- Slices
- Vectors
- File operations
- Socket operations

Create a useful native CosmoOS utility written in Nova.

### C4 — Freestanding compilation

Build a minimal Nova object using no libc or hosted runtime.

Inspect its symbols, relocations, and runtime dependencies.

Prove the freestanding restrictions.

### C5 — Kernel memory integration

Implement a small, isolated adapter to the existing kernel allocation APIs.

Test normal allocation, exhaustion, ownership transfer, and cleanup.

Do not modify the kernel allocator merely to accommodate the language.

### C6 — Controlled kernel component

Choose a small, noncritical kernel component with clear boundaries and test coverage.

Implement a Nova version without removing the existing C implementation.

Compare correctness and performance.

### C7 — Kernel concurrency integration

Investigate and introduce:

- Context effects
- Kernel object borrowing
- Lock guards
- Epoch guards
- Deferred reclamation compatibility

Test against the kernel's real concurrency rules.

### C8 — Experimental kernel module

Compile a small Nova kernel module.

Load and unload it through CosmoOS's existing mechanism.

Verify object lifetime, teardown, symbol compatibility, and safety constraints.

### C9 — Gradual adoption

Evaluate which additional components benefit from migration.

Do not mandate a wholesale rewrite.

## 11. Validation requirements

Every integration milestone must preserve:

- Existing CosmoOS build targets
- Existing tests
- x86-64 support
- AArch64 support
- Kernel architecture invariants
- Native ABI stability
- Existing security boundaries

Use the repository's available host tests, sanitizer tests, and QEMU integration tests.

Add negative compiler tests for memory safety.

For kernel integration, add tests that exercise actual allocation failures and lifetime boundaries.

Clearly distinguish tests performed from tests merely proposed.

## 12. Required deliverables

Produce:

1. CosmoOS integration feasibility report.
2. Revised Nova memory architecture.
3. Kernel and userland profile specifications.
4. ABI compatibility report.
5. Allocator integration design.
6. Ownership and quiescence design.
7. Freestanding runtime design.
8. Compiler/backend integration design.
9. Security and correctness risk register.
10. Detailed milestone roadmap.
11. Acceptance-test matrix for both architectures.
12. Recommendation for the first working integration.

**Important:** Research and design first. Do not create commits, rewrite kernel code, or begin large-scale implementation until the integration plan is reviewed.

The ultimate goal is not to replace C everywhere.

It is to make Nova a genuinely useful, memory-safe language for developing CosmoOS applications and progressively implementing suitable operating-system components without compromising kernel correctness.