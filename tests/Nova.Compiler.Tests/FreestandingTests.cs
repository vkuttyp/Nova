using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// The freestanding profile (cosmoos/profiles.md §4): tests/corpus/freestanding/NAME.nova is built into one relocatable
/// object whose undefined symbols are only the allowlist (§4.4), for the host and both CosmoOS triples; on the host,
/// its C half NAME.c (an environment: nova_panic, main) links with the object and runs to the file's expectations.
/// </summary>
public class FreestandingTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    /// <summary>§4.4: what a freestanding object may leave undefined, besides its own extern "C" functions.</summary>
    private static readonly string[] s_allowlist = ["nova_panic", "memcpy", "memmove", "memset", "memcmp"];

    private static IEnumerable<string> FileList() =>
        Directory.GetFiles(Path.Combine(CorpusRoot, "freestanding"), "*.nova").Order(StringComparer.Ordinal)
            .Select(file => Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));

    public static TheoryData<string> Files() => new(FileList());

    private static (Compilation Compilation, CProgram Program) Generate(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var compilation = Compilation.Create([new SourceText(relativePath, text)], TargetInfo.Lp64, Profile.Freestanding);
        Assert.False(compilation.Diagnostics.HasErrors, string.Join("\n", compilation.Diagnostics.Items));
        var program = CBackend.Generate(compilation, requireMain: false, out _)!;
        Assert.Equal(Profile.Freestanding, program.Profile);
        Assert.DoesNotContain(program.Files, f => f.Key.Contains("hosted", StringComparison.Ordinal) || f.Key.Contains("_io", StringComparison.Ordinal));
        return (compilation, program);
    }

    /// <summary>llvm-nm if there is one (it reads Mach-O and ELF alike), else the system's nm.</summary>
    private static readonly Lazy<string> s_nm = new(() =>
        new[] { "llvm-nm", "llvm-nm-20", "llvm-nm-19", "llvm-nm-18", "llvm-nm-17" }.FirstOrDefault(Works) ?? "nm");

    private static readonly Lazy<string?> s_lld = new(() => new[] { "ld.lld", "ld.lld-20", "ld.lld-19", "ld.lld-18" }.FirstOrDefault(Works));

    private static bool Works(string tool)
    {
        try
        {
            return Toolchain.Run(tool, ["--version"], TimeSpan.FromSeconds(30)).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Symbols the objects leave undefined, minus those one of them defines (as a relocatable join would).</summary>
    private static SortedSet<string> Undefined(IEnumerable<string> objects)
    {
        var undefined = new SortedSet<string>(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            var listing = Toolchain.Run(s_nm.Value, [obj]);
            Assert.True(listing.ExitCode == 0, listing.StandardError);
            foreach (var line in listing.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var (kind, name) = (parts[^2], parts[^1].TrimStart('_')); // Mach-O prefixes C names with '_'
                if (kind == "U")
                {
                    undefined.Add(name);
                }
                else
                {
                    defined.Add(name);
                }
            }
        }
        undefined.ExceptWith(defined);
        return undefined;
    }

    private static void AssertAllowed(string relativePath, Compilation compilation, SortedSet<string> undefined, string where)
    {
        var externs = compilation.Hir!.Functions.Where(f => f.IsExtern).Select(f => compilation.Symbols.GetName(f.Name));
        var extra = undefined.Except(s_allowlist).Except(externs).ToList();
        Assert.True(extra.Count == 0, $"{relativePath} ({where}) leaves undefined: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Host_object_has_only_allowlisted_undefined_symbols_and_runs(string relativePath)
    {
        var (compilation, program) = Generate(relativePath);
        using var temp = new TempDirectory();
        program.WriteTo(temp.Path);
        var obj = Path.Combine(temp.Path, "package.o");
        var built = Toolchain.CompileObject("clang", temp.Path, program, obj, ["-O2", "-Werror"]);
        Assert.True(built.ExitCode == 0, built.StandardError);
        AssertAllowed(relativePath, compilation, Undefined([obj]), "host");

        // The environment: a C file that defines nova_panic and main, linked with the object (hosted, so it has libc).
        var harness = Path.ChangeExtension(Path.Combine(CorpusRoot, relativePath), ".c");
        var exe = Path.Combine(temp.Path, "environment");
        var linked = Toolchain.Run("clang", ["-std=c11", "-Wall", "-Werror", "-I", temp.Path, harness, obj, "-o", exe]);
        Assert.True(linked.ExitCode == 0, linked.StandardError);
        var run = Toolchain.Run(exe, [], TimeSpan.FromSeconds(30));
        var expected = RunCorpusTests.ParseExpectations(File.ReadAllText(Path.Combine(CorpusRoot, relativePath)));
        Assert.Equal(expected.Stdout, run.StandardOutput.ReplaceLineEndings("\n"));
        Assert.Equal(expected.ExitCode, run.ExitCode);
        if (expected.PanicReason is { } reason)
        {
            Assert.StartsWith($"nova panic: {reason} at {relativePath}:{expected.PanicLine}:", run.StandardError, StringComparison.Ordinal);
        }
    }

    /// <summary>The CosmoOS triples (cross-compiled; nothing runs): each file compiled freestanding, then the allowlist.</summary>
    [Theory]
    [InlineData("x86_64-unknown-none-elf")]
    [InlineData("aarch64-unknown-none-elf")]
    public void CosmoOS_triple_objects_have_only_allowlisted_undefined_symbols(string triple)
    {
        var resourceDir = Toolchain.Run("clang", ["-print-resource-dir"]).StandardOutput.Trim();
        string[] flags = [$"--target={triple}", "-nostdinc", "-isystem", Path.Combine(resourceDir, "include"), "-fno-pic", "-fno-pie", "-O2", "-Werror"];
        foreach (var relativePath in FileList())
        {
            var (compilation, program) = Generate(relativePath);
            using var temp = new TempDirectory();
            program.WriteTo(temp.Path);
            var objects = new List<string>();
            foreach (var file in program.SourceFiles)
            {
                var obj = Path.Combine(temp.Path, file + ".o");
                var compiled = Toolchain.Run("clang", [.. Toolchain.StrictWarnings, .. program.ProfileFlags, .. flags, "-I", temp.Path,
                    "-c", Path.Combine(temp.Path, file), "-o", obj]);
                Assert.True(compiled.ExitCode == 0, $"{relativePath} {file}:\n{compiled.StandardError}");
                objects.Add(obj);
            }
            AssertAllowed(relativePath, compilation, Undefined(objects), triple);

            // With ld.lld, the one object that --emit obj --ld ld.lld makes (as CosmoOS links), and its sections.
            if (s_lld.Value is { } lld)
            {
                var joined = Path.Combine(temp.Path, "package.o");
                var built = Toolchain.CompileObject("clang", temp.Path, program, joined, flags, linker: lld);
                Assert.True(built.ExitCode == 0, built.StandardError);
                AssertAllowed(relativePath, compilation, Undefined([joined]), triple + " joined");
            }
        }
    }

    [Fact]
    public void Hosted_programs_are_unchanged()
    {
        // The hosted profile is the default: the same program gets the hosted runtime and a C main.
        var compilation = Compilation.Create([new SourceText("t.nova", "fn main() -> i32 { return 0; }")], TargetInfo.Lp64);
        var program = CBackend.Generate(compilation, requireMain: true, out _)!;
        Assert.Equal(Profile.Hosted, program.Profile);
        Assert.Contains(program.Files, f => f.Key == "nova_rt_hosted.c");
        Assert.Empty(program.ProfileFlags);
    }
}
