using Nova.Compiler.Backend.C;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

public class BackendTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> RunPassFiles() => RunCorpusTests.RunPassFiles();

    public static TheoryData<string> EmitCFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, "emit-c"), "*.nova").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
        }
        return data;
    }

    private static (CProgram Program, NirProgram Nir, Compilation Compilation) Generate(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors);
        var program = CBackend.Generate(compilation, requireMain: false, out var nir);
        return (program!, nir!, compilation);
    }

    private static string MainC(CProgram program) => program.Files.Single(f => f.Key == CProgram.MainFileName).Value;

    /// <summary>Reproducible builds: generated C depends only on the input (docs/cosmoos/build-integration.md §6).</summary>
    [Theory]
    [MemberData(nameof(RunPassFiles))]
    public void Generated_C_is_deterministic_and_contains_no_host_paths(string relativePath)
    {
        var first = MainC(Generate(relativePath).Program);
        var second = MainC(Generate(relativePath).Program);
        Assert.Equal(first, second);
        Assert.DoesNotContain(TestHelpers.RepoRoot(), first, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), first, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RunPassFiles))]
    public void Nir_printer_handles_every_program(string relativePath)
    {
        var (_, nir, compilation) = Generate(relativePath);
        Assert.Contains("fn main", NirPrinter.Print(nir, compilation.Symbols), StringComparison.Ordinal);
    }

    /// <summary>Reviewed snapshots of generated C, so that changes to the C shape show up in review.</summary>
    [Theory]
    [MemberData(nameof(EmitCFiles))]
    public void Emit_c_snapshot(string relativePath)
    {
        var actual = MainC(Generate(relativePath).Program);
        var snapshot = Path.ChangeExtension(Path.Combine(CorpusRoot, relativePath), ".c");
        if (Environment.GetEnvironmentVariable("NOVA_BLESS") == "1")
        {
            File.WriteAllText(snapshot, actual);
            return;
        }
        Assert.True(File.Exists(snapshot), $"missing snapshot {snapshot}; run with NOVA_BLESS=1 and review it");
        Assert.Equal(File.ReadAllText(snapshot).ReplaceLineEndings("\n"), actual);
    }

    /// <summary>
    /// Generated C and the core runtime must compile freestanding (no libc headers) for both CosmoOS
    /// triples. These are generic freestanding flags; exact CosmoOS flags are checked in C1.
    /// </summary>
    [Theory]
    [InlineData("x86_64-unknown-none-elf")]
    [InlineData("aarch64-unknown-none-elf")]
    public void Generated_C_compiles_freestanding_for_cosmoos_triples(string triple)
    {
        var resourceDir = Toolchain.Run("clang", ["-print-resource-dir"]).StandardOutput.Trim();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var files = Directory.GetFiles(Path.Combine(CorpusRoot, "run-pass"), "*.nova")
            .Select(f => Path.GetRelativePath(CorpusRoot, f).Replace('\\', '/'));
        Parallel.ForEach(files, relativePath =>
        {
            var program = Generate(relativePath).Program;
            using var temp = new TempDirectory();
            program.WriteTo(temp.Path);
            foreach (var file in new[] { CProgram.MainFileName, "nova_rt_core.c" })
            {
                var args = new List<string>(Toolchain.StrictWarnings)
                {
                    $"--target={triple}", "-ffreestanding", "-nostdinc", "-isystem", Path.Combine(resourceDir, "include"),
                    "-fno-pic", "-fno-pie", "-fno-stack-protector", "-O2", "-Werror",
                    "-I", temp.Path, "-c", Path.Combine(temp.Path, file), "-o", Path.Combine(temp.Path, file + ".o"),
                };
                var result = Toolchain.Run("clang", args);
                if (result.ExitCode != 0)
                {
                    failures.Add($"{relativePath} ({file}):\n{result.StandardError}");
                }
            }
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures));
    }
}
