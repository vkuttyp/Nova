using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// The C runtime helpers (runtime/include/nova/rt.h) must implement exactly the semantics of
/// <see cref="IntegerSemantics"/> (docs/type-system.md §6–7). This test generates a C harness
/// that calls every helper on edge values of every integer type. A panic is caught with longjmp
/// and its kind recorded. The output is compared line by line with values computed in C#, under
/// every C configuration, including the portable overflow fallback and UBSan.
/// </summary>
public class RuntimeSemanticsTests
{
    private const int PanicOverflow = 1;
    private const int PanicDivideByZero = 2;
    private const int PanicShift = 3;

    private static readonly TypeTable Types = new(TargetInfo.Lp64);

    private static readonly ImmutableArray<NovaType> IntegerTypes =
        [Types.I8, Types.I16, Types.I32, Types.I64, Types.U8, Types.U16, Types.U32, Types.U64];

    private static readonly (BinaryOp Op, string Helper)[] CheckedOps =
        [(BinaryOp.Add, "add"), (BinaryOp.Subtract, "sub"), (BinaryOp.Multiply, "mul"), (BinaryOp.Divide, "div"), (BinaryOp.Remainder, "rem")];

    private sealed class Harness
    {
        private readonly StringBuilder _functions = new();
        private readonly StringBuilder _current = new();
        private readonly List<string> _functionNames = [];
        private int _casesInFunction;

        public List<string> Expected { get; } = [];

        public void Case(string cExpression, bool signedResult, string expected)
        {
            if (_casesInFunction == 0)
            {
                _current.Clear();
            }
            var macro = signedResult ? "CASE_S" : "CASE_U";
            _current.Append(CultureInfo.InvariantCulture, $"    {macro}({cExpression});\n");
            Expected.Add(expected);
            if (++_casesInFunction == 200)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_casesInFunction == 0)
            {
                return;
            }
            var name = $"cases_{_functionNames.Count}";
            _functionNames.Add(name);
            _functions.Append(CultureInfo.InvariantCulture, $"static void {name}(void) {{\n{_current}}}\n\n");
            _casesInFunction = 0;
        }

        public string Source()
        {
            Flush();
            var sb = new StringBuilder("""
                #include "nova/rt.h"
                #include <setjmp.h>
                #include <stdio.h>

                static jmp_buf g_env;
                static volatile int g_kind;

                _Noreturn void nova_panic(nv_panic_kind kind, nv_loc loc) {
                    (void)loc;
                    g_kind = (int)kind;
                    longjmp(g_env, 1);
                }

                #define CASE_S(expr) do { if (setjmp(g_env) == 0) { long long r_ = (long long)(expr); printf("ok %lld\n", r_); } else { printf("panic %d\n", g_kind); } } while (0)
                #define CASE_U(expr) do { if (setjmp(g_env) == 0) { unsigned long long r_ = (unsigned long long)(expr); printf("ok %llu\n", r_); } else { printf("panic %d\n", g_kind); } } while (0)

                """);
            sb.Append(_functions);
            sb.Append("int main(void) {\n");
            foreach (var name in _functionNames)
            {
                sb.Append(CultureInfo.InvariantCulture, $"    {name}();\n");
            }
            sb.Append("    return 0;\n}\n");
            return sb.ToString();
        }
    }

    private static IEnumerable<BigInteger> EdgeValues(NovaType type)
    {
        BigInteger[] candidates =
        [
            type.Min, type.Min + 1, type.Min / 2, -3, -2, -1, 0, 1, 2, 3, 7, 10,
            type.Max / 2, type.Max / 2 + 1, type.Max - 1, type.Max,
        ];
        return candidates.Where(type.Fits).Distinct().Order();
    }

    private static string C(BigInteger value, NovaType type) => CEmitter.Constant((Int128)value, type);

    private static string Ok(BigInteger value) => "ok " + value.ToString(CultureInfo.InvariantCulture);

    private static string Panic(int kind) => "panic " + kind.ToString(CultureInfo.InvariantCulture);

    private static Harness BuildHarness()
    {
        const string loc = "NV_LOC(\"t\", 1u, 1u)";
        var harness = new Harness();

        foreach (var type in IntegerTypes)
        {
            var suffix = $"{(type.IsSigned ? "i" : "u")}{type.Bits}";
            var values = EdgeValues(type).ToList();

            foreach (var (op, helper) in CheckedOps)
            {
                foreach (var a in values)
                {
                    foreach (var b in values)
                    {
                        var expected = IntegerSemantics.TryEvaluate(op, a, b, type, out var result, out _)
                            ? Ok(result)
                            : Panic(b.IsZero && op is BinaryOp.Divide or BinaryOp.Remainder ? PanicDivideByZero : PanicOverflow);
                        harness.Case($"nv_{helper}_{suffix}({C(a, type)}, {C(b, type)}, {loc})", type.IsSigned, expected);
                    }
                }
            }

            if (type.IsSigned)
            {
                foreach (var a in values)
                {
                    harness.Case($"nv_neg_{suffix}({C(a, type)}, {loc})", true, type.Fits(-a) ? Ok(-a) : Panic(PanicOverflow));
                }
            }
            else
            {
                BigInteger[] signedAmounts = [-1, 0, 1, type.Bits - 1, type.Bits, type.Bits + 1, 1000];
                BigInteger[] unsignedAmounts = [0, 1, type.Bits - 1, type.Bits, ulong.MaxValue];
                foreach (var a in values)
                {
                    foreach (var (op, helper) in new[] { (BinaryOp.ShiftLeft, "shl"), (BinaryOp.ShiftRight, "shr") })
                    {
                        foreach (var n in signedAmounts)
                        {
                            var expected = IntegerSemantics.TryEvaluate(op, a, n, type, out var r, out _) ? Ok(r) : Panic(PanicShift);
                            harness.Case($"nv_{helper}_{suffix}({C(a, type)}, nv_shift_amount_s({C(n, Types.I64)}, {type.Bits}u, {loc}))", false, expected);
                        }
                        foreach (var n in unsignedAmounts)
                        {
                            var expected = IntegerSemantics.TryEvaluate(op, a, n, type, out var r, out _) ? Ok(r) : Panic(PanicShift);
                            harness.Case($"nv_{helper}_{suffix}({C(a, type)}, nv_shift_amount_u({C(n, Types.U64)}, {type.Bits}u, {loc}))", false, expected);
                        }
                    }
                }
            }

            // truncate<target>(value), emitted exactly as CEmitter.Truncate does.
            foreach (var target in IntegerTypes)
            {
                foreach (var v in values)
                {
                    var asUnsigned = $"(uint{target.Bits}_t){C(v, type)}";
                    var expression = target.IsSigned ? $"nv_as_i{target.Bits}({asUnsigned})" : asUnsigned;
                    harness.Case(expression, target.IsSigned, Ok(IntegerSemantics.Wrap(v, target)));
                }
            }
        }
        return harness;
    }

    [Fact]
    public void C_runtime_helpers_agree_with_IntegerSemantics()
    {
        var harness = BuildHarness();
        var program = CBackend.RuntimeOnly(("harness.c", harness.Source()));

        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(CToolchains.Configs, (config, _, index) =>
        {
            var exe = Path.Combine(temp.Path, $"harness-{index}");
            // The harness itself uses setjmp/longjmp, which GCC's -Wclobbered (in -Wextra) flags for
            // the NV_LOC compound literals. Generated programs never use setjmp; this is harness-only.
            var flags = config.Name.StartsWith("gcc", StringComparison.Ordinal) ? [.. config.Flags, "-Wno-clobbered"] : config.Flags;
            var build = Toolchain.CompileExecutable(config.Compiler, temp.Path, program, exe, flags);
            if (build.ExitCode != 0)
            {
                failures.Add($"[{config.Name}] harness did not compile:\n{build.StandardError}");
                return;
            }
            var run = Toolchain.Run(exe, [], TimeSpan.FromMinutes(2));
            var actual = run.StandardOutput.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
            if (run.ExitCode != 0 || actual.Length != harness.Expected.Count)
            {
                failures.Add($"[{config.Name}] exit {run.ExitCode}, {actual.Length} lines for {harness.Expected.Count} cases\n{run.StandardError}");
                return;
            }
            var mismatches = Enumerable.Range(0, actual.Length)
                .Where(i => actual[i] != harness.Expected[i])
                .Take(10)
                .Select(i => $"case {i}: expected '{harness.Expected[i]}', got '{actual[i]}'")
                .ToList();
            if (mismatches.Count > 0)
            {
                failures.Add($"[{config.Name}]\n" + string.Join("\n", mismatches));
            }
        });

        Assert.True(failures.IsEmpty, string.Join("\n", failures.OrderBy(f => f, StringComparer.Ordinal)));
        Assert.True(harness.Expected.Count > 5000, "the harness should cover thousands of cases");
    }
}
