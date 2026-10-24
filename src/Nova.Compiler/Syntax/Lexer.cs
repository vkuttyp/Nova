using System.Globalization;
using System.Text;
using Nova.Compiler.Diagnostics;

namespace Nova.Compiler.Syntax;

/// <summary>Converts source text into a <see cref="LexedSource"/> (grammar.md §1).</summary>
public sealed class Lexer
{
    private const int Eof = -1;

    private readonly SourceText _source;
    private readonly string _text;
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;

    private readonly List<Token> _tokens = [];
    private readonly List<IntLiteralValue> _ints = [];
    private readonly List<FloatLiteralValue> _floats = [];
    private readonly List<string> _strings = [];
    private readonly List<int> _chars = [];
    private readonly Dictionary<int, string> _docs = [];
    private readonly List<string> _pendingDoc = [];

    private int _pos;

    private Lexer(SourceText source, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _source = source;
        _text = source.Text;
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    public static LexedSource Lex(SourceText source, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var lexer = new Lexer(source, symbols, diagnostics);
        lexer.Run();
        return new LexedSource(
            source,
            [.. lexer._tokens],
            [.. lexer._ints],
            [.. lexer._floats],
            [.. lexer._strings],
            [.. lexer._chars],
            lexer._docs);
    }

    private int Ch(int offset = 0)
    {
        var i = _pos + offset;
        return i < _text.Length ? _text[i] : Eof;
    }

    private void Error(string code, int start, int end, string message) =>
        _diagnostics.Error(code, _source, TextSpan.FromBounds(start, end), message);

    private void Add(TokenKind kind, int start, int data = 0)
    {
        if (_pendingDoc.Count > 0)
        {
            _docs[_tokens.Count] = string.Join('\n', _pendingDoc);
            _pendingDoc.Clear();
        }
        _tokens.Add(new Token(kind, TextSpan.FromBounds(start, _pos), data));
    }

    private void Run()
    {
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _text.Length)
            {
                _tokens.Add(new Token(TokenKind.EndOfFile, new TextSpan(_text.Length, 0)));
                return;
            }

            var start = _pos;
            var c = _text[_pos];
            if (IsIdentStart(c))
            {
                LexIdentifier(start);
            }
            else if (IsDecimalDigit(c))
            {
                LexNumber(start);
            }
            else if (c == '"')
            {
                LexString(start);
            }
            else if (c == '\'')
            {
                LexChar(start);
            }
            else if (!TryLexPunctuation(start, c))
            {
                // Consume a whole surrogate pair so the span covers one character.
                _pos += char.IsHighSurrogate(c) && char.IsLowSurrogate((char)Ch(1)) ? 2 : 1;
                Error(DiagnosticCodes.UnexpectedCharacter, start, _pos, $"unexpected character {DescribeChar(start)}");
            }
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                _pos++;
            }
            else if (c == '/' && Ch(1) == '/')
            {
                var start = _pos;
                while (_pos < _text.Length && _text[_pos] is not ('\n' or '\r'))
                {
                    _pos++;
                }
                // A doc comment is exactly "///" (grammar.md §1.2); any other comment ends a run of them.
                var length = _pos - start;
                if (length >= 3 && _text[start + 2] == '/' && (length == 3 || _text[start + 3] != '/'))
                {
                    _pendingDoc.Add(_text[(start + 3).._pos].TrimStart(' ', '\t'));
                }
                else
                {
                    _pendingDoc.Clear();
                }
            }
            else if (c == '/' && Ch(1) == '*')
            {
                _pendingDoc.Clear();
                var start = _pos;
                var close = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    Error(DiagnosticCodes.UnterminatedBlockComment, start, start + 2, "unterminated block comment");
                    _pos = _text.Length;
                }
                else
                {
                    _pos = close + 2;
                }
            }
            else
            {
                return;
            }
        }
    }

    private void LexIdentifier(int start)
    {
        while (IsIdentContinue(Ch()))
        {
            _pos++;
        }

        var text = _text[start.._pos];
        if (text == "_")
        {
            Add(TokenKind.Underscore, start);
        }
        else if (Keywords.Table.TryGetValue(text, out var keyword))
        {
            Add(keyword, start);
        }
        else
        {
            Add(TokenKind.Identifier, start, _symbols.Intern(text).Id);
        }
    }

    private void LexNumber(int start)
    {
        var radix = 10;
        if (_text[_pos] == '0' && Ch(1) is 'x' or 'b' or 'o')
        {
            radix = Ch(1) switch { 'x' => 16, 'b' => 2, _ => 8 };
            _pos += 2;
        }

        var digitsStart = _pos;
        while (Ch() == '_' || (radix == 16 ? IsHexDigit(Ch()) : IsDecimalDigit(Ch())))
        {
            _pos++;
        }
        var digitsEnd = _pos;

        var isFloat = false;
        if (radix == 10)
        {
            if (Ch() == '.' && IsDecimalDigit(Ch(1)))
            {
                isFloat = true;
                _pos++;
                SkipDecimalDigits();
            }
            if (Ch() is 'e' or 'E' && (IsDecimalDigit(Ch(1)) || (Ch(1) is '+' or '-' && IsDecimalDigit(Ch(2)))))
            {
                isFloat = true;
                _pos += IsDecimalDigit(Ch(1)) ? 1 : 2;
                SkipDecimalDigits();
            }
        }

        var bodyEnd = _pos;
        while (IsIdentContinue(Ch()))
        {
            _pos++;
        }
        var suffix = _text[bodyEnd.._pos];

        if (digitsStart == digitsEnd)
        {
            // One error per literal: "0xg" reports the missing digits, not also the suffix "g".
            Error(DiagnosticCodes.InvalidDigit, start, digitsEnd, $"expected digits after '{_text[start..digitsStart]}'");
            suffix = "";
        }
        else
        {
            CheckSeparators(digitsStart, bodyEnd, radix);
        }

        if (isFloat)
        {
            var floatSuffix = suffix switch
            {
                "" => FloatSuffix.None,
                "f32" => FloatSuffix.F32,
                "f64" => FloatSuffix.F64,
                _ => ReportBadSuffix(bodyEnd, suffix, FloatSuffix.None),
            };
            var digits = _text[start..bodyEnd].Replace("_", "", StringComparison.Ordinal);
            _floats.Add(new FloatLiteralValue(digits, floatSuffix));
            Add(TokenKind.FloatLiteral, start, _floats.Count - 1);
            return;
        }

        var intSuffix = suffix switch
        {
            "" => IntSuffix.None,
            "i8" => IntSuffix.I8,
            "i16" => IntSuffix.I16,
            "i32" => IntSuffix.I32,
            "i64" => IntSuffix.I64,
            "isize" => IntSuffix.ISize,
            "u8" => IntSuffix.U8,
            "u16" => IntSuffix.U16,
            "u32" => IntSuffix.U32,
            "u64" => IntSuffix.U64,
            "usize" => IntSuffix.USize,
            _ => ReportBadSuffix(bodyEnd, suffix, IntSuffix.None),
        };
        var value = ComputeIntValue(start, digitsStart, digitsEnd, radix);
        _ints.Add(new IntLiteralValue(value, intSuffix));
        Add(TokenKind.IntLiteral, start, _ints.Count - 1);
    }

    private void SkipDecimalDigits()
    {
        while (Ch() == '_' || IsDecimalDigit(Ch()))
        {
            _pos++;
        }
    }

    private T ReportBadSuffix<T>(int suffixStart, string suffix, T fallback)
    {
        Error(DiagnosticCodes.InvalidLiteralSuffix, suffixStart, _pos, $"invalid literal suffix '{suffix}'");
        return fallback;
    }

    /// <summary>A '_' must sit between two digits of the literal's radix (grammar.md §1.4).</summary>
    private void CheckSeparators(int start, int end, int radix)
    {
        for (var i = start; i < end; i++)
        {
            if (_text[i] != '_')
            {
                continue;
            }
            var before = i > start && IsRadixDigit(_text[i - 1], radix);
            var after = i + 1 < end && IsRadixDigit(_text[i + 1], radix);
            if (!before || !after)
            {
                Error(DiagnosticCodes.MisplacedDigitSeparator, i, i + 1, "digit separator '_' must be between two digits");
                return;
            }
        }
    }

    private ulong ComputeIntValue(int literalStart, int digitsStart, int digitsEnd, int radix)
    {
        ulong value = 0;
        var overflowed = false;
        for (var i = digitsStart; i < digitsEnd; i++)
        {
            var c = _text[i];
            if (c == '_')
            {
                continue;
            }
            var digit = (ulong)HexValue(c);
            if (digit >= (ulong)radix)
            {
                Error(DiagnosticCodes.InvalidDigit, i, i + 1, $"invalid digit '{c}' in base-{radix} literal");
                return 0;
            }
            if (!overflowed)
            {
                try
                {
                    value = checked((value * (ulong)radix) + digit);
                }
                catch (OverflowException)
                {
                    overflowed = true;
                }
            }
        }
        if (overflowed)
        {
            Error(DiagnosticCodes.IntegerLiteralTooLarge, literalStart, digitsEnd, "integer literal is too large for 64 bits");
            return 0;
        }
        return value;
    }

    private void LexString(int start)
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (true)
        {
            var c = Ch();
            if (c is Eof or '\n' or '\r')
            {
                Error(DiagnosticCodes.UnterminatedString, start, _pos, "unterminated string literal");
                break;
            }
            if (c == '"')
            {
                _pos++;
                break;
            }
            if (c == '\\')
            {
                var scalar = LexEscape();
                if (scalar >= 0)
                {
                    sb.Append(char.ConvertFromUtf32(scalar));
                }
            }
            else
            {
                sb.Append((char)c);
                _pos++;
            }
        }
        _strings.Add(sb.ToString());
        Add(TokenKind.StringLiteral, start, _strings.Count - 1);
    }

    private void LexChar(int start)
    {
        _pos++; // opening quote
        var value = 0;
        var c = Ch();
        if (c == '\'')
        {
            _pos++;
            Error(DiagnosticCodes.InvalidCharLiteral, start, _pos, "empty char literal");
        }
        else if (c is Eof or '\n' or '\r')
        {
            Error(DiagnosticCodes.InvalidCharLiteral, start, _pos, "unterminated char literal");
        }
        else
        {
            if (c == '\\')
            {
                value = Math.Max(0, LexEscape());
            }
            else if (char.IsHighSurrogate((char)c) && Ch(1) >= 0 && char.IsLowSurrogate((char)Ch(1)))
            {
                value = char.ConvertToUtf32((char)c, (char)Ch(1));
                _pos += 2;
            }
            else
            {
                value = c;
                _pos++;
            }

            if (Ch() == '\'')
            {
                _pos++;
            }
            else
            {
                // Recover by skipping to a closing quote on the same line, if there is one.
                var scan = _pos;
                while (scan < _text.Length && _text[scan] is not ('\'' or '\n' or '\r'))
                {
                    scan++;
                }
                var closed = scan < _text.Length && _text[scan] == '\'';
                _pos = closed ? scan + 1 : scan;
                Error(DiagnosticCodes.InvalidCharLiteral, start, _pos,
                    closed ? "char literal must contain exactly one character" : "unterminated char literal");
            }
        }
        _chars.Add(value);
        Add(TokenKind.CharLiteral, start, _chars.Count - 1);
    }

    /// <summary>Lexes an escape starting at '\'. Returns the scalar value, or -1 after reporting an error.</summary>
    private int LexEscape()
    {
        var start = _pos;
        _pos++; // backslash
        var e = Ch();
        int simple = e switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '0' => 0,
            _ => -1,
        };
        if (simple >= 0)
        {
            _pos++;
            return simple;
        }

        if (e == 'u' && Ch(1) == '{')
        {
            _pos += 2;
            var digitsStart = _pos;
            while (IsHexDigit(Ch()))
            {
                _pos++;
            }
            var count = _pos - digitsStart;
            if (Ch() == '}' && count is >= 1 and <= 6)
            {
                var value = int.Parse(_text.AsSpan(digitsStart, count), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                _pos++;
                if (value <= 0x10FFFF && value is not (>= 0xD800 and <= 0xDFFF))
                {
                    return value;
                }
                Error(DiagnosticCodes.InvalidEscape, start, _pos, "\\u{...} escape is not a Unicode scalar value");
                return -1;
            }
            Error(DiagnosticCodes.InvalidEscape, start, _pos, "\\u escape must be \\u{H} with 1 to 6 hex digits");
            return -1;
        }

        if (e is not (Eof or '\n' or '\r'))
        {
            _pos++;
        }
        Error(DiagnosticCodes.InvalidEscape, start, _pos, "invalid escape sequence");
        return -1;
    }

    private bool TryLexPunctuation(int start, char c)
    {
        TokenKind kind;
        var next = Ch(1);
        switch (c)
        {
            case '(': kind = TokenKind.OpenParen; break;
            case ')': kind = TokenKind.CloseParen; break;
            case '{': kind = TokenKind.OpenBrace; break;
            case '}': kind = TokenKind.CloseBrace; break;
            case '[': kind = TokenKind.OpenBracket; break;
            case ']': kind = TokenKind.CloseBracket; break;
            case ',': kind = TokenKind.Comma; break;
            case ';': kind = TokenKind.Semicolon; break;
            case ':': kind = TokenKind.Colon; break;
            case '@': kind = TokenKind.At; break;
            case '?': kind = TokenKind.Question; break;
            case '~': kind = TokenKind.Tilde; break;
            case '.': kind = next == '.' ? TokenKind.DotDot : TokenKind.Dot; break;
            case '-': kind = next switch { '>' => TokenKind.Arrow, '=' => TokenKind.MinusEquals, _ => TokenKind.Minus }; break;
            case '=': kind = next switch { '>' => TokenKind.FatArrow, '=' => TokenKind.EqualsEquals, _ => TokenKind.Equals }; break;
            case '+': kind = next == '=' ? TokenKind.PlusEquals : TokenKind.Plus; break;
            case '*': kind = next == '=' ? TokenKind.StarEquals : TokenKind.Star; break;
            case '/': kind = next == '=' ? TokenKind.SlashEquals : TokenKind.Slash; break;
            case '%': kind = next == '=' ? TokenKind.PercentEquals : TokenKind.Percent; break;
            case '^': kind = next == '=' ? TokenKind.CaretEquals : TokenKind.Caret; break;
            case '!': kind = next == '=' ? TokenKind.BangEquals : TokenKind.Bang; break;
            case '&': kind = next switch { '&' => TokenKind.AmpAmp, '=' => TokenKind.AmpEquals, _ => TokenKind.Amp }; break;
            case '|': kind = next switch { '|' => TokenKind.PipePipe, '=' => TokenKind.PipeEquals, _ => TokenKind.Pipe }; break;
            case '<':
                kind = next switch
                {
                    '<' => Ch(2) == '=' ? TokenKind.LessLessEquals : TokenKind.LessLess,
                    '=' => TokenKind.LessEquals,
                    _ => TokenKind.Less,
                };
                break;
            // Never '>>' or '>>=': the parser joins adjacent '>' tokens (grammar.md §1.7).
            case '>': kind = next == '=' ? TokenKind.GreaterEquals : TokenKind.Greater; break;
            default: return false;
        }

        _pos += TokenFacts.Text(kind).Length;
        Add(kind, start);
        return true;
    }

    private string DescribeChar(int start)
    {
        var text = _text[start.._pos];
        var scalar = char.ConvertToUtf32(text, 0);
        return scalar is >= 0x20 and < 0x7F
            ? $"'{text}'"
            : $"U+{scalar.ToString("X4", CultureInfo.InvariantCulture)}";
    }

    private static bool IsIdentStart(int c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsIdentContinue(int c) => IsIdentStart(c) || IsDecimalDigit(c);

    private static bool IsDecimalDigit(int c) => c is >= '0' and <= '9';

    private static bool IsHexDigit(int c) => IsDecimalDigit(c) || c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static bool IsRadixDigit(char c, int radix) => radix == 16 ? IsHexDigit(c) : IsDecimalDigit(c);

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };
}
