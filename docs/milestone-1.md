# Milestone 1 — Smallest End-to-End Compiler (specification)

Status: M0 proposal (master prompt §23.L). Starts after OD-1/2/5/6/11 are approved.

## 1. Language subset ("Nova-M1")

- **Types:** all fixed-width integers (`i8`–`i64`, `u8`–`u64`, `isize`, `usize`), `bool`, `void`.
  No floats in M1 (keeps the kernel subset in reach and avoids float formatting). The full
  static semantics are in `type-system.md`.
- **Declarations:** one file or a few files in one package. `namespace`, top-level
  `fn` (including `public`/`internal`), `let`, `var`, optional type annotations,
  `const` integer constants.
- **Statements:** blocks, `if`/`else`, `while`, `loop`, `break`, `continue`, `return`,
  expression statements, assignment and compound assignment (`+= -= *= /= %=`).
- **Expressions:** literals, names, calls (including recursion), unary `- !`, binary
  `+ - * / % == != < <= > >= && ||` (short-circuit), `& | ^ << >>` on
  unsigned types, lossless `as`, `truncate<T>(x)`, parentheses.
- **Built-ins:** `Console.write(x)` / `Console.writeLine(x)` for integer, `bool` and
  **string literals only**. `Process.exit(code: i32)`.
- **Semantics:** overflow, division and shift checks panic (OD-6). Panic = message +
  abort (OD-5). Definite initialisation of `var` (the M2 init analysis restricted to
  scalars). Strict left-to-right evaluation. `main() -> i32` or `main() -> void`.
- **Excluded:** structs, arrays, references, heap, strings as values, generics,
  imports across packages.

## 2. Pipeline delivered

Lexer → Parser (with recovery) → AST → name resolution → type check → HIR → NIR
(CFG, with `CheckedBinOp` and `Panic` terminators) → LIR → C emitter
(labelled blocks) → host C compiler → executable. The full IR stack exists from M1,
so M2 adds analyses, not a new pipeline.

Runtime: `runtime/include/nova/rt.h` (types, checked arithmetic, `nova_panic`
declaration), `runtime/hosted/rt_hosted.c` (stdio + abort),
`runtime/cosmoos/user/rt_cosmoos_user.c` (`write(1, …)`, `abort`).

## 3. Acceptance criteria

1. **Corpus:** ≥ 150 tests in total: ≥ 40 parse-pass/fail (including the full precedence
   table and recovery cases), ≥ 40 check-fail (types, names, definite init,
   lossy `as`, literal out of range), ≥ 50 run-pass (including `gcd`, recursive `fib`,
   Collatz, integer `isqrt`, an FNV-1a hash of constant input, nested loops with
   `break`/`continue`), ≥ 15 run-fail (each overflow, division, shift and `MIN/-1` case with the
   expected exit status and message).
2. **C quality:** all generated C compiles with
   `-std=c11 -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wsign-conversion
   -Wmissing-prototypes -Wstrict-prototypes -Wvla -Wcast-qual -Wundef -Werror`
   on clang and gcc.
3. **Differential:** every run-pass test gives identical output under {clang, gcc}
   × {-O0, -O2} × {no sanitizer, ASan+UBSan} on the macOS (arm64) dev host. Linux x86-64 runs in CI.
4. **Cross-compile:** the corpus compiles for `x86_64-unknown-none-elf` and
   `aarch64-unknown-none-elf` with CosmoOS `USER_CFLAGS` (compile only; linking is C1).
5. **Determinism:** compiling the corpus twice gives byte-identical C.
   Output is independent of the host path.
6. **Diagnostics:** every check-fail test asserts the error code and primary span.
7. **Docs:** `grammar.md` (complete for Nova-M1), `intermediate-representation.md`
   (HIR/NIR/LIR as implemented), `diagnostics.md`, `testing.md` updated. Limitations listed.

C1 then reuses the M1 compiler unchanged: `hello.nova` prints `NOVATEST: PASS`
inside CosmoOS on both arches (`roadmap.md`).

## 4. The exact first implementation increment (1a) — **done 2026-10-22**

Delivered: `Nova.slnx` (the .NET 10 solution format) with `Nova.Compiler`, `Nova.Cli` (`novac`)
and `Nova.Compiler.Tests`; `docs/grammar.md`; `docs/diagnostics.md`; 151 passing tests
(see `testing.md` §4). Deviation from the plan: the corpus runner lives inside the xUnit
project for now. A separate `Nova.TestRunner` is deferred to 1c, when tests need to invoke C
compilers. Trivia (comments) is not retained yet; it is needed only for the M10 formatter.

Original plan:

1. `Nova.sln` with `Nova.Compiler`, `Nova.Cli`, `Nova.Compiler.Tests`,
   `Nova.TestRunner`. `Directory.Build.props`: nullable enabled, warnings as errors,
   `<Deterministic>true</Deterministic>`. `global.json` pinned to SDK 10.0.401.
2. `SourceText`, `Span`, `Diagnostic` and a text renderer.
3. Lexer for the full M1 token set: struct tokens, interned identifiers,
   literal validation, and keyword table with reserved words.
4. Parser: recursive descent for declarations and statements, Pratt parsing for expressions,
   recovery at `;`/`}`/declaration keywords. AST plus a `--dump-ast` printer.
5. Corpus runner for `parse-pass`/`parse-fail` with `// error: N0xxx` annotations.
   The first ~40 parser tests.

Exit criterion for 1a: parser tests green, AST dumps stable.

**Increment 1b — done 2026-10-22.** `type-system.md` (spec first), then:
- the type checker with bidirectional literal typing;
- constant folding with cycle detection (`IntegerSemantics` defines the exact operator semantics
  that 1c's C runtime must match);
- definite assignment and missing-return analysis on structured HIR;
- the HIR printer and `novac check [--dump-hir]`.

Semantic analysis runs only on files that parsed cleanly. Two simplifications, both documented:
the flow analysis runs on HIR, not NIR (no moves exist yet, so no fixpoint is needed), and all
files of a compilation share one item scope.

**Increment 1c — done 2026-10-22.**
- `intermediate-representation.md` (spec first).
- NIR (CFG) and the HIR→NIR lowering, with explicit evaluation order and short-circuit branches.
- The C emitter: labelled blocks, checked helpers, zero-initialised locals, deterministic output.
- The runtime: `nova/rt.h` (freestanding helpers), `nova_rt_core.c` (formatting, panics) and
  `nova_rt_hosted.c` (stdio). They are embedded in the compiler and written next to the generated C.
- `novac build [--emit c|exe] [--dump-nir]`.
- Tests: run-pass/run-fail corpora, the runtime agreement harness, determinism, C snapshots and
  freestanding cross-compiles.

Deviation: no separate LIR yet (`intermediate-representation.md` §4).

**Acceptance status (§3), 2026-10-22:**

| # | Criterion | Status |
|---|---|---|
| 1 | Corpus ≥ 40 parse, ≥ 40 check-fail, ≥ 50 run-pass, ≥ 15 run-fail | met: 38 files / 72 cases, 19 files / 99 cases, 51, 19 |
| 2 | Generated C warning-free under the strict set on clang **and gcc** | clang: met. gcc: **not run** (no GCC on the dev host) |
| 3 | Differential: {clang, gcc} × {-O0, -O2} × {plain, ASan+UBSan} agree | clang half met (plus the portable-overflow config). gcc and Linux CI: **pending** |
| 4 | Corpus compiles for both CosmoOS triples with CosmoOS `USER_CFLAGS` | generic freestanding flags met. Exact CosmoOS flags: **pending (1d)** |
| 5 | Determinism | met (byte-identical C, no host paths) |
| 6 | Diagnostics tests assert code and position | met (line, and column where annotated) |
| 7 | Docs | met: grammar, type-system, IR, diagnostics, testing |

**Increment 1d (next):** C1 (`hello` in CosmoOS under QEMU on both arches, stage S0), compiling the
corpus with the exact CosmoOS flags, and a GCC differential run (Linux CI or a local GCC). 1b (resolution + types + HIR),
1c (NIR + LIR + C + runtime + run tests) and 1d (C1 script) follow.
