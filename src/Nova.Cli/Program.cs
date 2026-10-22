using Nova.Compiler;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
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
        options:
          --dump-ast   (parse) print the AST of each file
          --dump-hir   (check) print the typed HIR of the package
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Out.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        return args[0] switch
        {
            "parse" => Run(args[1..], "--dump-ast", Parse),
            "check" => Run(args[1..], "--dump-hir", Check),
            _ => Fail($"unknown command '{args[0]}'"),
        };
    }

    private static int Run(string[] args, string dumpOption, Func<List<SourceText>, bool, DiagnosticBag> command)
    {
        var dump = false;
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (arg == dumpOption)
            {
                dump = true;
            }
            else if (arg.StartsWith('-'))
            {
                return Fail($"unknown option '{arg}'");
            }
            else
            {
                files.Add(arg);
            }
        }
        if (files.Count == 0)
        {
            return Fail("no input files");
        }

        var sources = new List<SourceText>();
        foreach (var file in files)
        {
            try
            {
                sources.Add(new SourceText(file, File.ReadAllText(file)));
            }
            catch (IOException e)
            {
                return Fail($"cannot read '{file}': {e.Message}");
            }
        }

        var diagnostics = command(sources, dump);
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

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"novac: {message}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
