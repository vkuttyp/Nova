using Nova.Compiler.Diagnostics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

internal static class TestHelpers
{
    public static (LexedSource Lexed, SymbolTable Symbols, DiagnosticBag Diagnostics) Lex(string text)
    {
        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        var lexed = Lexer.Lex(new SourceText("test.nova", text), symbols, diagnostics);
        return (lexed, symbols, diagnostics);
    }

    public static (CompilationUnit Unit, SymbolTable Symbols, DiagnosticBag Diagnostics) Parse(string text)
    {
        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        var unit = Parser.ParseText(new SourceText("test.nova", text), symbols, diagnostics);
        return (unit, symbols, diagnostics);
    }

    /// <summary>Parses <paramref name="expression"/> as the initialiser of a local and prints it.</summary>
    public static string ParseExpr(string expression)
    {
        var (unit, symbols, diagnostics) = Parse($"fn f() {{ let x = {expression}; }}");
        Assert.Empty(diagnostics.Items);
        var fn = Assert.IsType<FunctionDecl>(Assert.Single(unit.Items));
        var local = Assert.IsType<LocalDeclStmt>(Assert.Single(fn.Body!.Statements));
        return AstPrinter.PrintExpr(local.Initializer!, symbols);
    }

    public static string[] Codes(DiagnosticBag diagnostics) => [.. diagnostics.Items.Select(d => d.Code)];

    /// <summary>The repository root (the directory containing Nova.slnx).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nova.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("cannot find repository root (Nova.slnx)");
    }
}
