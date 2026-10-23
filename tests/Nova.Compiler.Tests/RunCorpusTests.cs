using System.Text;
using System.Text.RegularExpressions;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// Compiles tests/corpus/run-pass and run-fail programs to C, builds them with every available
/// C configuration (strict warnings, -Werror), runs them, and checks the results. Directives:
/// <c>// expect-stdout: text</c> (one per output line, in order), <c>// expect-exit: N</c>, and in run-fail
/// files <c>// expect-panic: reason</c> on the line where the panic must be reported.
/// All configurations must agree with the expectations, which makes this a differential test too.
/// </summary>
public partial class RunCorpusTests
{
    private const int AbortExitCode = 134; // 128 + SIGABRT

    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> RunPassFiles() => Files("run-pass");

    public static TheoryData<string> RunFailFiles() => Files("run-fail");

    private static TheoryData<string> Files(string directory)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, directory), "*.nova").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RunPassFiles))]
    public void Run_pass(string relativePath) => RunEverywhere(relativePath, expectPanic: false);

    [Theory]
    [MemberData(nameof(RunFailFiles))]
    public void Run_fail(string relativePath) => RunEverywhere(relativePath, expectPanic: true);

    internal sealed record Expectations(string Stdout, int ExitCode, string? PanicReason, int PanicLine);

    private static void RunEverywhere(string relativePath, bool expectPanic)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var expected = ParseExpectations(text);
        Assert.Equal(expectPanic, expected.PanicReason is not null);
        RunEverywhere(relativePath, text, expected);
    }

    /// <summary>Builds <paramref name="text"/> with every C configuration, runs it, and checks the results against <paramref name="expected"/>.</summary>
    internal static void RunEverywhere(string relativePath, string text, Expectations expected)
    {
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.True(!compilation.Diagnostics.HasErrors,
            "compile errors:\n" + string.Join("\n", compilation.Diagnostics.Items.Select(d => d.ToString())));
        var program = CBackend.Generate(compilation, requireMain: true, out _);
        Assert.NotNull(program);

        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(CToolchains.Configs, (config, _, index) =>
        {
            var exe = Path.Combine(temp.Path, $"program-{index}");
            var build = Toolchain.CompileExecutable(config.Compiler, temp.Path, program, exe, config.Flags);
            if (build.ExitCode != 0)
            {
                failures.Add($"[{config.Name}] C compilation failed:\n{build.StandardError}");
                return;
            }

            var run = Toolchain.Run(exe, [], TimeSpan.FromSeconds(30));
            var problems = new StringBuilder();
            if (run.StandardOutput.ReplaceLineEndings("\n") != expected.Stdout)
            {
                problems.Append(CultureInfoInvariant($"stdout:\n--- expected ---\n{expected.Stdout}--- actual ---\n{run.StandardOutput}\n"));
            }
            if (run.ExitCode != expected.ExitCode)
            {
                problems.Append(CultureInfoInvariant($"exit code {run.ExitCode}, expected {expected.ExitCode}\n"));
            }
            if (expected.PanicReason is { } reason)
            {
                var marker = $"nova panic: {reason} at {relativePath}:{expected.PanicLine}:";
                if (!run.StandardError.Contains(marker, StringComparison.Ordinal))
                {
                    problems.Append(CultureInfoInvariant($"stderr does not contain '{marker}':\n{run.StandardError}\n"));
                }
            }
            else if (run.StandardError.Length > 0)
            {
                problems.Append(CultureInfoInvariant($"unexpected stderr:\n{run.StandardError}\n"));
            }
            if (problems.Length > 0)
            {
                failures.Add($"[{config.Name}] {problems}");
            }
        });

        Assert.True(failures.IsEmpty, string.Join("\n", failures.OrderBy(f => f, StringComparer.Ordinal)));
    }

    private static string CultureInfoInvariant(FormattableString text) => FormattableString.Invariant(text);

    private static Expectations ParseExpectations(string text)
    {
        var stdout = new StringBuilder();
        var exitCode = 0;
        string? panicReason = null;
        var panicLine = 0;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = DirectivePattern().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }
            var value = match.Groups[2].Value;
            switch (match.Groups[1].Value)
            {
                case "stdout":
                    stdout.Append(value).Append('\n');
                    break;
                case "exit":
                    exitCode = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "panic":
                    Assert.Null(panicReason); // one panic per test
                    panicReason = value;
                    panicLine = i + 1;
                    exitCode = AbortExitCode;
                    break;
            }
        }
        return new Expectations(stdout.ToString(), exitCode, panicReason, panicLine);
    }

    // "// expect-stdout: text": the single space after ':' is part of the syntax, not the value.
    [GeneratedRegex(@"// expect-(stdout|exit|panic):(?: (.*))?$")]
    private static partial Regex DirectivePattern();
}
