using System.Text;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// C interoperability (type-system.md §28): each tests/corpus/ffi/NAME.nova is built with its C half NAME.c under
/// every C configuration, run, and checked against its expectations (as in <see cref="RunCorpusTests"/>). The
/// generated nova_exports.h is a reviewed snapshot, NAME.h.
/// </summary>
public class FfiTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> FfiFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, "ffi"), "*.nova").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
        }
        return data;
    }

    private static (Compilation Compilation, CProgram Program) Generate(string relativePath, CEmitOptions? options = null)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors, string.Join("\n", compilation.Diagnostics.Items));
        var cFile = Path.ChangeExtension(Path.Combine(CorpusRoot, relativePath), ".c");
        var program = CBackend.Generate(compilation, requireMain: false, out _, options)!
            .WithCSources([(Path.GetFileName(cFile), File.ReadAllText(cFile))]);
        return (compilation, program);
    }

    [Theory]
    [MemberData(nameof(FfiFiles))]
    public void Ffi_program_runs_everywhere(string relativePath)
    {
        var expected = RunCorpusTests.ParseExpectations(File.ReadAllText(Path.Combine(CorpusRoot, relativePath)));
        var (_, program) = Generate(relativePath);
        var (_, flat) = Generate(relativePath, new CEmitOptions(Structured: false));
        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);
        var flatDirectory = Path.Combine(temp.Path, "flat");
        flat.WriteTo(flatDirectory);
        var failures = new StringBuilder();
        foreach (var (config, index) in CToolchains.Configs.Select((c, i) => (c, i)))
        {
            var (directory, source) = config.Flat ? (flatDirectory, flat) : (temp.Path, program);
            var exe = Path.Combine(temp.Path, $"program-{index}");
            var build = Toolchain.CompileExecutable(config.Compiler, directory, source, exe, config.Flags);
            if (build.ExitCode != 0)
            {
                failures.Append($"[{config.Name}] C compilation failed:\n{build.StandardError}\n");
                continue;
            }
            var run = Toolchain.Run(exe, [], TimeSpan.FromSeconds(30));
            if (run.StandardOutput.ReplaceLineEndings("\n") != expected.Stdout || run.ExitCode != expected.ExitCode)
            {
                failures.Append($"[{config.Name}] exit {run.ExitCode}, stdout:\n{run.StandardOutput}\nstderr:\n{run.StandardError}\n");
            }
            else if (expected.PanicReason is { } reason
                     && !run.StandardError.StartsWith($"nova panic: {reason} at {relativePath}:{expected.PanicLine}:", StringComparison.Ordinal))
            {
                failures.Append($"[{config.Name}] stderr: {run.StandardError}\n");
            }
        }
        Assert.True(failures.Length == 0, failures.ToString());
    }

    [Theory]
    [MemberData(nameof(FfiFiles))]
    public void Exports_header_snapshot(string relativePath)
    {
        var (_, program) = Generate(relativePath);
        var actual = program.Files.Single(f => f.Key == CProgram.ExportsHeaderName).Value;
        var snapshot = Path.ChangeExtension(Path.Combine(CorpusRoot, relativePath), ".h");
        if (Environment.GetEnvironmentVariable("NOVA_BLESS") == "1")
        {
            File.WriteAllText(snapshot, actual);
            return;
        }
        Assert.True(File.Exists(snapshot), $"missing snapshot {snapshot}; run with NOVA_BLESS=1 and review it");
        Assert.Equal(File.ReadAllText(snapshot).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void The_interpreter_runs_no_C_code()
    {
        var outcome = InterpreterTests.Interpret("t.nova", """
            extern "C" { fn c_function(x: i32) -> i32; }
            /// Safety: c_function is defined in C.
            fn main() -> i32 { return unsafe { c_function(1) }; }
            """);
        Assert.Equal(Interpreter.NirInterpreter.FaultExitStatus, outcome.ExitCode);
        Assert.StartsWith("nova interp: fault: unsupported: call to C function 'c_function'", outcome.Stderr, StringComparison.Ordinal);
    }
}
