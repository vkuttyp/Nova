using Nova.Compiler;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Interpreter;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Cli;

internal static class Program
{
    private const string Usage = """
        usage: novac <command> [options] <file.nova>... [<file.c>...] [-- <program arguments>]

        commands:
          parse    lex and parse the files, report syntax errors
          check    parse and type-check the files as one package
          build    compile the package to C, and by default to an executable; .c files are compiled
                   and linked with it (type-system.md §28.6)
          run      run the package with the NIR reference interpreter (--interp)
        options:
          --dump-ast        (parse) print the AST of each file
          --dump-hir        (check) print the typed HIR of the package
          --dump-nir        (build) print the NIR of the package
          --emit c|exe|obj  (build) write C sources (to the -o directory), an executable (default), or one
                            relocatable object (cosmoos/profiles.md §4.3)
          --profile hosted|freestanding
                            (build, check) the profile: hosted (default) or freestanding, with no C library
                            and no operating system (cosmoos/profiles.md §4)
          -o <path>         (build) output file or directory; default: name of the first input
          --cc <compiler>   (build) C compiler for --emit exe; default: $CC or cc
          --cflag <flag>    (build) extra flag for the C compiler (repeatable)
          --ld <linker>     (build --emit obj) join the objects with '<linker> -r' (e.g. ld.lld for ELF
                            cross builds) instead of the C compiler
          --line-directives (build) map the generated C to Nova lines with #line
          -g                (build) debug build: #line directives, C compiled with -g -O0
          --flat-c          (build) emit the flat form: one label per block, no if/loop reconstruction
          --interp          (run) interpret the checked NIR (intermediate-representation.md §5)
          --placement       (build, run) place non-escaping SystemHeap boxes on the stack (M5)
          --explain-allocations  (build) print where every allocation site's memory comes from
        """;

    private sealed class Options
    {
        public List<string> Files { get; } = [];

        /// <summary>C source files given on the command line, by file name.</summary>
        public List<(string Name, string Content)> CSources { get; } = [];
        public bool Dump { get; set; }
        public string Emit { get; set; } = "exe";

        /// <summary>--ld: joins the objects of --emit obj (cosmoos/profiles.md §4.3).</summary>
        public string? Linker { get; set; }

        /// <summary>--profile (cosmoos/profiles.md §4).</summary>
        public Profile Profile { get; set; } = Profile.Hosted;
        public string? Output { get; set; }
        public string Compiler { get; set; } = Environment.GetEnvironmentVariable("CC") is { Length: > 0 } cc ? cc : "cc";
        public List<string> CFlags { get; } = ["-O2"];
        public bool LineDirectives { get; set; }
        public bool Flat { get; set; }
        public bool Interpret { get; set; }
        public bool Placement { get; set; }
        public bool ExplainAllocations { get; set; }
        public List<string> ProgramArguments { get; } = [];
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Out.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        if (args[0] is "--version")
        {
            Console.Out.WriteLine($"novac {NovaVersion.Text}");
            return 0;
        }

        var command = args[0];
        if (command is not ("parse" or "check" or "build" or "run"))
        {
            return Fail($"unknown command '{command}'");
        }
        if (ParseOptions(command, args[1..]) is not { } options)
        {
            return 2;
        }

        var sources = new List<SourceText>();
        foreach (var file in options.Files)
        {
            try
            {
                if (file.EndsWith(".c", StringComparison.Ordinal))
                {
                    // C sources go to the C compiler with the generated code (type-system.md §28.6).
                    if (options.CSources.Any(c => c.Name == Path.GetFileName(file)))
                    {
                        return Fail($"two C files are named '{Path.GetFileName(file)}'");
                    }
                    options.CSources.Add((Path.GetFileName(file), File.ReadAllText(file)));
                    continue;
                }
                sources.Add(new SourceText(SourceText.SourcePath(file, Environment.CurrentDirectory), File.ReadAllText(file)));
            }
            catch (IOException e)
            {
                return Fail($"cannot read '{file}': {e.Message}");
            }
        }

        if (sources.Count == 0)
        {
            return Fail("no Nova input files");
        }
        return command switch
        {
            "parse" => Report(Parse(sources, options.Dump)),
            "check" => Report(Check(sources, options.Dump, options.Profile)),
            "run" => Run(sources, options),
            _ => Build(sources, options),
        };
    }

    private static Options? ParseOptions(string command, string[] args)
    {
        var options = new Options();
        var dumpOption = command switch { "parse" => "--dump-ast", "check" => "--dump-hir", _ => "--dump-nir" };
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Value()
            {
                if (i + 1 < args.Length)
                {
                    return args[++i];
                }
                Fail($"option '{arg}' needs a value");
                return null;
            }

            if (arg == "--" && command == "run")
            {
                options.ProgramArguments.AddRange(args[(i + 1)..]);
                break;
            }
            if (command == "run" && arg == "--interp")
            {
                options.Interpret = true;
            }
            else if (command is "build" or "run" && arg == "--placement")
            {
                options.Placement = true;
            }
            else if (command == "build" && arg == "--explain-allocations")
            {
                options.ExplainAllocations = true;
            }
            else if (arg == dumpOption)
            {
                options.Dump = true;
            }
            else if (command == "build" && arg == "--line-directives")
            {
                options.LineDirectives = true;
            }
            else if (command == "build" && arg == "--flat-c")
            {
                options.Flat = true;
            }
            else if (command == "build" && arg == "-g")
            {
                options.LineDirectives = true;
                options.CFlags.Remove("-O2");
                options.CFlags.InsertRange(0, ["-g", "-O0"]);
            }
            else if (command is "build" or "check" && arg == "--profile")
            {
                switch (Value())
                {
                    case null: return null;
                    case "hosted": options.Profile = Profile.Hosted; break;
                    case "freestanding": options.Profile = Profile.Freestanding; break;
                    case var other: Fail($"--profile must be 'hosted' or 'freestanding', not '{other}'"); return null;
                }
            }
            else if (command == "build" && arg is "--emit" or "-o" or "--cc" or "--cflag" or "--ld")
            {
                if (Value() is not { } value)
                {
                    return null;
                }
                switch (arg)
                {
                    case "--emit" when value is "c" or "exe" or "obj": options.Emit = value; break;
                    case "--emit": Fail($"--emit must be 'c', 'exe' or 'obj', not '{value}'"); return null;
                    case "-o": options.Output = value; break;
                    case "--cc": options.Compiler = value; break;
                    case "--ld": options.Linker = value; break;
                    default: options.CFlags.Add(value); break;
                }
            }
            else if (arg.StartsWith('-'))
            {
                Fail($"unknown option '{arg}'");
                return null;
            }
            else
            {
                options.Files.Add(arg);
            }
        }
        if (options.Files.Count == 0)
        {
            Fail("no input files");
            return null;
        }
        if (command == "run" && !options.Interpret)
        {
            Fail("'run' needs --interp (the only way to run without building is the interpreter)");
            return null;
        }
        return options;
    }

    private static int Report(DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in diagnostics.Items)
        {
            Console.Error.Write(DiagnosticRenderer.Render(diagnostic));
        }
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static DiagnosticBag Parse(List<SourceText> sources, bool dumpAst)
    {
        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        foreach (var source in sources)
        {
            var unit = Parser.ParseText(source, symbols, diagnostics);
            if (dumpAst)
            {
                Console.Out.Write(AstPrinter.Print(unit, symbols));
            }
        }
        return diagnostics;
    }

    private static DiagnosticBag Check(List<SourceText> sources, bool dumpHir, Profile profile)
    {
        var compilation = Compilation.Create(sources, TargetInfo.Lp64, profile);
        if (dumpHir && compilation.Hir is not null)
        {
            Console.Out.Write(HirPrinter.Print(compilation.Hir, compilation.Symbols));
        }
        return compilation.Diagnostics;
    }

    private static int Build(List<SourceText> sources, Options options)
    {
        var compilation = Compilation.Create(sources, TargetInfo.Lp64, options.Profile);
        if (compilation.Diagnostics.HasErrors)
        {
            return Report(compilation.Diagnostics);
        }

        // With C files, `main` may be in C (type-system.md §28.6).
        var program = CBackend.Generate(compilation,
            requireMain: options.Emit == "exe" && options.CSources.Count == 0 && options.Profile == Profile.Hosted, out var nir,
            new CEmitOptions(LineDirectives: options.LineDirectives, Structured: !options.Flat, Placement: options.Placement))
            ?.WithCSources(options.CSources);
        if (program is null)
        {
            return Report(compilation.Diagnostics);
        }
        if (options.Dump && nir is not null)
        {
            Console.Out.Write(NirPrinter.Print(nir, compilation.Symbols));
        }
        if (options.ExplainAllocations && nir is not null)
        {
            // The decisions --placement would act on (memory-model.md §5, rule 4).
            Console.Out.Write(Nova.Compiler.Analysis.PlacementAnalysis.Analyze(nir, compilation.Symbols).Explain());
        }

        var defaultName = Path.GetFileNameWithoutExtension(sources[0].Path);
        if (options.Emit == "c")
        {
            var directory = options.Output ?? defaultName + "-c";
            program.WriteTo(directory);
            return 0;
        }

        var output = options.Output ?? (options.Emit == "obj" ? defaultName + ".o" : defaultName);
        var workDirectory = Path.Combine(Path.GetTempPath(), "novac-" + Guid.NewGuid().ToString("N"));
        try
        {
            program.WriteTo(workDirectory);
            // Run in the current directory: the source paths in #line directives are relative to it (c-backend.md §7).
            var result = options.Emit == "obj"
                ? Toolchain.CompileObject(options.Compiler, workDirectory, program, Path.GetFullPath(output), options.CFlags,
                    workingDirectory: Environment.CurrentDirectory, linker: options.Linker)
                : Toolchain.CompileExecutable(options.Compiler, workDirectory, program, output, options.CFlags,
                    workingDirectory: Environment.CurrentDirectory);
            Console.Error.Write(result.StandardOutput);
            Console.Error.Write(result.StandardError);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine($"novac: the C compiler '{options.Compiler}' failed (exit code {result.ExitCode})");
                return 1;
            }
            return 0;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return Fail($"cannot run the C compiler '{options.Compiler}': {e.Message}");
        }
        finally
        {
            Directory.Delete(workDirectory, recursive: true);
        }
    }

    /// <summary>Interprets the package's NIR (intermediate-representation.md §5) with the process's own streams and environment.</summary>
    private static int Run(List<SourceText> sources, Options options)
    {
        var compilation = Compilation.Create(sources, TargetInfo.Lp64);
        if (compilation.Diagnostics.HasErrors)
        {
            return Report(compilation.Diagnostics);
        }
        if (CBackend.Generate(compilation, requireMain: true, out var nir) is null || nir is null)
        {
            return Report(compilation.Diagnostics);
        }
        using var output = new BufferedStream(Console.OpenStandardOutput());
        using var errors = Console.OpenStandardError();
        return NirInterpreter.Run(nir, compilation.Symbols, new InterpreterOptions
        {
            StandardOutput = output,
            StandardError = errors,
            Arguments = [sources[0].Path, .. options.ProgramArguments],
            FailAllocationAt = Config("NOVA_FAIL_ALLOC_AT"),
            AllocationReport = Config("NOVA_ALLOC_REPORT") != 0,
            InheritStandardStreams = true,
            Placement = options.Placement,
        });
    }

    /// <summary>A number from the environment, as nova_platform_config reads it: leading digits, else 0.</summary>
    private static ulong Config(string name)
    {
        ulong value = 0;
        foreach (var c in Environment.GetEnvironmentVariable(name) ?? "")
        {
            if (c is < '0' or > '9')
            {
                break;
            }
            value = value * 10 + (ulong)(c - '0');
        }
        return value;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"novac: {message}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
