using System.Text.RegularExpressions;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>
/// File-based tests under tests/corpus (format: docs/testing.md §3).
/// <list type="bullet">
/// <item>parse-pass/*.nova must parse without diagnostics; the AST dump must equal the sibling .ast snapshot.</item>
/// <item>parse-fail/*.nova must produce exactly the diagnostics annotated with <c>// error[CODE]</c> comments,
/// matched by (line, code), optionally <c>// error[CODE]@column</c>.</item>
/// </list>
/// Set NOVA_BLESS=1 to (re)write .ast snapshots; review the diff before committing.
/// </summary>
public partial class CorpusTests
{
    private static readonly string CorpusRoot = Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus");

    public static TheoryData<string> ParsePassFiles() => Files("parse-pass");

    public static TheoryData<string> ParseFailFiles() => Files("parse-fail");

    private static TheoryData<string> Files(string directory)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(CorpusRoot, directory), "*.nova").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(CorpusRoot, file).Replace('\\', '/'));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ParsePassFiles))]
    public void Parse_pass(string relativePath)
    {
        var (unit, symbols, diagnostics) = ParseFile(relativePath);
        Assert.True(diagnostics.Items.Count == 0,
            "unexpected diagnostics:\n" + string.Join("\n", diagnostics.Items.Select(d => d.ToString())));

        var actual = AstPrinter.Print(unit, symbols);
        var snapshotPath = Path.ChangeExtension(Path.Combine(CorpusRoot, relativePath), ".ast");
        if (Environment.GetEnvironmentVariable("NOVA_BLESS") == "1")
        {
            File.WriteAllText(snapshotPath, actual);
            return;
        }
        Assert.True(File.Exists(snapshotPath), $"missing snapshot {snapshotPath}; run with NOVA_BLESS=1 and review it");
        Assert.Equal(File.ReadAllText(snapshotPath).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [MemberData(nameof(ParseFailFiles))]
    public void Parse_fail(string relativePath)
    {
        var (_, _, diagnostics) = ParseFile(relativePath);
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var expected = ParseAnnotations(text);
        Assert.True(expected.Count > 0, "a parse-fail test must annotate at least one expected error");

        var actual = diagnostics.Items
            .Select(d => (d.Position.Line, d.Code, (int?)d.Position.Column))
            .ToList();

        var unmatched = new List<string>();
        foreach (var (line, code, column) in expected)
        {
            var index = actual.FindIndex(a => a.Line == line && a.Code == code && (column is null || a.Item3 == column));
            if (index < 0)
            {
                unmatched.Add($"missing {code} at line {line}{(column is null ? "" : $":{column}")}");
            }
            else
            {
                actual.RemoveAt(index);
            }
        }
        unmatched.AddRange(actual.Select(a => $"unexpected {a.Code} at {a.Line}:{a.Item3}"));

        Assert.True(unmatched.Count == 0,
            string.Join("\n", unmatched) + "\nall diagnostics:\n" + string.Join("\n", diagnostics.Items.Select(d => d.ToString())));
    }

    private static (CompilationUnit, SymbolTable, DiagnosticBag) ParseFile(string relativePath)
    {
        var symbols = new SymbolTable();
        var diagnostics = new DiagnosticBag();
        var text = File.ReadAllText(Path.Combine(CorpusRoot, relativePath));
        var unit = Parser.ParseText(new SourceText(relativePath, text), symbols, diagnostics);
        return (unit, symbols, diagnostics);
    }

    private static List<(int Line, string Code, int? Column)> ParseAnnotations(string text)
    {
        var result = new List<(int, string, int?)>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match match in AnnotationPattern().Matches(lines[i]))
            {
                int? column = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
                result.Add((i + 1, match.Groups[1].Value, column));
            }
        }
        return result;
    }

    [GeneratedRegex(@"// error\[(N\d{4})\](?:@(\d+))?")]
    private static partial Regex AnnotationPattern();
}
