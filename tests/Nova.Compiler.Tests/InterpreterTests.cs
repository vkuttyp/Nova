using System.Text;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Interpreter;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// The NIR reference interpreter agrees with native code (intermediate-representation.md §5.5): every run-pass
/// and run-fail program gives the corpus expectations, which the native runs check under every C configuration.
/// </summary>
public class InterpreterTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> RunFiles()
    {
        var data = new TheoryData<string>();
        foreach (var directory in new[] { "run-pass", "run-fail" })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, directory), "*.nova").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
            }
        }
        return data;
    }

    internal sealed record Outcome(string Stdout, string Stderr, int ExitCode);

    /// <summary>Interprets a program in-process with captured streams.</summary>
    internal static Outcome Interpret(string relativePath, string text, ulong failAt = 0, bool report = false, IReadOnlyList<string>? arguments = null,
        byte[]? input = null, string? workingDirectory = null, Action<NirProgram>? mutate = null)
    {
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors, string.Join("\n", compilation.Diagnostics.Items));
        Assert.NotNull(CBackend.Generate(compilation, requireMain: true, out var nir));
        mutate?.Invoke(nir!);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        using var stdin = new MemoryStream(input ?? []);
        var status = NirInterpreter.Run(nir!, compilation.Symbols, new InterpreterOptions
        {
            StandardOutput = stdout,
            StandardError = stderr,
            StandardInput = stdin,
            Arguments = arguments ?? ["program"],
            FailAllocationAt = failAt,
            AllocationReport = report,
            WorkingDirectory = workingDirectory,
        });
        return new Outcome(Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), status);
    }

    [Theory]
    [MemberData(nameof(RunFiles))]
    public void Interpreter_agrees_with_the_corpus(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var expected = RunCorpusTests.ParseExpectations(text);
        var outcome = Interpret(relativePath, text);
        Assert.Equal(expected.Stdout, outcome.Stdout);
        Assert.Equal(expected.ExitCode, outcome.ExitCode);
        if (expected.PanicReason is { } reason)
        {
            Assert.StartsWith($"nova panic: {reason} at {relativePath}:{expected.PanicLine}:", outcome.Stderr, StringComparison.Ordinal);
            Assert.Single(outcome.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            Assert.Equal("", outcome.Stderr);
        }
    }

    // ---- The dynamic safety oracle (§5.2): NIR changed by hand into unsafe code must fault ----

    private static NirFunction MainOf(NirProgram nir) => nir.Functions.Single(f => f.Source.Syntax.Name.Span.Start == f.Source.Source.Text.IndexOf("main", StringComparison.Ordinal));

    private static void AssertFault(Outcome outcome, string what)
    {
        Assert.Equal(NirInterpreter.FaultExitStatus, outcome.ExitCode);
        Assert.StartsWith($"nova interp: fault: {what}", outcome.Stderr, StringComparison.Ordinal);
        Assert.Contains(" at t.nova:", outcome.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicated_drop_is_a_double_free()
    {
        var outcome = Interpret("t.nova", "fn main() -> i32 { let b = Box.create(5)!; return 0; }", mutate: nir =>
        {
            var block = MainOf(nir).Blocks.First(b => b.Statements.Any(s => s is NirDrop { Place.Type.IsBox: true }));
            var drop = block.Statements.First(s => s is NirDrop { Place.Type.IsBox: true });
            block.Statements.Insert(block.Statements.IndexOf(drop) + 1, drop);
        });
        AssertFault(outcome, "double free");
    }

    [Fact]
    public void A_read_after_storage_dead_is_a_use_of_a_dead_local()
    {
        var outcome = Interpret("t.nova", "fn main() -> i32 { var x = 5; x += 1; return x; }", mutate: nir =>
        {
            var main = MainOf(nir);
            var x = main.Locals.First(l => l.Name is not null);
            var block = main.Blocks[0];
            var assign = block.Statements.First(s => s is NirAssign a && a.Target.Local == x);
            block.Statements.Insert(block.Statements.IndexOf(assign) + 1, new NirStorageDead(x));
        });
        AssertFault(outcome, "use of a dead local");
    }

    /// <summary>Without its bounds check, reading past a vector's length reads heap memory never written: native C prints garbage.</summary>
    [Fact]
    public void A_missing_bounds_check_reads_uninitialised_memory()
    {
        const string Program = "fn main() -> i32 { var v = Vector<i32>.withCapacity(4)!; v.push(1)!; Console.writeLine(v[2]); return 0; }";
        Assert.Equal(PanicExitStatus(), Interpret("t.nova", Program).ExitCode); // checked: index out of bounds
        var outcome = Interpret("t.nova", Program, mutate: nir =>
        {
            foreach (var block in MainOf(nir).Blocks)
            {
                block.Statements.RemoveAll(s => s is NirBoundsCheck);
            }
        });
        AssertFault(outcome, "use of uninitialised memory");
    }

    private static int PanicExitStatus() => NirInterpreter.PanicExitStatus;
}
