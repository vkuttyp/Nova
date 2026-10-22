using Nova.Compiler.Diagnostics;
using Nova.Compiler.Syntax;

namespace Nova.Cli;

internal static class Program
{
    private const string Usage = """
        usage: novac parse [--dump-ast] <file.nova>...

        commands:
          parse    lex and parse the files, report syntax errors
        options:
          --dump-ast   print the AST of each file (stable S-expression format)
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
            "parse" => Parse(args[1..]),
            _ => Fail($"unknown command '{args[0]}'"),
        };
    }

    private static int Parse(string[] args)
    {
        var dumpAst = false;
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (arg == "--dump-ast")
            {
                dumpAst = true;
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

        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException e)
            {
                return Fail($"cannot read '{file}': {e.Message}");
            }

            var unit = Parser.ParseText(new SourceText(file, text), symbols, diagnostics);
            if (dumpAst)
            {
                Console.Out.Write(AstPrinter.Print(unit, symbols));
            }
        }

        foreach (var diagnostic in diagnostics.Items)
        {
            Console.Error.Write(DiagnosticRenderer.Render(diagnostic));
        }
        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"novac: {message}");
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
