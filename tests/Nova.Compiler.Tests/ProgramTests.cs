using System.Text;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// The C3 programs on the host (docs/cosmoos/c3-report.md): integrations/cosmoos/c3/c3-io.nova (arguments,
/// files, loopback TCP) and programs/nwc (BusyBox-compatible `wc`). In CosmoOS, c3.py compares nwc with
/// BusyBox itself; here the expected results are BusyBox's, recorded on CosmoOS 1.0.10 (2026-10-23).
/// </summary>
public class ProgramTests
{
    private static readonly string Root = TestHelpers.RepoRoot();

    private static string Build(string source, TempDirectory temp)
    {
        var text = File.ReadAllText(source);
        var compilation = Compilation.Create([new SourceText(Path.GetFileName(source), text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors, string.Join("\n", compilation.Diagnostics.Items));
        var program = CBackend.Generate(compilation, requireMain: true, out _)!;
        var gen = Path.Combine(temp.Path, "gen");
        program.WriteTo(gen);
        var config = CToolchains.Configs.Single(c => c.Name.Contains("asan", StringComparison.Ordinal));
        var exe = Path.Combine(temp.Path, "program");
        var build = Toolchain.CompileExecutable(config.Compiler, gen, program, exe, config.Flags);
        Assert.True(build.ExitCode == 0, build.StandardError);
        return exe;
    }

    [Fact]
    public void C3_io_program_uses_arguments_files_and_loopback_tcp()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(Root, "integrations", "cosmoos", "c3", "c3-io.nova");
        var exe = Build(source, temp);
        var expected = string.Concat(File.ReadAllLines(source)
            .Where(l => l.StartsWith("// expect-stdout: ", StringComparison.Ordinal))
            .Select(l => l["// expect-stdout: ".Length..] + "\n"));
        var run = Toolchain.Run(exe, ["first", "second"], TimeSpan.FromSeconds(30), workingDirectory: temp.Path);
        Assert.Equal("", run.StandardError);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(expected, run.StandardOutput);
        Assert.False(File.Exists(Path.Combine(temp.Path, "c3-test.txt"))); // removed by the program
    }

    public static TheoryData<string, string?, string, string, int> NwcCases() => new()
    {
        { "x", null, "        2         5        10 x\n", "", 0 },
        { "-l x", null, "2 x\n", "", 0 },
        { "-w -c x", null, "        5        10 x\n", "", 0 },
        { "", "x", "        2         5        10\n", "", 0 },
        { "x y z", null, "        2         5        10 x\n        1         1         4 y\n        0         2        10 z\n        3         8        24 total\n", "", 0 },
        { "-l x y", null, "        2 x\n        1 y\n        3 total\n", "", 0 },
        { "nosuch", null, "", "wc: nosuch: No such file or directory", 1 },
        { "-c -m -L -l -w t", null, "        3         5        28        30        11 t\n", "", 0 },
        { "-lw t", null, "        3         5 t\n", "", 0 },
        { "-", "t", "        3         5        30 -\n", "", 0 },
        { "d", null, "        0         0         0 d\n", "wc: d: Is a directory", 1 },
        { "-q t", null, "", "wc: unrecognized option: q", 1 },
        { "-w w", null, "6 w\n", "", 0 },
        { "-m -c m", null, "        7        13 m\n", "", 0 },
        { "-L m", null, "1 m\n", "", 0 },
        { "-L tab", null, "9 tab\n", "", 0 },
        { "-L tab2", null, "17 tab2\n", "", 0 },
    };

    [Theory]
    [MemberData(nameof(NwcCases))]
    public void Nwc_matches_busybox_wc(string arguments, string? input, string stdout, string firstErrorLine, int status)
    {
        using var temp = new TempDirectory();
        var exe = Build(Path.Combine(Root, "programs", "nwc", "nwc.nova"), temp);
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(Path.Combine(data, "d"));
        foreach (var (name, content) in new[]
        {
            ("x", "a b\nc d e\n"), ("y", "one\n"), ("z", "no newline"), ("t", "a\tb\n12345678901\nhéllo wörld\n"),
            ("w", "  a  b\t\tc\r\nd\fe\vf"), ("m", "é €\n🙂x\n"), ("tab", "x\ty\n"), ("tab2", "abcdefgh\tz\n"),
        })
        {
            File.WriteAllBytes(Path.Combine(data, name), Encoding.UTF8.GetBytes(content));
        }
        var run = Toolchain.Run(exe, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), TimeSpan.FromSeconds(30),
            workingDirectory: data, standardInput: input is null ? [] : File.ReadAllBytes(Path.Combine(data, input)));
        Assert.Equal(stdout, run.StandardOutput);
        Assert.Equal(firstErrorLine, run.StandardError.Split('\n')[0]);
        Assert.Equal(status, run.ExitCode);
    }
}
