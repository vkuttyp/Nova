namespace Nova.Compiler.Syntax;

public enum TokenKind : byte
{
    EndOfFile,

    Identifier,
    IntLiteral,
    FloatLiteral,
    StringLiteral,
    CharLiteral,
    Underscore,

    // Keywords (grammar.md §1.3). Keep in sync with Keywords.Table.
    AsKeyword,
    AsyncKeyword,
    AwaitKeyword,
    BorrowKeyword,
    BreakKeyword,
    ClassKeyword,
    ConstKeyword,
    ContinueKeyword,
    DeferKeyword,
    DropKeyword,
    ElseKeyword,
    EnumKeyword,
    ExternKeyword,
    FalseKeyword,
    FnKeyword,
    ForKeyword,
    IfKeyword,
    ImplKeyword,
    ImportKeyword,
    InKeyword,
    InterfaceKeyword,
    InternalKeyword,
    LetKeyword,
    LoopKeyword,
    MacroKeyword,
    MatchKeyword,
    MoveKeyword,
    MutKeyword,
    NamespaceKeyword,
    NullKeyword,
    OwnKeyword,
    PrivateKeyword,
    PublicKeyword,
    ReturnKeyword,
    SelfTypeKeyword,
    StaticKeyword,
    StructKeyword,
    ThisKeyword,
    TrueKeyword,
    TypeKeyword,
    UnsafeKeyword,
    UsingKeyword,
    VarKeyword,
    WhereKeyword,
    WhileKeyword,
    YieldKeyword,

    // Punctuation
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Comma,
    Semicolon,
    Colon,
    Dot,
    DotDot,
    At,
    Question,
    Arrow,
    FatArrow,

    // Operators
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Bang,
    Amp,
    Pipe,
    Caret,
    Tilde,
    AmpAmp,
    PipePipe,
    LessLess,
    Equals,
    EqualsEquals,
    BangEquals,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,
    PlusEquals,
    MinusEquals,
    StarEquals,
    SlashEquals,
    PercentEquals,
    AmpEquals,
    PipeEquals,
    CaretEquals,
    LessLessEquals,
}

public enum IntSuffix : byte
{
    None,
    I8,
    I16,
    I32,
    I64,
    ISize,
    U8,
    U16,
    U32,
    U64,
    USize,
}

public enum FloatSuffix : byte
{
    None,
    F32,
    F64,
}

/// <summary>
/// A token. <see cref="Data"/> depends on the kind: the symbol id for identifiers, or the
/// index into the matching literal table of <see cref="LexedSource"/> for literals.
/// </summary>
public readonly record struct Token(TokenKind Kind, TextSpan Span, int Data = 0);

public readonly record struct IntLiteralValue(ulong Value, IntSuffix Suffix);

public readonly record struct FloatLiteralValue(string Digits, FloatSuffix Suffix);

/// <summary>The lexer's output: tokens (ending in EndOfFile) plus literal side tables.</summary>
public sealed class LexedSource
{
    internal LexedSource(
        SourceText source,
        Token[] tokens,
        IntLiteralValue[] ints,
        FloatLiteralValue[] floats,
        string[] strings,
        int[] chars)
    {
        Source = source;
        Tokens = tokens;
        Ints = ints;
        Floats = floats;
        Strings = strings;
        Chars = chars;
    }

    public SourceText Source { get; }

    public IReadOnlyList<Token> Tokens { get; }

    public IReadOnlyList<IntLiteralValue> Ints { get; }

    public IReadOnlyList<FloatLiteralValue> Floats { get; }

    public IReadOnlyList<string> Strings { get; }

    /// <summary>Unicode scalar values of char literals.</summary>
    public IReadOnlyList<int> Chars { get; }
}

public static class Keywords
{
    public static readonly IReadOnlyDictionary<string, TokenKind> Table = new Dictionary<string, TokenKind>(StringComparer.Ordinal)
    {
        ["as"] = TokenKind.AsKeyword,
        ["async"] = TokenKind.AsyncKeyword,
        ["await"] = TokenKind.AwaitKeyword,
        ["borrow"] = TokenKind.BorrowKeyword,
        ["break"] = TokenKind.BreakKeyword,
        ["class"] = TokenKind.ClassKeyword,
        ["const"] = TokenKind.ConstKeyword,
        ["continue"] = TokenKind.ContinueKeyword,
        ["defer"] = TokenKind.DeferKeyword,
        ["drop"] = TokenKind.DropKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["enum"] = TokenKind.EnumKeyword,
        ["extern"] = TokenKind.ExternKeyword,
        ["false"] = TokenKind.FalseKeyword,
        ["fn"] = TokenKind.FnKeyword,
        ["for"] = TokenKind.ForKeyword,
        ["if"] = TokenKind.IfKeyword,
        ["impl"] = TokenKind.ImplKeyword,
        ["import"] = TokenKind.ImportKeyword,
        ["in"] = TokenKind.InKeyword,
        ["interface"] = TokenKind.InterfaceKeyword,
        ["internal"] = TokenKind.InternalKeyword,
        ["let"] = TokenKind.LetKeyword,
        ["loop"] = TokenKind.LoopKeyword,
        ["macro"] = TokenKind.MacroKeyword,
        ["match"] = TokenKind.MatchKeyword,
        ["move"] = TokenKind.MoveKeyword,
        ["mut"] = TokenKind.MutKeyword,
        ["namespace"] = TokenKind.NamespaceKeyword,
        ["null"] = TokenKind.NullKeyword,
        ["own"] = TokenKind.OwnKeyword,
        ["private"] = TokenKind.PrivateKeyword,
        ["public"] = TokenKind.PublicKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["Self"] = TokenKind.SelfTypeKeyword,
        ["static"] = TokenKind.StaticKeyword,
        ["struct"] = TokenKind.StructKeyword,
        ["this"] = TokenKind.ThisKeyword,
        ["true"] = TokenKind.TrueKeyword,
        ["type"] = TokenKind.TypeKeyword,
        ["unsafe"] = TokenKind.UnsafeKeyword,
        ["using"] = TokenKind.UsingKeyword,
        ["var"] = TokenKind.VarKeyword,
        ["where"] = TokenKind.WhereKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["yield"] = TokenKind.YieldKeyword,
    };

    private static readonly Dictionary<TokenKind, string> s_text = Table.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static bool IsKeyword(TokenKind kind) => kind >= TokenKind.AsKeyword && kind <= TokenKind.YieldKeyword;

    public static string KeywordText(TokenKind kind) => s_text[kind];
}

public static class TokenFacts
{
    /// <summary>How a token kind is named in diagnostics ("expected ';', found identifier").</summary>
    public static string Describe(TokenKind kind) => kind switch
    {
        TokenKind.EndOfFile => "end of file",
        TokenKind.Identifier => "identifier",
        TokenKind.IntLiteral => "integer literal",
        TokenKind.FloatLiteral => "float literal",
        TokenKind.StringLiteral => "string literal",
        TokenKind.CharLiteral => "char literal",
        _ => $"'{Text(kind)}'",
    };

    public static string Text(TokenKind kind) => kind switch
    {
        TokenKind.Underscore => "_",
        TokenKind.OpenParen => "(",
        TokenKind.CloseParen => ")",
        TokenKind.OpenBrace => "{",
        TokenKind.CloseBrace => "}",
        TokenKind.OpenBracket => "[",
        TokenKind.CloseBracket => "]",
        TokenKind.Comma => ",",
        TokenKind.Semicolon => ";",
        TokenKind.Colon => ":",
        TokenKind.Dot => ".",
        TokenKind.DotDot => "..",
        TokenKind.At => "@",
        TokenKind.Question => "?",
        TokenKind.Arrow => "->",
        TokenKind.FatArrow => "=>",
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.Bang => "!",
        TokenKind.Amp => "&",
        TokenKind.Pipe => "|",
        TokenKind.Caret => "^",
        TokenKind.Tilde => "~",
        TokenKind.AmpAmp => "&&",
        TokenKind.PipePipe => "||",
        TokenKind.LessLess => "<<",
        TokenKind.Equals => "=",
        TokenKind.EqualsEquals => "==",
        TokenKind.BangEquals => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEquals => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEquals => ">=",
        TokenKind.PlusEquals => "+=",
        TokenKind.MinusEquals => "-=",
        TokenKind.StarEquals => "*=",
        TokenKind.SlashEquals => "/=",
        TokenKind.PercentEquals => "%=",
        TokenKind.AmpEquals => "&=",
        TokenKind.PipeEquals => "|=",
        TokenKind.CaretEquals => "^=",
        TokenKind.LessLessEquals => "<<=",
        _ when Keywords.IsKeyword(kind) => Keywords.KeywordText(kind),
        _ => kind.ToString(),
    };
}
