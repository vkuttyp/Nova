using System.Text.RegularExpressions;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>Source maps (c-backend.md §7): <c>#line</c> directives, debug line tables and source paths.</summary>
public partial class SourceMapTests
{
    private const string Program = """
        fn gcd(a: u64, b: u64) -> u64 {
            var x = a;
            var y = b;
            while (y != 0) {
                let t = x % y;
                x = y;
                y = t;
            }
            return x;
        }

        fn main() -> i32 {
            let g = gcd(1071, 462);
            Console.writeLine(g);
            return 0;
        }
        """;

    private static CProgram Generate(string path, string text, bool lineDirectives)
    {
        var compilation = Compilation.Create([new SourceText(path, text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors);
        return CBackend.Generate(compilation, requireMain: true, out _, new CEmitOptions(lineDirectives))!;
    }

    /// <summary>
    /// The debug line table clang builds from the directives names the Nova file and every statement line,
    /// and no line outside it. Read from the assembler's <c>.file</c>/<c>.loc</c> directives, so no DWARF tool is needed.
    /// </summary>
    [Fact]
    public void Debug_line_table_maps_to_nova_lines()
    {
        using var temp = new TempDirectory();
        Generate("demo/gcd.nova", Program, lineDirectives: true).WriteTo(temp.Path);
        var assembly = Toolchain.Run("clang", ["-std=c11", "-g", "-O0", "-S", "-I", ".", "main.c", "-o", "-"], workingDirectory: temp.Path);
        Assert.True(assembly.ExitCode == 0, assembly.StandardError);

        var files = new Dictionary<string, string>();
        foreach (Match match in FileDirective().Matches(assembly.StandardOutput))
        {
            var name = match.Groups[3].Success ? $"{match.Groups[2].Value}/{match.Groups[3].Value}" : match.Groups[2].Value;
            files[match.Groups[1].Value] = name;
        }
        var nova = files.Where(f => f.Value == "demo/gcd.nova").Select(f => f.Key).ToHashSet();
        Assert.NotEmpty(nova);

        var lines = LocDirective().Matches(assembly.StandardOutput)
            .Where(m => nova.Contains(m.Groups[1].Value))
            .Select(m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();
        int[] statements = [1, 2, 3, 4, 5, 6, 7, 9, 12, 13, 14, 15];
        Assert.All(statements, line => Assert.Contains(line, lines));
        Assert.All(lines, line => Assert.InRange(line, 1, 16));
        Assert.DoesNotContain(10, lines); // the blank line between the functions
    }

    /// <summary>Directives hand the rest of the file back to main.c at its true line numbers.</summary>
    [Fact]
    public void Main_c_line_numbers_resume_correctly()
    {
        var mainC = Generate("demo/gcd.nova", Program, lineDirectives: true).Files.Single(f => f.Key == CProgram.MainFileName).Value;
        var lines = mainC.Split('\n');
        var handBacks = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (MainDirective().Match(lines[i]) is { Success: true } match)
            {
                Assert.Equal(i + 2, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                handBacks++;
            }
        }
        Assert.Equal(2, handBacks); // one per function
    }

    /// <summary>Generated C contains no host path with directives on either (build-integration.md §6).</summary>
    [Theory]
    [MemberData(nameof(BackendTests.RunPassFiles), MemberType = typeof(BackendTests))]
    public void Directives_are_deterministic_and_contain_no_host_paths(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus", relativePath));
        string MainC() => Generate(relativePath, text, lineDirectives: true).Files.Single(f => f.Key == CProgram.MainFileName).Value;
        var first = MainC();
        Assert.Equal(first, MainC());
        Assert.Contains($"#line ", first, StringComparison.Ordinal);
        Assert.DoesNotContain(TestHelpers.RepoRoot(), first, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.nova", "/work", "a.nova")]
    [InlineData("dir/a.nova", "/work", "dir/a.nova")]
    [InlineData("/work/dir/a.nova", "/work", "dir/a.nova")]
    [InlineData("/elsewhere/a.nova", "/work", "a.nova")]
    [InlineData("/work-other/a.nova", "/work", "a.nova")]
    [InlineData("../up/a.nova", "/work", "../up/a.nova")]
    public void Source_paths_never_contain_host_paths(string path, string currentDirectory, string expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the cases use POSIX roots
        }
        Assert.Equal(expected, SourceText.SourcePath(path, currentDirectory));
    }

    [GeneratedRegex("""^\s*\.file\s+(\d+)\s+"([^"]*)"(?:\s+"([^"]*)")?""", RegexOptions.Multiline)]
    private static partial Regex FileDirective();

    [GeneratedRegex(@"^\s*\.loc\s+(\d+)\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex LocDirective();

    [GeneratedRegex("""^#line (\d+) "main\.c"$""")]
    private static partial Regex MainDirective();
}
