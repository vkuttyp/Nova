using System.Text;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Diagnostics;

public enum Severity
{
    Error,
    Warning,
}

/// <summary>Stable diagnostic codes. Meanings are documented in docs/diagnostics.md; never reuse a retired code.</summary>
public static class DiagnosticCodes
{
    // Lexer: N0001-N0099
    public const string UnexpectedCharacter = "N0001";
    public const string UnterminatedBlockComment = "N0003";
    public const string IntegerLiteralTooLarge = "N0004";
    public const string UnterminatedString = "N0005";
    public const string InvalidEscape = "N0006";
    public const string InvalidCharLiteral = "N0007";
    public const string InvalidDigit = "N0008";
    public const string InvalidLiteralSuffix = "N0009";
    public const string MisplacedDigitSeparator = "N0010";

    // Parser: N0100-N0199
    public const string ExpectedToken = "N0100";
    public const string ExpectedItem = "N0101";
    public const string ExpectedExpression = "N0102";
    public const string ChainedComparison = "N0103";
    public const string InvalidAssignmentTarget = "N0104";
    public const string ExpectedType = "N0105";
    public const string MisplacedNamespace = "N0106";
}

public sealed record Diagnostic(string Code, Severity Severity, string Message, SourceText Source, TextSpan Span)
{
    public LinePosition Position => Source.GetLinePosition(Span.Start);

    public override string ToString() =>
        $"{Source.Path}:{Position}: {(Severity == Severity.Error ? "error" : "warning")}[{Code}]: {Message}";
}

/// <summary>Collects diagnostics in the order they are reported.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];

    public IReadOnlyList<Diagnostic> Items => _items;

    public bool HasErrors => _items.Exists(d => d.Severity == Severity.Error);

    public void Error(string code, SourceText source, TextSpan span, string message) =>
        _items.Add(new Diagnostic(code, Severity.Error, message, source, span));

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);
}

/// <summary>Renders diagnostics as human-readable text with a source excerpt and caret.</summary>
public static class DiagnosticRenderer
{
    public static string Render(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var sb = new StringBuilder();
        sb.Append(diagnostic.ToString()).Append('\n');

        var position = diagnostic.Position;
        var lineText = diagnostic.Source.GetLineText(position.Line);
        var gutter = position.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        sb.Append(' ', gutter.Length).Append(" |\n");
        sb.Append(gutter).Append(" | ").Append(lineText.Replace('\t', ' ')).Append('\n');

        // Underline the span, clipped to the first line it touches; at least one caret.
        var underline = Math.Max(1, Math.Min(diagnostic.Span.Length, lineText.Length - position.Column + 1));
        sb.Append(' ', gutter.Length).Append(" | ").Append(' ', position.Column - 1).Append('^', underline).Append('\n');
        return sb.ToString();
    }
}
