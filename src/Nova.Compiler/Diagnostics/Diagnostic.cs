using System.Collections.Immutable;
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
    public const string ExpectedMember = "N0107";
    public const string ExpectedPattern = "N0108";

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
    public const string RecursiveStruct = "N0227";
    public const string DuplicateMember = "N0228";
    public const string MissingField = "N0229";
    public const string UnknownField = "N0230";
    public const string DuplicateFieldInit = "N0231";
    public const string ThisOutsideMethod = "N0232";
    public const string MethodCallKind = "N0233";
    public const string NotAStruct = "N0234";

    // References and borrows (lifetimes.md): N0240-N0249
    public const string WriteWhileBorrowed = "N0240";
    public const string ConflictingBorrow = "N0241";
    public const string ReadWhileMutablyBorrowed = "N0242";
    public const string DeadWhileBorrowed = "N0243";
    public const string ReturnsLocalReference = "N0244";
    public const string ReturnSourceMismatch = "N0245";
    public const string ReferenceNotAllowed = "N0246";
    public const string BorrowOfNonPlace = "N0247";
    public const string BorrowMutOfImmutable = "N0248";
    public const string MutArgumentMismatch = "N0249";

    // Moves and destruction (type-system.md §14): N0250-N0259
    public const string UseOfMoved = "N0250";
    public const string MoveOutOfReference = "N0251";
    public const string MoveOutOfDropType = "N0252";
    public const string MoveOutOfUsing = "N0253";
    public const string JumpInDefer = "N0254";

    // Enums and match (type-system.md §15)
    public const string NonExhaustiveMatch = "N0256";
    public const string UnreachableArm = "N0257";
    public const string NonCopyBinding = "N0258";
    public const string InvalidArray = "N0259";

    // Results and the heap (type-system.md §17-§18): N0260-N0269
    public const string ResultTypeUnknown = "N0260";
    public const string TryReturnMismatch = "N0261";
    public const string NotAResult = "N0262";
    public const string NotAnAllocator = "N0263";
    public const string NotRegionContent = "N0264";
    public const string CannotInferTypeArguments = "N0265";
    public const string InstantiationTooDeep = "N0266";
    public const string ImplMismatch = "N0267";
    public const string BoundNotSatisfied = "N0268";

    // Region parameters (type-system.md §25): N0270-N0279
    public const string ScopedFieldNeedsRegion = "N0270";
    public const string ForeignLoanStored = "N0272";
    public const string UnknownRegion = "N0273";
    public const string ScopedTypeArgument = "N0274";

    // Unsafe and raw pointers (type-system.md §27, unsafe.md): N0280-N0289
    public const string UnsafeOutsideUnsafe = "N0280";
    public const string InvalidPointee = "N0281";
    public const string InvalidPointerOperation = "N0282";
    public const string MissingSafetyComment = "N0283";

    // C interoperability (type-system.md §28): N0290-N0299
    public const string InvalidAttribute = "N0290";
    public const string NotFfiSafe = "N0291";
    public const string InvalidCName = "N0292";
    public const string HostedOnly = "N0293";
    public const string ImportedTypeByValue = "N0294";
    public const string EffectViolation = "N0295";
    public const string PrivateField = "N0296";

    // Build: N0300-N0399
    public const string MissingMain = "N0300";
}

/// <summary>A secondary location attached to a diagnostic ("borrowed here").</summary>
public sealed record DiagnosticNote(TextSpan Span, string Message);

public sealed record Diagnostic(string Code, Severity Severity, string Message, SourceText Source, TextSpan Span)
{
    public ImmutableArray<DiagnosticNote> Notes { get; init; } = [];

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

    public void Error(string code, SourceText source, TextSpan span, string message, ImmutableArray<DiagnosticNote> notes) =>
        _items.Add(new Diagnostic(code, Severity.Error, message, source, span) { Notes = notes });

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
        foreach (var note in diagnostic.Notes)
        {
            sb.Append(' ', gutter.Length).Append(" = note: ").Append(note.Message)
              .Append(" (").Append(diagnostic.Source.GetLinePosition(note.Span.Start).ToString()).Append(")\n");
        }
        return sb.ToString();
    }
}
