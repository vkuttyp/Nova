using System.Globalization;
using System.Text.RegularExpressions;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// The M3 fault-injection sweep (docs/milestone-3.md, criterion 1). Each run-pass program that
/// allocates is built with ASan+UBSan and run once to count its allocation attempts <i>n</i>
/// (NOVA_ALLOC_REPORT). It is then run again with attempt <i>k</i> failing (NOVA_FAIL_ALLOC_AT),
/// for every <i>k ≤ n</i>. Every run must end either with a normal return from <c>main</c> and no
/// live allocations, or with the panic of <c>!</c> on the failed allocation. Leaks, double frees,
/// use after free and any other panic or sanitizer report fail the test.
/// </summary>
public partial class FaultInjectionTests
{
    private const int MaxAttempts = 200;
    /// <summary>Runtime calls that allocate: directly, or through a region (M4).</summary>
    private static readonly string[] s_allocationCalls = ["nova_alloc(", "nova_arena_alloc("];
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> RunPassFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, "run-pass"), "*.nova").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RunPassFiles))]
    public void Every_allocation_failure_is_handled(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors);
        var program = CBackend.Generate(compilation, requireMain: true, out _);
        Assert.NotNull(program);
        var main = program.Files.Single(f => f.Key == CProgram.MainFileName).Value;
        if (!s_allocationCalls.Any(call => main.Contains(call, StringComparison.Ordinal)))
        {
            return; // the program never allocates
        }

        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);
        var config = CToolchains.Configs.Single(c => c.Name.Contains("asan", StringComparison.Ordinal));
        var exe = Path.Combine(temp.Path, "program");
        var build = Toolchain.CompileExecutable(config.Compiler, temp.Path, program, exe, config.Flags);
        Assert.True(build.ExitCode == 0, build.StandardError);

        var baseline = Run(exe, failAt: 0);
        var attempts = Report(baseline, "baseline");
        Assert.True(attempts.Live == 0, $"{relativePath}: {attempts.Live} allocation(s) leaked without fault injection");
        Assert.True(attempts.Attempts >= 1, "the program allocates, but no attempt was counted");

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(FailurePoints(attempts.Attempts), k =>
        {
            var run = Run(exe, failAt: k);
            if (Problem(run) is { } problem)
            {
                failures.Add($"allocation {k} of {attempts.Attempts} failing: {problem}\nstderr:\n{run.StandardError}");
            }
        });
        Assert.True(failures.IsEmpty, $"{relativePath}:\n" + string.Join("\n", failures.OrderBy(f => f, StringComparer.Ordinal)));
    }

    /// <summary>
    /// Every attempt, or for programs with more than <see cref="MaxAttempts"/> attempts (deep structures):
    /// the first 50, the last 50 and 100 evenly spaced ones.
    /// </summary>
    private static IEnumerable<long> FailurePoints(long attempts) => attempts <= MaxAttempts
        ? Enumerable.Range(1, (int)attempts).Select(k => (long)k)
        : Enumerable.Range(1, 50).Select(k => (long)k)
            .Concat(Enumerable.Range(0, 50).Select(k => attempts - k))
            .Concat(Enumerable.Range(1, 100).Select(k => k * attempts / 101))
            .Distinct();

    private static ToolResult Run(string exe, long failAt) => Toolchain.Run(exe, [], TimeSpan.FromSeconds(30), new Dictionary<string, string>
    {
        ["NOVA_ALLOC_REPORT"] = "1",
        ["NOVA_FAIL_ALLOC_AT"] = failAt.ToString(CultureInfo.InvariantCulture),
    });

    /// <summary>Null if the run ended in an accepted way; otherwise what went wrong.</summary>
    private static string? Problem(ToolResult run)
    {
        if (run.StandardError.Contains("Sanitizer", StringComparison.Ordinal) || run.StandardError.Contains("runtime error", StringComparison.Ordinal))
        {
            return "sanitizer report";
        }
        if (run.StandardError.Contains("nova panic: ", StringComparison.Ordinal))
        {
            // `!` on the failed allocation (or on a result carrying its error) is the accepted panic.
            return run.StandardError.Contains("nova panic: unwrapped an Err value", StringComparison.Ordinal) && run.ExitCode == 134
                ? null
                : "unexpected panic";
        }
        if (ReportPattern().Match(run.StandardError) is not { Success: true } match)
        {
            return $"no allocation report (exit code {run.ExitCode})";
        }
        return match.Groups[2].Value == "0" ? null : $"{match.Groups[2].Value} allocation(s) leaked";
    }

    private static (long Attempts, long Live) Report(ToolResult run, string what)
    {
        var match = ReportPattern().Match(run.StandardError);
        Assert.True(match.Success, $"{what}: no allocation report; exit code {run.ExitCode}\n{run.StandardError}");
        return (long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"nova alloc: attempts=(\d+) live=(\d+)")]
    private static partial Regex ReportPattern();
}
