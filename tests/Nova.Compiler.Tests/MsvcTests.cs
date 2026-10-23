using System.Text;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// MSVC, best effort (docs/c-backend.md §1.1): every run-pass and run-fail program builds with
/// <c>cl /std:c11 /W3 /WX</c> and gives the corpus expectations. Runs where <c>cl</c> is set up (the
/// `test-windows` CI job sets NOVA_MSVC=1, which makes a missing <c>cl</c> a failure); elsewhere it returns early.
/// </summary>
public class MsvcTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    /// <summary>Corpus programs that need what the Windows runtime does not support (none so far: TCP fails only after its address check).</summary>
    private static readonly HashSet<string> s_unsupported = [];

    public static TheoryData<string> RunFiles() => InterpreterTests.RunFiles();

    private static bool Enabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
            var required = Environment.GetEnvironmentVariable("NOVA_MSVC") == "1";
            try
            {
                _ = Toolchain.Run("cl", [], TimeSpan.FromSeconds(30));
                return true;
            }
            catch (System.ComponentModel.Win32Exception) when (!required)
            {
                return false;
            }
        }
    }

    [Theory]
    [MemberData(nameof(RunFiles))]
    public void Msvc_build_gives_the_corpus_expectations(string relativePath)
    {
        if (!Enabled || s_unsupported.Contains(relativePath))
        {
            return;
        }
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var expected = RunCorpusTests.ParseExpectations(text);
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors);
        var program = CBackend.Generate(compilation, requireMain: true, out _)!;
        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);
        var exe = Path.Combine(temp.Path, "program.exe");
        var build = Toolchain.Run("cl",
            ["/nologo", "/std:c11", "/W3", "/WX", "/I", temp.Path, .. program.SourceFiles.Select(f => Path.Combine(temp.Path, f)), $"/Fe:{exe}"],
            TimeSpan.FromMinutes(2), workingDirectory: temp.Path);
        Assert.True(build.ExitCode == 0, $"cl failed:\n{build.StandardOutput}\n{build.StandardError}");

        var run = Toolchain.Run(exe, [], TimeSpan.FromSeconds(30));
        Assert.Equal(expected.Stdout, run.StandardOutput.ReplaceLineEndings("\n"));
        Assert.Equal(expected.ExitCode, run.ExitCode & 0xFF); // Windows keeps all 32 bits of main's result
        if (expected.PanicReason is { } reason)
        {
            Assert.Contains($"nova panic: {reason} at {relativePath}:{expected.PanicLine}:", run.StandardError, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("", run.StandardError);
        }
    }
}
