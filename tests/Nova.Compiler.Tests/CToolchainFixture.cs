using System.Collections.Immutable;
using Nova.Compiler.Backend.C;

namespace Nova.Compiler.Tests;

/// <summary>A way of compiling generated C: a compiler plus flags (docs/testing.md §3).</summary>
public sealed record CConfig(string Name, string Compiler, ImmutableArray<string> Flags)
{
    /// <summary>Build the program generated with <c>#line</c> directives (c-backend.md §7).</summary>
    public bool LineDirectives { get; init; }

    /// <summary>Build the flat form (one label per block; c-backend.md §3), the oracle for the structured form.</summary>
    public bool Flat { get; init; }

    /// <summary>Build with M5's placement (stack-placed boxes; c-backend.md §2.2).</summary>
    public bool Placement { get; init; }
}

/// <summary>
/// Discovers the C compilers available on this machine. clang is required. A real GCC
/// (not Apple's clang alias) is used when found. NOVA_TEST_CC adds an extra compiler.
/// </summary>
internal static class CToolchains
{
    private static readonly Lazy<ImmutableArray<CConfig>> s_configs = new(Discover);
    private static readonly string[] s_gccCandidates = ["gcc", "gcc-15", "gcc-14", "gcc-13", "gcc-12"];

    public static ImmutableArray<CConfig> Configs => s_configs.Value;

    /// <summary>Compilers that were looked for but not found; reported in the test log.</summary>
    public static ImmutableArray<string> Missing { get; private set; } = [];

    private static ImmutableArray<CConfig> Discover()
    {
        var configs = ImmutableArray.CreateBuilder<CConfig>();
        var missing = ImmutableArray.CreateBuilder<string>();

        if (!Works("clang", "clang"))
        {
            throw new InvalidOperationException("clang is required to run the C backend tests");
        }
        configs.Add(new("clang -O0", "clang", ["-O0", "-Werror"]));
        configs.Add(new("clang -O2", "clang", ["-O2", "-Werror"]));
        configs.Add(new("clang -O0 -g line-directives", "clang", ["-O0", "-g", "-Werror"]) { LineDirectives = true });
        configs.Add(new("clang -O2 flat", "clang", ["-O2", "-Werror"]) { Flat = true });
        configs.Add(new("clang -O2 placement", "clang", ["-O2", "-Werror"]) { Placement = true });
        configs.Add(new("clang -O2 portable-overflow", "clang", ["-O2", "-Werror", "-DNV_NO_BUILTIN_OVERFLOW"]));
        configs.Add(new("clang -O1 asan+ubsan", "clang",
            ["-O1", "-Werror", "-fsanitize=address,undefined", "-fno-sanitize-recover=all", "-fno-omit-frame-pointer"]));

        var gcc = s_gccCandidates.FirstOrDefault(c => Works(c, "Free Software Foundation"));
        if (gcc is null)
        {
            missing.Add("GCC (no real GCC found; 'gcc' on macOS is clang)");
        }
        else
        {
            configs.Add(new($"{gcc} -O0", gcc, ["-O0", "-Werror"]));
            configs.Add(new($"{gcc} -O2", gcc, ["-O2", "-Werror"]));
            configs.Add(new($"{gcc} -O2 portable-overflow", gcc, ["-O2", "-Werror", "-DNV_NO_BUILTIN_OVERFLOW"]));
        }

        if (Environment.GetEnvironmentVariable("NOVA_TEST_CC") is { Length: > 0 } extra)
        {
            configs.Add(new($"{extra} -O2", extra, ["-O2", "-Werror"]));
        }

        Missing = missing.ToImmutable();
        return configs.ToImmutable();
    }

    private static bool Works(string compiler, string versionMarker)
    {
        try
        {
            var result = Toolchain.Run(compiler, ["--version"], TimeSpan.FromSeconds(20));
            return result.ExitCode == 0 && (result.StandardOutput + result.StandardError).Contains(versionMarker, StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>A fresh temporary directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nova-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a crashed child process may still hold a file briefly.
        }
    }
}
