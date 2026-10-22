# ADR 0001 — Compiler implementation language

- Status: **Accepted** 2026-10-22 (OD-1; see ADR 0002).
- Date: 2026-10-22
- Scope: the language of the *host compiler* (`novac`) only. Generated Nova programs never depend on it.

## Context

The master prompt (§17) asks for a comparison of C++20, Rust and C#/.NET, with a stated preference for C# if it is technically suitable. The host environment has .NET SDK 10.0.401, clang and gcc available. The compiler:

- parses, type-checks and borrow-checks Nova, lowers through two IRs and emits C text;
- must be **deterministic** (bit-identical C output for identical input and flags), because CosmoOS builds should be reproducible;
- must compute **target** layouts (x86-64 and AArch64, LP64) independently of the host;
- must run on macOS and Linux dev hosts and in CosmoOS CI;
- will not be self-hosted for a long time (master prompt §17).

## Comparison

| Criterion | C# / .NET 10 | Rust | C++20 |
|---|---|---|---|
| Implementation productivity for a compiler | High: pattern matching, records, discriminated-union-style hierarchies, `Span<T>`, LINQ for analyses | High once fluent; borrow checker friction for graph-shaped IRs (usually solved with arenas plus indices) | Medium: a lot of boilerplate (visitors, ownership of AST nodes) and slow iteration |
| Memory usage | GC heap; manageable with struct-of-arrays token and IR storage and pooling. Peak memory probably 2–4× an equivalent Rust compiler (estimate, not measured) | Lowest | Low |
| Compiler throughput | Good with RyuJIT and tiered PGO; NativeAOT available for startup. Roslyn shows that C# can host an industrial compiler | Best | Best |
| Startup latency (matters for per-file `make` invocations) | JIT startup ~50–100 ms; **NativeAOT** removes most of it (estimate, to be measured in M1) | Native | Native |
| Build times of the compiler itself | Fast (seconds) | Slow incremental builds as the project grows | Slow |
| Correctness tooling | Nullable reference types, analyzers, xUnit, FsCheck/CsCheck property testing, SharpFuzz (libFuzzer/AFL bridge) | Excellent (cargo test, proptest, cargo-fuzz, miri) | Sanitizers, but the compiler itself can have memory bugs |
| Cross-platform | macOS, Linux and Windows first-class; NativeAOT single binaries per RID | Excellent | Excellent, but build-system pain |
| Bootstrap requirements | .NET SDK on the dev host (not in CosmoOS) | rustup | A C++ toolchain, which every host already has |
| Maintainability by the owner | **Owner prefers and knows C#** (many .NET projects in `~/dev`) | Unknown | Unknown |
| Risk of host leakage into target semantics | Real: `sizeof`, `Marshal`, `nint` and host endianness must **never** be used for target layout. Mitigated by a `TargetInfo` abstraction and a banned-API analyzer | Same risk, same mitigation | Same risk |
| Self-hosting path | Rewrite in Nova eventually. C# is not on the path, but neither is Rust or C++ | Same | Same |

## Decision

**C# on .NET 10**, structured for performance from the start:

1. Tokens as a `struct` array, plus a separate trivia array. Identifiers are interned into a `Symbol` table (`int` IDs).
2. AST stored as immutable class hierarchies at first (clarity matters more in M1). The later IRs (HIR, NIR) are index-based: functions own arrays of blocks, statements and locals, referenced by `int` IDs, as in rustc's `IndexVec`. Ownership dataflow uses `BitArray`/`ulong[]` bitsets.
3. All output goes through a deterministic emitter: no `Dictionary` enumeration order in output paths (use ordered collections), no hash-seeded naming, and a stable mangling scheme.
4. All target layout queries go through `TargetInfo` (pointer size, alignment, endianness, C type widths). A Roslyn analyzer bans `System.Runtime.InteropServices.Marshal.SizeOf`, `Unsafe.SizeOf` and `sizeof` in `Nova.Compiler.*` outside tests.
5. Ship as a NativeAOT single-file binary for CI and for CosmoOS's build. Development uses `dotnet run`.
6. Testing: xUnit plus a file-based test runner (`tests/compile-pass`, `tests/compile-fail` with expected diagnostics, `tests/run-pass` with expected stdout). Property and fuzz tests come later (CsCheck, SharpFuzz).

## Consequences

- C# is technically suitable. Compilers with the same structure (Roslyn, F#, Bflat/NativeAOT tooling) exist and perform well. The main costs are GC pauses and memory use on very large inputs, which do not matter at Nova's scale for several years.
- The CosmoOS build must have `novac` available on the host. **CosmoOS itself gains no .NET dependency**: only generated C files (and optionally a checked-in generated C snapshot) enter its build. See `docs/cosmoos/build-integration.md`.
- Revisit if (a) per-file startup measured in M1 exceeds 150 ms under NativeAOT, or (b) peak memory compiling a 100 kLOC Nova workspace exceeds 2 GB.
