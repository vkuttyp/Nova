# Milestone 7 — Production C backend (plan and acceptance)

Status: **in progress** (started 2026-10-23; 7a done). Normative: `c-backend.md` (§3 control flow, §7
debugging, §8 validation), `intermediate-representation.md` §5 (the NIR interpreter), `testing.md`
(fuzzing). Each increment writes its spec first, then the code and tests.

## Increments

| # | Content | Spec | Status |
|---|---|---|---|
| **7a** | **Source maps**: `#line` directives to Nova source (`--line-directives`, on with `-g`), source paths that never contain host paths, the C compiler run where the paths resolve; tests that the debug line table names Nova lines, and that the whole run corpus builds and runs with directives | c-backend §7 | **done 2026-10-23** |
| 7b | **Structured C**: `if`/`else`, loops, `break`/`continue` reconstructed from the NIR graph (dominator-based, for reducible graphs; `goto` only where no structured form exists, and the flat form as a fallback and test oracle). The run corpus passes in both forms | c-backend §3 | planned |
| 7c | **NIR reference interpreter** (`novac run --interp`): executes checked NIR with a model of memory (objects, places, liveness, initialisation), the runtime's allocation accounting and fault injection, and the same output, panics and exit statuses as native code. It reports any use of a dead or uninitialised object as an interpreter fault (a dynamic safety oracle). The whole run corpus and the fault sweep agree with native output | IR §5 | planned |
| 7d | **Random program generator** (Csmith-style, `tests/fuzz`): well-typed, terminating Nova programs over integers of every width, checked arithmetic, structs, enums, `match`, arrays, slices, references, boxes, vectors, optionals and results, printing a checksum. Oracle: the checker accepts every program, and the interpreter, clang and GCC at `-O0`/`-O2` agree (output and exit status) | testing §1 | planned |
| 7e | **Campaign and portability**: a 24-hour fuzz campaign (clang/GCC × O0/O2 × interpreter), with every finding fixed and kept as a corpus test; MSVC best-effort for the hosted profile (C11 mode) in CI | c-backend §1, §8 | planned |

## Acceptance criteria (`roadmap.md`)

1. A 24-hour fuzz campaign with no miscompile across clang/GCC × O0/O2.
2. The interpreter agrees with native output on the whole run corpus (run-pass and run-fail), and
   with the fault-injection sweep's outcomes.
3. Source maps: a debugger's line table maps generated code to Nova lines; generated C contains no
   host paths, with or without directives.
4. Structured C: the run corpus passes with structured and flat C under every C configuration;
   reviewed snapshots show the structured form.
5. MSVC best-effort: the hosted run corpus compiles with MSVC (C11) in CI, and runs where the
   runtime supports Windows; unsupported parts are listed.
6. Docs: `c-backend.md`, `intermediate-representation.md`, `testing.md`, `roadmap.md`, `risks.md`
   (R-1, R-3).

## Results so far

| Increment | Evidence (run 2026-10-23) |
|---|---|
| 7a | `SourceMapTests`: clang's line table (`-g -S`, `.file`/`.loc`) for a two-function program names `demo/gcd.nova` and every statement line (1–7, 9, 12–15), and no line outside the file or on the blank line; the `main.c` hand-back numbers are exact; directives are deterministic and contain no host path for all run-pass programs; `SourceText.SourcePath` cases. The run corpus gained a configuration `clang -O0 -g line-directives` (every run-pass and run-fail program builds warning-clean and agrees). Snapshot `emit-c/line-directives.c` reviewed. Manually: `novac build -g run-pass/gcd.nova`, then `lldb -o "breakpoint set --file gcd.nova --line 5"` stops in `nv_gcd` at `gcd.nova:5`, lists the Nova source, shows `v_x_2 = 1071`, and the backtrace names `gcd.nova:13` for the caller. Default output (no directives) is byte-identical to before: the 17 existing C snapshots are unchanged, so the guest runs (`c2.py`, `c3.py`) were not repeated. Not done: columns (C's columns, not Nova's, appear in the line table; `#line` cannot carry columns) |
