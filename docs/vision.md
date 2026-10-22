# Nova — Vision

Status: Milestone 0. "Nova" is a working name.

## What Nova is

Nova is a **statically typed, ahead-of-time compiled, memory-safe systems
programming language** that:

- reads like C#/TypeScript: braces, `let`/`var`, `struct`, `interface`, `enum`,
  generics with `<T>`, `T?` optionals;
- guarantees memory safety in its safe subset through **compile-time
  ownership and borrow checking** (`ownership.md`), not through garbage collection
  or implicit reference counting;
- makes **allocation explicit and allocator-aware**: values live in registers
  or on the native stack by default, explicitly owned heap objects go through an
  allocator chosen by type, and scoped arenas give bulk lifetimes
  (`memory-model.md`);
- reports allocation failure as a **value**, never as a hidden abort, so it can
  be used where `kmalloc` can fail;
- compiles to **portable, UB-free C11**, so it runs wherever a C compiler runs and links
  directly with existing C code (`c-backend.md`);
- has a **freestanding core** with no libc, no runtime and no allocator assumed, so it
  can eventually be used inside the CosmoOS kernel (`cosmoos/profiles.md`).

**Memory safety is a semantic guarantee. Where a value lives is an optimisation and
resource-management decision.** No optimisation may change which programs are
accepted or when destructors run.

## What Nova is not

- Not a managed language. There is no VM, no tracing GC, no implicit refcounting, and no
  .NET dependency in generated programs (the compiler happens to be written in C#).
- Not "C with nicer syntax". Programs that cannot be shown safe are rejected unless
  written in `unsafe` with a stated contract.
- Not a Rust clone. v0 deliberately limits lifetime expressiveness (no
  lifetime-parameterised structs, `ownership.md` E3) in exchange for a smaller,
  intraprocedural checker and almost no annotations. Expressiveness grows
  only through approved extensions.
- Not Goose with a heap. Goose's data-stack ideas inform Nova's arenas and
  construction-in-place, but Nova's safety argument does not depend on a
  stack discipline (`research/goose.md`, `memory-model.md`).
- Not an OS rewrite project. CosmoOS adopts Nova component by component, alongside C,
  and only where Nova adds value.
- Not exception-based. Errors are `Result`/`Option`, and panics abort.

## Intended users and workloads

| User | Workload | Profile |
|---|---|---|
| CosmoOS application developers | Native userland tools: file and text utilities, network daemons, package tooling | `userland` |
| CosmoOS kernel and driver developers (later) | Isolated, testable kernel components: parsers of untrusted input, protocol state machines, drivers with clear boundaries | `kernel` |
| General systems developers coming from C#/TS | CLI tools, services, libraries exported to C | `hosted` (Linux/macOS) |
| Embedded developers | Freestanding firmware with a fixed-buffer or pool allocator | `freestanding` (generic, post-M8) |

The first users are CosmoOS userland programs (milestone C1→C3). They give
the shortest path to real programs on the real target, with no kernel risk.

## Success criteria (long-term)

1. A non-trivial CosmoOS utility written in Nova ships in the CosmoOS image, with
   no memory-safety bugs found by fuzzing that are attributable to safe Nova code.
2. A small kernel component exists in Nova alongside its C original, and passes
   the same tests under lockdep and sanitizers.
3. Generated C compiles warning-free under CosmoOS's own flags on x86-64 and AArch64.
4. Every performance claim in the docs is backed by a reproducible benchmark in
   `benchmarks/` (`benchmarks.md`). Until then, Nova makes **no** performance claims.
