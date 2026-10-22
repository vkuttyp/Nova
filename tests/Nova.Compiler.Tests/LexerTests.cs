using Nova.Compiler.Diagnostics;
using Nova.Compiler.Syntax;
using static Nova.Compiler.Tests.TestHelpers;

namespace Nova.Compiler.Tests;

public class LexerTests
{
    private static TokenKind[] Kinds(string text)
    {
        var (lexed, _, diagnostics) = Lex(text);
        Assert.Empty(diagnostics.Items);
        return [.. lexed.Tokens.Select(t => t.Kind)];
    }

    [Fact]
    public void Empty_input_yields_only_end_of_file()
    {
        Assert.Equal([TokenKind.EndOfFile], Kinds(""));
        Assert.Equal([TokenKind.EndOfFile], Kinds("  // comment\n /* block */ \t"));
    }

    [Fact]
    public void Leading_byte_order_mark_is_ignored()
    {
        var (lexed, _, diagnostics) = Lex("\uFEFFfn");
        Assert.Empty(diagnostics.Items);
        Assert.Equal(new TextSpan(0, 2), lexed.Tokens[0].Span);
    }

    [Theory]
    [InlineData("fn", TokenKind.FnKeyword)]
    [InlineData("let", TokenKind.LetKeyword)]
    [InlineData("var", TokenKind.VarKeyword)]
    [InlineData("Self", TokenKind.SelfTypeKeyword)]
    [InlineData("class", TokenKind.ClassKeyword)]
    [InlineData("_", TokenKind.Underscore)]
    [InlineData("i32", TokenKind.Identifier)]
    [InlineData("self", TokenKind.Identifier)]
    [InlineData("_x1", TokenKind.Identifier)]
    [InlineData("fnord", TokenKind.Identifier)]
    public void Keywords_and_identifiers(string text, TokenKind expected) =>
        Assert.Equal([expected, TokenKind.EndOfFile], Kinds(text));

    [Fact]
    public void Identifiers_are_interned()
    {
        var (lexed, symbols, _) = Lex("abc def abc");
        Assert.Equal(lexed.Tokens[0].Data, lexed.Tokens[2].Data);
        Assert.NotEqual(lexed.Tokens[0].Data, lexed.Tokens[1].Data);
        Assert.Equal("def", symbols.GetName(new Symbol(lexed.Tokens[1].Data)));
    }

    [Fact]
    public void Every_punctuation_token_round_trips()
    {
        var all = Enum.GetValues<TokenKind>()
            .Where(k => k >= TokenKind.OpenParen)
            .ToArray();
        foreach (var kind in all)
        {
            Assert.Equal([kind, TokenKind.EndOfFile], Kinds(TokenFacts.Text(kind)));
        }
    }

    [Fact]
    public void Right_shift_is_never_a_single_token()
    {
        Assert.Equal([TokenKind.Greater, TokenKind.Greater, TokenKind.EndOfFile], Kinds(">>"));
        Assert.Equal([TokenKind.Greater, TokenKind.GreaterEquals, TokenKind.EndOfFile], Kinds(">>="));
    }

    [Fact]
    public void Range_after_integer_is_not_a_float()
    {
        Assert.Equal([TokenKind.IntLiteral, TokenKind.DotDot, TokenKind.IntLiteral, TokenKind.EndOfFile], Kinds("1..2"));
        Assert.Equal([TokenKind.IntLiteral, TokenKind.Dot, TokenKind.Identifier, TokenKind.EndOfFile], Kinds("1.x"));
    }

    [Theory]
    [InlineData("0", 0ul, IntSuffix.None)]
    [InlineData("1_000_000", 1_000_000ul, IntSuffix.None)]
    [InlineData("010", 10ul, IntSuffix.None)]
    [InlineData("0xFF_ff", 0xFFFFul, IntSuffix.None)]
    [InlineData("0b1010", 10ul, IntSuffix.None)]
    [InlineData("0o777", 511ul, IntSuffix.None)]
    [InlineData("255u8", 255ul, IntSuffix.U8)]
    [InlineData("0x10i64", 16ul, IntSuffix.I64)]
    [InlineData("7usize", 7ul, IntSuffix.USize)]
    [InlineData("18446744073709551615", ulong.MaxValue, IntSuffix.None)]
    public void Integer_literals(string text, ulong value, IntSuffix suffix)
    {
        var (lexed, _, diagnostics) = Lex(text);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(TokenKind.IntLiteral, lexed.Tokens[0].Kind);
        Assert.Equal(new IntLiteralValue(value, suffix), lexed.Ints[lexed.Tokens[0].Data]);
        Assert.Equal(text.Length, lexed.Tokens[0].Span.Length);
    }

    [Theory]
    [InlineData("1.5", "1.5", FloatSuffix.None)]
    [InlineData("1e9", "1e9", FloatSuffix.None)]
    [InlineData("2.5E-3f32", "2.5E-3", FloatSuffix.F32)]
    [InlineData("1_000.000_1f64", "1000.0001", FloatSuffix.F64)]
    public void Float_literals(string text, string digits, FloatSuffix suffix)
    {
        var (lexed, _, diagnostics) = Lex(text);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(TokenKind.FloatLiteral, lexed.Tokens[0].Kind);
        Assert.Equal(new FloatLiteralValue(digits, suffix), lexed.Floats[lexed.Tokens[0].Data]);
    }

    [Theory]
    [InlineData("\"\"", "")]
    [InlineData("\"hi there\"", "hi there")]
    [InlineData("\"a\\nb\\t\\\\\\\"\\'\\0\"", "a\nb\t\\\"'\0")]
    [InlineData("\"\\u{41}\\u{1F600}\"", "A\U0001F600")]
    [InlineData("\"héllo\"", "héllo")]
    public void String_literals(string text, string value)
    {
        var (lexed, _, diagnostics) = Lex(text);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(value, lexed.Strings[lexed.Tokens[0].Data]);
    }

    [Theory]
    [InlineData("'a'", 'a')]
    [InlineData("'\\n'", '\n')]
    [InlineData("'\\''", '\'')]
    [InlineData("'\\u{10FFFF}'", 0x10FFFF)]
    [InlineData("'😀'", 0x1F600)]
    public void Char_literals(string text, int value)
    {
        var (lexed, _, diagnostics) = Lex(text);
        Assert.Empty(diagnostics.Items);
        Assert.Equal(value, lexed.Chars[lexed.Tokens[0].Data]);
    }

    [Theory]
    [InlineData("#", DiagnosticCodes.UnexpectedCharacter)]
    [InlineData("é", DiagnosticCodes.UnexpectedCharacter)]
    [InlineData("/* never closed", DiagnosticCodes.UnterminatedBlockComment)]
    [InlineData("18446744073709551616", DiagnosticCodes.IntegerLiteralTooLarge)]
    [InlineData("\"abc", DiagnosticCodes.UnterminatedString)]
    [InlineData("\"abc\ndef", DiagnosticCodes.UnterminatedString)]
    [InlineData("\"\\q\"", DiagnosticCodes.InvalidEscape)]
    [InlineData("\"\\u{110000}\"", DiagnosticCodes.InvalidEscape)]
    [InlineData("\"\\u{D800}\"", DiagnosticCodes.InvalidEscape)]
    [InlineData("\"\\u41\"", DiagnosticCodes.InvalidEscape)]
    [InlineData("''", DiagnosticCodes.InvalidCharLiteral)]
    [InlineData("'ab'", DiagnosticCodes.InvalidCharLiteral)]
    [InlineData("'a", DiagnosticCodes.InvalidCharLiteral)]
    [InlineData("0b102", DiagnosticCodes.InvalidDigit)]
    [InlineData("0o8", DiagnosticCodes.InvalidDigit)]
    [InlineData("0x", DiagnosticCodes.InvalidDigit)]
    [InlineData("0xg", DiagnosticCodes.InvalidDigit)]
    [InlineData("12abc", DiagnosticCodes.InvalidLiteralSuffix)]
    [InlineData("1.5u8", DiagnosticCodes.InvalidLiteralSuffix)]
    [InlineData("1f32", DiagnosticCodes.InvalidLiteralSuffix)]
    [InlineData("1__000", DiagnosticCodes.MisplacedDigitSeparator)]
    [InlineData("1000_", DiagnosticCodes.MisplacedDigitSeparator)]
    [InlineData("0x_FF", DiagnosticCodes.MisplacedDigitSeparator)]
    [InlineData("1_.5", DiagnosticCodes.MisplacedDigitSeparator)]
    public void Lexical_errors_report_exactly_one_diagnostic(string text, string code)
    {
        var (lexed, _, diagnostics) = Lex(text);
        Assert.Equal([code], Codes(diagnostics));
        Assert.Equal(TokenKind.EndOfFile, lexed.Tokens[^1].Kind);
    }

    [Fact]
    public void Lexing_continues_after_an_error()
    {
        var (lexed, _, diagnostics) = Lex("a # b");
        Assert.Single(diagnostics.Items);
        Assert.Equal([TokenKind.Identifier, TokenKind.Identifier, TokenKind.EndOfFile], lexed.Tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Line_positions_handle_all_line_terminators()
    {
        var source = new SourceText("t.nova", "a\nbb\r\nccc\rd");
        Assert.Equal(new LinePosition(1, 1), source.GetLinePosition(0));
        Assert.Equal(new LinePosition(2, 2), source.GetLinePosition(3));
        Assert.Equal(new LinePosition(3, 1), source.GetLinePosition(6));
        Assert.Equal(new LinePosition(4, 1), source.GetLinePosition(10));
        Assert.Equal("bb", source.GetLineText(2));
        Assert.Equal("ccc", source.GetLineText(3));
    }

    [Fact]
    public void Diagnostic_position_is_reported_one_based()
    {
        var (_, _, diagnostics) = Lex("let x\n  # y");
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal(new LinePosition(2, 3), diagnostic.Position);
        Assert.Equal("test.nova:2:3: error[N0001]: unexpected character '#'", diagnostic.ToString());
    }
}
