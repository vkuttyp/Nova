using System.Text;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// MSVC, best effort (docs/c-backend.md §1.1): every run-pass and run-fail program builds with
/// <c>cl /std:c11 /W3 /WX</c> (with the 8 MiB stack of Linux and macOS: <c>/link /STACK:8388608</c>) and gives the corpus expectations. Runs where <c>cl</c> is set up (the
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

    /// <summary>Builds a program with MSVC into <paramref name="temp"/>; returns the executable.</summary>
    private static string Build(string relativePath, string text, TempDirectory temp)
    {
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors, string.Join("\n", compilation.Diagnostics.Items));
        var program = CBackend.Generate(compilation, requireMain: true, out _)!;
        var gen = Path.Combine(temp.Path, "gen");
        program.WriteTo(gen);
        var exe = Path.Combine(temp.Path, "program.exe");
        var build = Toolchain.Run("cl",
            ["/nologo", "/std:c11", "/W3", "/WX", "/I", gen, .. program.SourceFiles.Select(f => Path.Combine(gen, f)), $"/Fe:{exe}", "/link", "/STACK:8388608"],
            TimeSpan.FromMinutes(2), workingDirectory: gen);
        Assert.True(build.ExitCode == 0, $"cl failed:\n{build.StandardOutput}\n{build.StandardError}");
        return exe;
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
        using var temp = new TempDirectory();
        var exe = Build(relativePath, text, temp);

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

    /// <summary>Files and standard input on Windows: nwc on cases recorded from BusyBox (ProgramTests).</summary>
    [Theory]
    [InlineData("x y", null, "        2         5        10 x\n        1         1         4 y\n        3         6        14 total\n", 0)]
    [InlineData("-l", "x", "2\n", 0)]
    [InlineData("nosuch", null, "", 1)]
    public void Msvc_nwc_reads_files_and_standard_input(string arguments, string? input, string stdout, int status)
    {
        if (!Enabled)
        {
            return;
        }
        using var temp = new TempDirectory();
        var source = Path.Combine(TestHelpers.RepoRoot(), "programs", "nwc", "nwc.nova");
        var exe = Build("nwc.nova", File.ReadAllText(source), temp);
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "x"), Encoding.UTF8.GetBytes("a b\nc d e\n"));
        File.WriteAllBytes(Path.Combine(data, "y"), Encoding.UTF8.GetBytes("one\n"));
        var run = Toolchain.Run(exe, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), TimeSpan.FromSeconds(30),
            workingDirectory: data, standardInput: input is null ? [] : File.ReadAllBytes(Path.Combine(data, input)));
        Assert.Equal(stdout, run.StandardOutput);
        Assert.Equal(status, run.ExitCode);
    }

    /// <summary>Process.run on Windows (_spawnvp), and TCP reported as unsupported (c-backend.md §1.1).</summary>
    [Fact]
    public void Msvc_runs_processes_and_reports_tcp_as_unsupported()
    {
        if (!Enabled)
        {
            return;
        }
        const string Program = """
            fn main() -> i32 {
                var script = Vector<StringView>.create();
                script.push("/c")!;
                script.push("exit 7")!;
                Console.writeLine("before");
                Console.writeLine(Process.run("cmd", script)!);
                var none = Vector<StringView>.create();
                match (Process.run("nova-no-such-program", none)) {
                    Ok(status) => { Console.writeLine("unexpected"); }
                    Err(e) => { Console.writeLine(e.message()); }
                }
                match (TcpListener.bind("127.0.0.1", 0u16)) {
                    Ok(l) => { Console.writeLine("unexpected"); }
                    Err(e) => {
                        match (e) {
                            IoError.Other(code) => { Console.writeLine("unsupported"); }
                            _ => { Console.writeLine("unexpected error"); }
                        }
                    }
                }
                return 0;
            }
            """;
        using var temp = new TempDirectory();
        var run = Toolchain.Run(Build("process.nova", Program, temp), [], TimeSpan.FromSeconds(30));
        Assert.Equal("before\n7\nNo such file or directory\nunsupported\n", run.StandardOutput.ReplaceLineEndings("\n"));
        Assert.Equal(0, run.ExitCode);
    }
}
