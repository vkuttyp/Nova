using Nova.Compiler;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Cli;

internal static class Program
{
    private const string Usage = """
        usage: novac <command> [options] <file.nova>...

        commands:
          parse    lex and parse the files, report syntax errors
          check    parse and type-check the files as one package
          build    compile the package to C, and by default to an executable
        options:
          --dump-ast        (parse) print the AST of each file
          --dump-hir        (check) print the typed HIR of the package
          --dump-nir        (build) print the NIR of the package
          --emit c|exe      (build) write C sources (to the -o directory) or an executable (default)
          -o <path>         (build) output file or directory; default: name of the first input
          --cc <compiler>   (build) C compiler for --emit exe; default: $CC or cc
          --cflag <flag>    (build) extra flag for the C compiler (repeatable)
          --line-directives (build) map the generated C to Nova lines with #line
          -g                (build) debug build: #line directives, C compiled with -g -O0
        """;

    private sealed class Options
    {
        public List<string> Files { get; } = [];
        public bool Dump { get; set; }
        public string Emit { get; set; } = "exe";
        public string? Output { get; set; }
        public string Compiler { get; set; } = Environment.GetEnvironmentVariable("CC") is { Length: > 0 } cc ? cc : "cc";
        public List<string> CFlags { get; } = ["-O2"];
        public bool LineDirectives { get; set; }
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
        if (command is not ("parse" or "check" or "build"))
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
                sources.Add(new SourceText(SourceText.SourcePath(file, Environment.CurrentDirectory), File.ReadAllText(file)));
            }
            catch (IOException e)
            {
                return Fail($"cannot read '{file}': {e.Message}");
            }
        }

        return command switch
        {
            "parse" => Report(Parse(sources, options.Dump)),
            "check" => Report(Check(sources, options.Dump)),
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

            if (arg == dumpOption)
            {
                options.Dump = true;
            }
            else if (command == "build" && arg == "--line-directives")
            {
                options.LineDirectives = true;
            }
            else if (command == "build" && arg == "-g")
            {
                options.LineDirectives = true;
                options.CFlags.Remove("-O2");
                options.CFlags.InsertRange(0, ["-g", "-O0"]);
            }
            else if (command == "build" && arg is "--emit" or "-o" or "--cc" or "--cflag")
            {
                if (Value() is not { } value)
                {
                    return null;
                }
                switch (arg)
                {
                    case "--emit" when value is "c" or "exe": options.Emit = value; break;
                    case "--emit": Fail($"--emit must be 'c' or 'exe', not '{value}'"); return null;
                    case "-o": options.Output = value; break;
                    case "--cc": options.Compiler = value; break;
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

    private static DiagnosticBag Check(List<SourceText> sources, bool dumpHir)
    {
        var compilation = Compilation.Create(sources, TargetInfo.Lp64);
        if (dumpHir && compilation.Hir is not null)
        {
            Console.Out.Write(HirPrinter.Print(compilation.Hir, compilation.Symbols));
        }
        return compilation.Diagnostics;
    }

    private static int Build(List<SourceText> sources, Options options)
    {
        var compilation = Compilation.Create(sources, TargetInfo.Lp64);
        if (compilation.Diagnostics.HasErrors)
        {
            return Report(compilation.Diagnostics);
        }

        var program = CBackend.Generate(compilation, requireMain: options.Emit == "exe", out var nir,
            new CEmitOptions(LineDirectives: options.LineDirectives));
        if (program is null)
        {
            return Report(compilation.Diagnostics);
        }
        if (options.Dump && nir is not null)
        {
            Console.Out.Write(NirPrinter.Print(nir, compilation.Symbols));
        }

        var defaultName = Path.GetFileNameWithoutExtension(sources[0].Path);
        if (options.Emit == "c")
        {
            var directory = options.Output ?? defaultName + "-c";
            program.WriteTo(directory);
            return 0;
        }

        var output = options.Output ?? defaultName;
        var workDirectory = Path.Combine(Path.GetTempPath(), "novac-" + Guid.NewGuid().ToString("N"));
        try
        {
            program.WriteTo(workDirectory);
            // Run in the current directory: the source paths in #line directives are relative to it (c-backend.md §7).
            var result = Toolchain.CompileExecutable(options.Compiler, workDirectory, program, output, options.CFlags,
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"novac: {message}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
