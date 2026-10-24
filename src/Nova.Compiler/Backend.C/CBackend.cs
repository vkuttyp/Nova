using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Nir;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Backend.C;

/// <summary>A generated C program: the main translation unit plus the runtime files it needs.</summary>
/// <param name="Files">Relative path → content. Compile every <c>.c</c> file with <c>-I</c> on the output root.</param>
public sealed record CProgram(ImmutableArray<KeyValuePair<string, string>> Files)
{
    public const string MainFileName = "main.c";

    /// <summary>The C declarations of the program's exports and @repr types (c-backend.md §2.3).</summary>
    public const string ExportsHeaderName = "nova_exports.h";

    /// <summary>Adds C source files (type-system.md §28.6) under <c>c/</c>, compiled with the generated C.</summary>
    public CProgram WithCSources(IEnumerable<(string Name, string Content)> sources) =>
        new([.. Files, .. sources.Select(s => new KeyValuePair<string, string>("c/" + s.Name, s.Content))]);

    public IEnumerable<string> SourceFiles => Files.Select(f => f.Key).Where(k => k.EndsWith(".c", StringComparison.Ordinal));

    public void WriteTo(string directory)
    {
        foreach (var (path, content) in Files)
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }
}

public static class CBackend
{
    /// <summary>Runtime files embedded in this assembly (see Nova.Compiler.csproj), keyed by output path.</summary>
    private static readonly ImmutableArray<KeyValuePair<string, string>> HostedRuntime =
    [
        new("nova/rt.h", ReadResource("runtime/include/nova/rt.h")),
        new("nova_rt_core.c", ReadResource("runtime/core/nova_rt_core.c")),
        new("nova_rt_hosted.c", ReadResource("runtime/hosted/nova_rt_hosted.c")),
        new("nova_rt_io.c", ReadResource("runtime/hosted/nova_rt_io.c")),
    ];

    /// <summary>
    /// Lowers a successfully checked compilation to C. Reports N0300 (into the compilation's
    /// diagnostics) and returns null if an executable is required but there is no `main`.
    /// </summary>
    public static CProgram? Generate(Compilation compilation, bool requireMain, out NirProgram? nir, CEmitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        nir = null;
        if (compilation.Hir is null || compilation.Diagnostics.HasErrors)
        {
            throw new InvalidOperationException("cannot generate code for a compilation with errors");
        }

        var hasMain = compilation.Hir.Functions.Any(f => f.Owner is null && compilation.Symbols.GetName(f.Name) == "main");
        if (requireMain && !hasMain)
        {
            var source = compilation.Units[0].Source;
            compilation.Diagnostics.Error(DiagnosticCodes.MissingMain, source, new TextSpan(0, 0),
                "an executable needs a 'main' function: fn main() -> i32 { ... }");
            return null;
        }

        nir = compilation.Nir ?? throw new InvalidOperationException("the compilation has no NIR");
        var mainSource = CEmitter.Emit(nir, compilation.Symbols, compilation.Types, compilation.Units.Select(u => u.Source), options);
        var header = CEmitter.ExportsHeader(nir, compilation.Symbols, compilation.Types);
        return new CProgram([new(CProgram.MainFileName, mainSource), .. HostedRuntime,
            .. header is null ? Array.Empty<KeyValuePair<string, string>>() : [new(CProgram.ExportsHeaderName, header)]]);
    }

    /// <summary>The runtime header plus the given C files, without the core or platform runtime (for tests and embedders).</summary>
    public static CProgram RuntimeOnly(params (string Path, string Content)[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return new CProgram([HostedRuntime[0], .. files.Select(f => new KeyValuePair<string, string>(f.Path, f.Content))]);
    }

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded runtime file '{name}'");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }
}

/// <summary>Result of running an external tool.</summary>
public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs a C compiler or a built program.</summary>
public static class Toolchain
{
    /// <summary>Default warning set: generated code must compile cleanly under it (docs/c-backend.md §1).</summary>
    public static readonly ImmutableArray<string> StrictWarnings =
    [
        "-std=c11", "-Wall", "-Wextra", "-Wpedantic", "-Wshadow", "-Wconversion", "-Wsign-conversion",
        "-Wmissing-prototypes", "-Wstrict-prototypes", "-Wvla", "-Wcast-qual", "-Wundef", "-Wimplicit-fallthrough",
    ];

    /// <param name="workingDirectory">Where the compiler runs; source paths in <c>#line</c> directives resolve against it (c-backend.md §7).</param>
    public static ToolResult CompileExecutable(string compiler, string directory, CProgram program, string output, IEnumerable<string> extraFlags,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        var args = new List<string>(StrictWarnings) { "-I", directory };
        args.AddRange(extraFlags);
        args.AddRange(program.SourceFiles.Select(f => Path.Combine(directory, f)));
        args.AddRange(["-o", output]);
        return Run(compiler, args, workingDirectory: workingDirectory);
    }

    /// <summary>
    /// At most one running tool per core. Test suites start tools from many threads at once; more
    /// than the machine can run only slows every tool down (and starved the runtime's process
    /// bookkeeping on small CI machines).
    /// </summary>
    private static readonly SemaphoreSlim s_toolSlots = new(Environment.ProcessorCount, Environment.ProcessorCount);

    public static ToolResult Run(string fileName, IEnumerable<string> arguments, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, byte[]? standardInput = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            // Nova programs write UTF-8; the default would be the console's code page on Windows.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        var limit = timeout ?? TimeSpan.FromMinutes(2);
        s_toolSlots.Wait();
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"cannot start '{fileName}'");
            if (standardInput is not null)
            {
                process.StandardInput.BaseStream.Write(standardInput);
                process.StandardInput.Close();
            }
            // Dedicated reader threads: async reads would need thread-pool threads, which blocked callers may hold.
            var stdout = ReadOnOwnThread(process.StandardOutput);
            var stderr = ReadOnOwnThread(process.StandardError);
            if (!process.WaitForExit(limit))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"'{fileName}' did not finish in {limit.TotalSeconds} s");
            }
            // The output ends when every process holding the pipes exits; a leftover child must not hang the caller.
            if (!Task.WaitAll([stdout, stderr], limit))
            {
                throw new TimeoutException($"'{fileName}' exited, but its output did not end within {limit.TotalSeconds} s");
            }
            return new ToolResult(process.ExitCode, stdout.Result, stderr.Result);
        }
        finally
        {
            s_toolSlots.Release();
        }
    }

    private static Task<string> ReadOnOwnThread(StreamReader reader) =>
        Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
