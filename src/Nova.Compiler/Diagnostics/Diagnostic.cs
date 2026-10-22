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

    // Names, types and flow: N0200-N0299
    public const string UndefinedName = "N0200";
    public const string DuplicateItem = "N0201";
    public const string DuplicateParameter = "N0202";
    public const string LocalRedeclared = "N0203";
    public const string UnknownType = "N0204";
    public const string TypeMismatch = "N0205";
    public const string LiteralOutOfRange = "N0206";
    public const string InvalidOperandTypes = "N0207";
    public const string InvalidCast = "N0208";
    public const string ArgumentCount = "N0209";
    public const string NotCallable = "N0210";
    public const string AssignToImmutable = "N0211";
    public const string PossiblyUnassigned = "N0212";
    public const string MissingReturn = "N0213";
    public const string LoopControlOutsideLoop = "N0214";
    public const string ReturnValueMismatch = "N0215";
    public const string Unsupported = "N0216";
    public const string NotConstant = "N0217";
    public const string CyclicConstant = "N0218";
    public const string InvalidMain = "N0219";
    public const string LetWithoutInitializer = "N0220";
    public const string LocalNeedsType = "N0221";
    // N0222, N0223: unassigned
    public const string VoidValue = "N0224";
    public const string StatementNotCall = "N0225";
    public const string VoidNotAllowed = "N0226";
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
