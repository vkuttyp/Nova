using System.Collections.Immutable;
using System.Text;
using Nova.Compiler;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Interpreter;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Fuzz;

/// <summary>What a program printed and how it ended.</summary>
public sealed record Outcome(string Stdout, string Stderr, int ExitCode)
{
    public override string ToString() => $"exit {ExitCode}, stdout {Stdout.Length} bytes, stderr: {Stderr.TrimEnd()}";
}

/// <summary>A way to compile the generated C (docs/testing.md §5).</summary>
public sealed record FuzzConfig(string Name, string Compiler, ImmutableArray<string> Flags, bool Flat = false);

/// <summary>The result of checking one program.</summary>
/// <param name="Finding">Null if every configuration agreed; otherwise what went wrong.</param>
public sealed record Verdict(int Seed, string Program, string? Finding, Outcome? Reference, int Configurations);

/// <summary>
/// The oracle (docs/testing.md §5): the checker accepts the program, the NIR interpreter runs it without a
/// fault, and every C configuration compiles it warning-free and gives the interpreter's outcome.
/// </summary>
public static class Oracle
{
    private static readonly string[] s_gccCandidates = ["gcc", "gcc-15", "gcc-14", "gcc-13", "gcc-12"];
    private static readonly Lazy<ImmutableArray<FuzzConfig>> s_configs = new(Discover);

    public static ImmutableArray<FuzzConfig> Configs => s_configs.Value;

    private static ImmutableArray<FuzzConfig> Discover()
    {
        var configs = ImmutableArray.CreateBuilder<FuzzConfig>();
        configs.Add(new("clang -O0", "clang", ["-O0", "-Werror"]));
        configs.Add(new("clang -O2", "clang", ["-O2", "-Werror"]));
        configs.Add(new("clang -O2 flat", "clang", ["-O2", "-Werror"], Flat: true));
        if (s_gccCandidates.FirstOrDefault(c => Works(c, "Free Software Foundation")) is { } gcc)
        {
            configs.Add(new($"{gcc} -O0", gcc, ["-O0", "-Werror"]));
            configs.Add(new($"{gcc} -O2", gcc, ["-O2", "-Werror"]));
        }
        return configs.ToImmutable();
    }

    private static bool Works(string compiler, string marker)
    {
        try
        {
            var result = Toolchain.Run(compiler, ["--version"], TimeSpan.FromSeconds(20));
            return result.ExitCode == 0 && (result.StandardOutput + result.StandardError).Contains(marker, StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Checks only that the checker accepts the program (fast; for tuning the generator).</summary>
    public static string? Accepts(string program)
    {
        var compilation = Compilation.Create([new SourceText("fuzz.nova", program)], TargetInfo.Lp64);
        return compilation.Diagnostics.HasErrors ? string.Join("\n", compilation.Diagnostics.Items.Take(5)) : null;
    }

    public static Verdict Check(int seed, string? workDirectory = null)
    {
        var program = ProgramGenerator.Generate(seed);
        var compilation = Compilation.Create([new SourceText("fuzz.nova", program)], TargetInfo.Lp64);
        if (compilation.Diagnostics.HasErrors)
        {
            return new Verdict(seed, program, "rejected by the checker:\n" + string.Join("\n", compilation.Diagnostics.Items.Take(10)), null, 0);
        }
        var structured = CBackend.Generate(compilation, requireMain: true, out var nir)!;
        var flat = CBackend.Generate(compilation, requireMain: true, out _, new CEmitOptions(Structured: false))!;

        Outcome reference;
        using (var stdout = new MemoryStream())
        using (var stderr = new MemoryStream())
        {
            var status = NirInterpreter.Run(nir!, compilation.Symbols, new InterpreterOptions
            {
                StandardOutput = stdout,
                StandardError = stderr,
                StandardInput = new MemoryStream(),
                Arguments = ["fuzz"],
            });
            reference = new Outcome(Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), status);
        }
        // A program may exit with status 70 itself: a fault is known by its message.
        if (reference.ExitCode == NirInterpreter.FaultExitStatus && reference.Stderr.Contains("nova interp: fault:", StringComparison.Ordinal))
        {
            return new Verdict(seed, program, $"interpreter fault: {reference.Stderr}", reference, 0);
        }

        var directory = Path.Combine(workDirectory ?? Path.GetTempPath(), $"nova-fuzz-{seed}-{Guid.NewGuid():N}");
        try
        {
            structured.WriteTo(Path.Combine(directory, "structured"));
            flat.WriteTo(Path.Combine(directory, "flat"));
            foreach (var (config, index) in Configs.Select((c, i) => (c, i)))
            {
                var source = Path.Combine(directory, config.Flat ? "flat" : "structured");
                var exe = Path.Combine(directory, $"program-{index}");
                var build = Toolchain.CompileExecutable(config.Compiler, source, config.Flat ? flat : structured, exe, config.Flags);
                if (build.ExitCode != 0)
                {
                    return new Verdict(seed, program, $"[{config.Name}] C compilation failed:\n{build.StandardError}", reference, index);
                }
                ToolResult run;
                try
                {
                    run = Toolchain.Run(exe, ["fuzz"], TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    return new Verdict(seed, program, $"[{config.Name}] timed out (the interpreter finished: {reference})", reference, index);
                }
                var native = new Outcome(run.StandardOutput.ReplaceLineEndings("\n"), run.StandardError.ReplaceLineEndings("\n"), run.ExitCode);
                if (native != reference)
                {
                    return new Verdict(seed, program, $"[{config.Name}] disagrees with the interpreter\nnative:      {native}\ninterpreter: {reference}\n"
                        + FirstDifference(native.Stdout, reference.Stdout), reference, index);
                }
            }
            return new Verdict(seed, program, null, reference, Configs.Length);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            var x = i < la.Length ? la[i] : "<end>";
            var y = i < lb.Length ? lb[i] : "<end>";
            if (x != y)
            {
                return $"first different stdout line {i + 1}: native '{x}', interpreter '{y}'";
            }
        }
        return "stdout equal";
    }
}
