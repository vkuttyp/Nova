using System.Text.RegularExpressions;
using Nova.Compiler.Analysis;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// M5, escape and placement analysis (docs/milestone-5.md; memory-model.md §5). Placement must be unobservable
/// except in allocation counts: equal outputs, identical drop timing, never more attempts, and native code that
/// agrees with the interpreter applying the same decisions.
/// </summary>
public partial class PlacementTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> RunFiles() => InterpreterTests.RunFiles();

    public static TheoryData<string> PlacementFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, "placement"), "*.nova").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
        }
        return data;
    }

    /// <summary>Criteria 1, 2 and 5: the same outcome and drop trace with and without placement, and no extra attempts.</summary>
    [Theory]
    [MemberData(nameof(RunFiles))]
    [MemberData(nameof(PlacementFiles))]
    public void Placement_is_unobservable(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var plainTrace = new List<string>();
        var placedTrace = new List<string>();
        var plain = InterpreterTests.Interpret(relativePath, text, report: true, dropTrace: plainTrace);
        var placed = InterpreterTests.Interpret(relativePath, text, report: true, placement: true, dropTrace: placedTrace);
        Assert.Equal(plain.Stdout, placed.Stdout);
        Assert.Equal(plain.ExitCode, placed.ExitCode);
        Assert.Equal(WithoutReport(plain.Stderr), WithoutReport(placed.Stderr));
        Assert.Equal(plainTrace, placedTrace); // drop timing: the same drops at the same statements, in the same order
        if (Attempts(plain.Stderr) is { } before && Attempts(placed.Stderr) is { } after)
        {
            Assert.True(after <= before, $"placement made more allocation attempts: {after} > {before}");
        }
    }

    /// <summary>Criterion 5: native code with placement agrees with the interpreter with placement, allocation report included.</summary>
    [Theory]
    [MemberData(nameof(RunFiles))]
    [MemberData(nameof(PlacementFiles))]
    public void Native_placement_agrees_with_the_interpreter(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        var program = CBackend.Generate(compilation, requireMain: true, out _, new CEmitOptions(Placement: true))!;
        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);
        var exe = Path.Combine(temp.Path, "program");
        var config = CToolchains.Configs.Single(c => c.Name.Contains("asan", StringComparison.Ordinal));
        var build = Toolchain.CompileExecutable(config.Compiler, temp.Path, program, exe, config.Flags);
        Assert.True(build.ExitCode == 0, build.StandardError);
        var run = Toolchain.Run(exe, [], TimeSpan.FromSeconds(30), new Dictionary<string, string> { ["NOVA_ALLOC_REPORT"] = "1" });
        var interpreted = InterpreterTests.Interpret(relativePath, text, report: true, placement: true);
        Assert.Equal(interpreted, new InterpreterTests.Outcome(run.StandardOutput.ReplaceLineEndings("\n"), run.StandardError.ReplaceLineEndings("\n"), run.ExitCode));
    }

    /// <summary>Criterion 4: the analysis reads NIR and changes nothing, so it cannot affect what is accepted.</summary>
    [Theory]
    [MemberData(nameof(RunFiles))]
    public void Analysis_does_not_change_the_program(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        var nir = compilation.Nir!;
        var before = NirPrinter.Print(nir, compilation.Symbols);
        _ = PlacementAnalysis.Analyze(nir, compilation.Symbols);
        Assert.Equal(before, NirPrinter.Print(nir, compilation.Symbols));
    }

    /// <summary>Criterion 3: reviewed snapshots of --explain-allocations.</summary>
    [Theory]
    [MemberData(nameof(PlacementFiles))]
    public void Explain_allocations_snapshot(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors);
        var actual = PlacementAnalysis.Analyze(compilation.Nir!, compilation.Symbols).Explain();
        var snapshot = Path.ChangeExtension(Path.Combine(CorpusRoot, relativePath), ".alloc");
        if (Environment.GetEnvironmentVariable("NOVA_BLESS") == "1")
        {
            File.WriteAllText(snapshot, actual);
            return;
        }
        Assert.True(File.Exists(snapshot), $"missing snapshot {snapshot}; run with NOVA_BLESS=1 and review it");
        Assert.Equal(File.ReadAllText(snapshot).ReplaceLineEndings("\n"), actual);
    }

    private static string WithoutReport(string stderr) => ReportPattern().Replace(stderr, "");

    private static long? Attempts(string stderr) =>
        ReportPattern().Match(stderr) is { Success: true } match ? long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"nova alloc: attempts=(\d+) live=\d+\n")]
    private static partial Regex ReportPattern();
}
