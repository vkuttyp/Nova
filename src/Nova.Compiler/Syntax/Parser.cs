using System.Collections.Immutable;
using Nova.Compiler.Diagnostics;

namespace Nova.Compiler.Syntax;

/// <summary>
/// Recursive-descent parser for the M1 grammar (docs/grammar.md §2), with Pratt-style binary
/// expressions and the error-recovery rules of grammar.md §3: after the first error the parser
/// stays silent until it re-synchronises at a statement or item boundary.
/// </summary>
public sealed class Parser
{
    private const int ComparisonPrecedence = 3;
    private const string MissingName = "<missing>";

    private readonly LexedSource _stream;
    private readonly IReadOnlyList<Token> _tokens;
    private readonly SourceText _source;
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;

    private int _pos;
    private bool _recovering;

    private Parser(LexedSource stream, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _stream = stream;
        _tokens = stream.Tokens;
        _source = stream.Source;
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    public static CompilationUnit Parse(LexedSource stream, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new Parser(stream, symbols, diagnostics).ParseCompilationUnit();
    }

    /// <summary>Lexes and parses one source file.</summary>
    public static CompilationUnit ParseText(SourceText source, SymbolTable symbols, DiagnosticBag diagnostics) =>
        Parse(Lexer.Lex(source, symbols, diagnostics), symbols, diagnostics);

    // ---- Token access ----

    private Token Current => _tokens[_pos];

    private Token Previous => _tokens[Math.Max(0, _pos - 1)];

    private Token Peek(int offset) => _tokens[Math.Min(_pos + offset, _tokens.Count - 1)];

    private bool At(TokenKind kind) => Current.Kind == kind;

    private Token Advance()
    {
        var token = Current;
        if (token.Kind != TokenKind.EndOfFile)
        {
            _pos++;
        }
        return token;
    }

    private bool Eat(TokenKind kind)
    {
        if (!At(kind))
        {
            return false;
        }
        Advance();
        return true;
    }

    /// <summary>Consumes a token of the given kind, or reports N0100 and returns a zero-width stand-in.</summary>
    private Token Expect(TokenKind kind)
    {
        if (At(kind))
        {
            return Advance();
        }

        // A missing ';' is reported right after the previous token, where it belongs.
        var span = kind == TokenKind.Semicolon && _pos > 0
            ? new TextSpan(Previous.Span.End, 0)
            : Current.Span;
        Error(DiagnosticCodes.ExpectedToken, span,
            $"expected {TokenFacts.Describe(kind)}, found {TokenFacts.Describe(Current.Kind)}");
        return new Token(kind, new TextSpan(span.Start, 0));
    }

    private void Error(string code, TextSpan span, string message)
    {
        if (_recovering)
        {
            return;
        }
        _recovering = true;
        _diagnostics.Error(code, _source, span, message);
    }

    /// <summary>Span from <paramref name="start"/> to the end of the last consumed token.</summary>
    private TextSpan SpanFrom(int start) => TextSpan.FromBounds(start, Math.Max(start, Previous.Span.End));

    // ---- Items ----

    private CompilationUnit ParseCompilationUnit()
    {
        NamespaceDecl? ns = null;
        if (At(TokenKind.NamespaceKeyword))
        {
            ns = ParseNamespace();
            SynchronizeItem();
        }

        var items = ImmutableArray.CreateBuilder<Item>();
        while (!At(TokenKind.EndOfFile))
        {
            var startPos = _pos;
            if (At(TokenKind.NamespaceKeyword))
            {
                Error(DiagnosticCodes.MisplacedNamespace, Current.Span,
                    "a namespace declaration must be the first declaration in the file");
                ParseNamespace();
            }
            else if (IsItemStart(Current.Kind))
            {
                var item = ParseItem();
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            else
            {
                Error(DiagnosticCodes.ExpectedItem, Current.Span,
                    $"expected 'fn' or 'const', found {TokenFacts.Describe(Current.Kind)}");
                Advance();
            }

            SynchronizeItem();
            if (_pos == startPos)
            {
                Advance();
            }
        }

        return new CompilationUnit(_source, ns, items.ToImmutable(), new TextSpan(0, _source.Length));
    }

    private NamespaceDecl ParseNamespace()
    {
        var start = Advance().Span.Start;
        var name = ParseQualifiedName();
        Expect(TokenKind.Semicolon);
        return new NamespaceDecl(name, SpanFrom(start));
    }

    private static bool IsItemStart(TokenKind kind) => kind is TokenKind.FnKeyword or TokenKind.ConstKeyword
        or TokenKind.PublicKeyword or TokenKind.InternalKeyword or TokenKind.PrivateKeyword;

    /// <summary>Keywords that begin a declaration in some milestone; top-level recovery stops at them.</summary>
    private static bool IsDeclarationKeyword(TokenKind kind) => IsItemStart(kind) || kind is TokenKind.NamespaceKeyword
        or TokenKind.StructKeyword or TokenKind.ClassKeyword or TokenKind.EnumKeyword or TokenKind.InterfaceKeyword
        or TokenKind.ImplKeyword or TokenKind.LetKeyword or TokenKind.VarKeyword or TokenKind.StaticKeyword
        or TokenKind.ExternKeyword or TokenKind.ImportKeyword or TokenKind.UsingKeyword or TokenKind.TypeKeyword;

    private void SynchronizeItem()
    {
        if (!_recovering)
        {
            return;
        }
        // Skip balanced { } groups, so keywords inside a skipped body do not restart parsing.
        var depth = 0;
        while (!At(TokenKind.EndOfFile))
        {
            var kind = Current.Kind;
            if (depth == 0 && IsDeclarationKeyword(kind))
            {
                break;
            }
            Advance();
            if (kind == TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (kind == TokenKind.CloseBrace && depth > 0)
            {
                depth--;
            }
        }
        _recovering = false;
    }

    private Item? ParseItem()
    {
        var start = Current.Span.Start;
        var visibility = Current.Kind switch
        {
            TokenKind.PublicKeyword => Visibility.Public,
            TokenKind.InternalKeyword => Visibility.Internal,
            TokenKind.PrivateKeyword => Visibility.Private,
            _ => Visibility.Default,
        };
        if (visibility != Visibility.Default)
        {
            Advance();
        }

        if (At(TokenKind.FnKeyword))
        {
            return ParseFunction(visibility, start);
        }
        if (At(TokenKind.ConstKeyword))
        {
            return ParseConst(visibility, start);
        }

        Error(DiagnosticCodes.ExpectedItem, Current.Span,
            $"expected 'fn' or 'const', found {TokenFacts.Describe(Current.Kind)}");
        // Consume the offender: recovery stops at declaration keywords, so leaving e.g. `let` would re-report it.
        Advance();
        return null;
    }

    private FunctionDecl ParseFunction(Visibility visibility, int start)
    {
        Expect(TokenKind.FnKeyword);
        var name = ParseIdentifier();

        Expect(TokenKind.OpenParen);
        var parameters = ImmutableArray.CreateBuilder<Parameter>();
        while (!At(TokenKind.CloseParen) && !At(TokenKind.EndOfFile) && !At(TokenKind.OpenBrace))
        {
            var paramStart = Current.Span.Start;
            var paramName = ParseIdentifier();
            Expect(TokenKind.Colon);
            var type = ParseType();
            parameters.Add(new Parameter(paramName, type, SpanFrom(paramStart)));
            if (!Eat(TokenKind.Comma))
            {
                break;
            }
        }
        Expect(TokenKind.CloseParen);

        var returnType = Eat(TokenKind.Arrow) ? ParseType() : null;

        BlockStmt? body = null;
        Expr? expressionBody = null;
        if (At(TokenKind.OpenBrace))
        {
            body = ParseBlock();
        }
        else if (Eat(TokenKind.FatArrow))
        {
            expressionBody = ParseExpr();
            Expect(TokenKind.Semicolon);
        }
        else
        {
            Error(DiagnosticCodes.ExpectedToken, Current.Span,
                $"expected '{{' or '=>', found {TokenFacts.Describe(Current.Kind)}");
            expressionBody = new ErrorExpr(new TextSpan(Current.Span.Start, 0));
        }

        return new FunctionDecl(visibility, name, parameters.ToImmutable(), returnType, body, expressionBody, SpanFrom(start));
    }

    private ConstDecl ParseConst(Visibility visibility, int start)
    {
        Expect(TokenKind.ConstKeyword);
        var name = ParseIdentifier();
        Expect(TokenKind.Colon);
        var type = ParseType();
        Expect(TokenKind.Equals);
        var value = ParseExpr();
        Expect(TokenKind.Semicolon);
        return new ConstDecl(visibility, name, type, value, SpanFrom(start));
    }

    // ---- Names and types ----

    private Identifier ParseIdentifier()
    {
        if (At(TokenKind.Identifier))
        {
            var token = Advance();
            return new Identifier(new Symbol(token.Data), token.Span);
        }
        Error(DiagnosticCodes.ExpectedToken, Current.Span,
            $"expected identifier, found {TokenFacts.Describe(Current.Kind)}");
        return new Identifier(_symbols.Intern(MissingName), new TextSpan(Current.Span.Start, 0)) { IsMissing = true };
    }

    private QualifiedName ParseQualifiedName()
    {
        var start = Current.Span.Start;
        var parts = ImmutableArray.CreateBuilder<Identifier>();
        parts.Add(ParseIdentifier());
        while (At(TokenKind.Dot) && Peek(1).Kind == TokenKind.Identifier)
        {
            Advance();
            parts.Add(ParseIdentifier());
        }
        return new QualifiedName(parts.ToImmutable(), SpanFrom(start));
    }

    private TypeSyntax ParseType()
    {
        if (At(TokenKind.Identifier))
        {
            var name = ParseQualifiedName();
            return new NamedType(name, name.Span);
        }
        Error(DiagnosticCodes.ExpectedType, Current.Span, $"expected type, found {TokenFacts.Describe(Current.Kind)}");
        return new MissingType(new TextSpan(Current.Span.Start, 0));
    }

    // ---- Statements ----

    private static bool IsStatementKeyword(TokenKind kind) => kind is TokenKind.LetKeyword or TokenKind.VarKeyword
        or TokenKind.IfKeyword or TokenKind.WhileKeyword or TokenKind.LoopKeyword or TokenKind.ReturnKeyword
        or TokenKind.BreakKeyword or TokenKind.ContinueKeyword;

    /// <summary>Tokens that cannot start a statement but do start an item: a block ends before them.</summary>
    private static bool EndsBlock(TokenKind kind) => kind is TokenKind.CloseBrace or TokenKind.EndOfFile
        or TokenKind.FnKeyword or TokenKind.PublicKeyword or TokenKind.InternalKeyword or TokenKind.PrivateKeyword;

    private BlockStmt ParseBlock()
    {
        var start = Current.Span.Start;
        if (!At(TokenKind.OpenBrace))
        {
            Expect(TokenKind.OpenBrace);
            return new BlockStmt([], new TextSpan(start, 0));
        }
        Advance();

        var statements = ImmutableArray.CreateBuilder<Stmt>();
        while (!EndsBlock(Current.Kind))
        {
            var startPos = _pos;
            statements.Add(ParseStatement());
            SynchronizeStatement();
            if (_pos == startPos)
            {
                Advance();
            }
        }
        Expect(TokenKind.CloseBrace);
        return new BlockStmt(statements.ToImmutable(), SpanFrom(start));
    }

    private void SynchronizeStatement()
    {
        if (!_recovering)
        {
            return;
        }
        // A statement that failed but still ended at a boundary needs no skipping.
        if (Previous.Kind is not (TokenKind.Semicolon or TokenKind.CloseBrace))
        {
            // Skip balanced { } groups, so a broken `if (x = 1) { ... }` does not end the enclosing block.
            var depth = 0;
            while (!At(TokenKind.EndOfFile))
            {
                var kind = Current.Kind;
                if (kind is TokenKind.FnKeyword or TokenKind.PublicKeyword or TokenKind.InternalKeyword or TokenKind.PrivateKeyword)
                {
                    break;
                }
                if (depth == 0 && (kind == TokenKind.CloseBrace || IsStatementKeyword(kind)))
                {
                    break;
                }
                Advance();
                if (kind == TokenKind.OpenBrace)
                {
                    depth++;
                }
                else if (kind == TokenKind.CloseBrace && --depth == 0)
                {
                    break;
                }
                else if (kind == TokenKind.Semicolon && depth == 0)
                {
                    break;
                }
            }
        }
        _recovering = false;
    }

    private Stmt ParseStatement()
    {
        var start = Current.Span.Start;
        switch (Current.Kind)
        {
            case TokenKind.OpenBrace:
                return ParseBlock();
            case TokenKind.LetKeyword:
            case TokenKind.VarKeyword:
                return ParseLocalDecl();
            case TokenKind.IfKeyword:
                return ParseIf();
            case TokenKind.WhileKeyword:
            {
                Advance();
                var condition = ParseParenthesizedCondition();
                var body = ParseBlock();
                return new WhileStmt(condition, body, SpanFrom(start));
            }
            case TokenKind.LoopKeyword:
            {
                Advance();
                var body = ParseBlock();
                return new LoopStmt(body, SpanFrom(start));
            }
            case TokenKind.BreakKeyword:
                Advance();
                Expect(TokenKind.Semicolon);
                return new BreakStmt(SpanFrom(start));
            case TokenKind.ContinueKeyword:
                Advance();
                Expect(TokenKind.Semicolon);
                return new ContinueStmt(SpanFrom(start));
            case TokenKind.ReturnKeyword:
            {
                Advance();
                var value = At(TokenKind.Semicolon) || At(TokenKind.CloseBrace) ? null : ParseExpr();
                Expect(TokenKind.Semicolon);
                return new ReturnStmt(value, SpanFrom(start));
            }
            default:
                return ParseExpressionOrAssignment();
        }
    }

    private LocalDeclStmt ParseLocalDecl()
    {
        var start = Current.Span.Start;
        var isMutable = Advance().Kind == TokenKind.VarKeyword;
        var name = ParseIdentifier();
        var type = Eat(TokenKind.Colon) ? ParseType() : null;
        var initializer = Eat(TokenKind.Equals) ? ParseExpr() : null;
        Expect(TokenKind.Semicolon);
        return new LocalDeclStmt(isMutable, name, type, initializer, SpanFrom(start));
    }

    private IfStmt ParseIf()
    {
        var start = Current.Span.Start;
        Advance();
        var condition = ParseParenthesizedCondition();
        var then = ParseBlock();
        Stmt? elseBranch = null;
        if (Eat(TokenKind.ElseKeyword))
        {
            elseBranch = At(TokenKind.IfKeyword) ? ParseIf() : ParseBlock();
        }
        return new IfStmt(condition, then, elseBranch, SpanFrom(start));
    }

    private Expr ParseParenthesizedCondition()
    {
        Expect(TokenKind.OpenParen);
        var condition = ParseExpr();
        Expect(TokenKind.CloseParen);
        return condition;
    }

    private Stmt ParseExpressionOrAssignment()
    {
        var start = Current.Span.Start;
        var expr = ParseExpr();

        if (TryPeekAssignOp(out var op, out var tokenCount))
        {
            if (expr is not (NameExpr or MemberExpr or ErrorExpr))
            {
                Error(DiagnosticCodes.InvalidAssignmentTarget, expr.Span,
                    "invalid assignment target; only a variable or member can be assigned");
            }
            for (var i = 0; i < tokenCount; i++)
            {
                Advance();
            }
            var value = ParseExpr();
            Expect(TokenKind.Semicolon);
            return new AssignStmt(expr, op, value, SpanFrom(start));
        }

        Expect(TokenKind.Semicolon);
        return new ExprStmt(expr, SpanFrom(start));
    }

    private bool TryPeekAssignOp(out AssignOp op, out int tokenCount)
    {
        tokenCount = 1;
        switch (Current.Kind)
        {
            case TokenKind.Equals: op = AssignOp.Assign; return true;
            case TokenKind.PlusEquals: op = AssignOp.Add; return true;
            case TokenKind.MinusEquals: op = AssignOp.Subtract; return true;
            case TokenKind.StarEquals: op = AssignOp.Multiply; return true;
            case TokenKind.SlashEquals: op = AssignOp.Divide; return true;
            case TokenKind.PercentEquals: op = AssignOp.Remainder; return true;
            case TokenKind.AmpEquals: op = AssignOp.BitAnd; return true;
            case TokenKind.PipeEquals: op = AssignOp.BitOr; return true;
            case TokenKind.CaretEquals: op = AssignOp.BitXor; return true;
            case TokenKind.LessLessEquals: op = AssignOp.ShiftLeft; return true;
            case TokenKind.Greater when IsAdjacent(TokenKind.GreaterEquals):
                op = AssignOp.ShiftRight;
                tokenCount = 2;
                return true;
            default:
                op = default;
                return false;
        }
    }

    /// <summary>True if the token after Current has the given kind and touches Current (no space between).</summary>
    private bool IsAdjacent(TokenKind next) => Peek(1).Kind == next && Peek(1).Span.Start == Current.Span.End;

    // ---- Expressions ----

    private Expr ParseExpr() => ParseBinary(1);

    private Expr ParseBinary(int minPrecedence)
    {
        var start = Current.Span.Start;
        var left = ParseUnary();
        var leftIsComparison = false;

        while (true)
        {
            if (At(TokenKind.AsKeyword))
            {
                Advance();
                var type = ParseType();
                left = new CastExpr(left, type, SpanFrom(start));
                leftIsComparison = false;
                continue;
            }

            if (!TryPeekBinaryOp(out var op, out var precedence, out var tokenCount) || precedence < minPrecedence)
            {
                return left;
            }

            if (precedence == ComparisonPrecedence && leftIsComparison)
            {
                Error(DiagnosticCodes.ChainedComparison, TextSpan.FromBounds(Current.Span.Start, Peek(tokenCount - 1).Span.End),
                    "comparison operators cannot be chained; combine the comparisons with '&&'");
            }

            for (var i = 0; i < tokenCount; i++)
            {
                Advance();
            }
            var right = ParseBinary(precedence + 1);
            left = new BinaryExpr(left, op, right, SpanFrom(start));
            leftIsComparison = precedence == ComparisonPrecedence;
        }
    }

    private bool TryPeekBinaryOp(out BinaryOp op, out int precedence, out int tokenCount)
    {
        tokenCount = 1;
        (op, precedence) = Current.Kind switch
        {
            TokenKind.PipePipe => (BinaryOp.LogicalOr, 1),
            TokenKind.AmpAmp => (BinaryOp.LogicalAnd, 2),
            TokenKind.EqualsEquals => (BinaryOp.Equal, ComparisonPrecedence),
            TokenKind.BangEquals => (BinaryOp.NotEqual, ComparisonPrecedence),
            TokenKind.Less => (BinaryOp.Less, ComparisonPrecedence),
            TokenKind.LessEquals => (BinaryOp.LessEqual, ComparisonPrecedence),
            TokenKind.GreaterEquals => (BinaryOp.GreaterEqual, ComparisonPrecedence),
            TokenKind.Pipe => (BinaryOp.BitOr, 4),
            TokenKind.Caret => (BinaryOp.BitXor, 5),
            TokenKind.Amp => (BinaryOp.BitAnd, 6),
            TokenKind.LessLess => (BinaryOp.ShiftLeft, 7),
            TokenKind.Plus => (BinaryOp.Add, 8),
            TokenKind.Minus => (BinaryOp.Subtract, 8),
            TokenKind.Star => (BinaryOp.Multiply, 9),
            TokenKind.Slash => (BinaryOp.Divide, 9),
            TokenKind.Percent => (BinaryOp.Remainder, 9),
            _ => (default(BinaryOp), 0),
        };
        if (precedence > 0)
        {
            return true;
        }

        if (At(TokenKind.Greater))
        {
            if (IsAdjacent(TokenKind.GreaterEquals))
            {
                return false; // '>>=' is an assignment operator, not a binary operator
            }
            if (IsAdjacent(TokenKind.Greater))
            {
                (op, precedence, tokenCount) = (BinaryOp.ShiftRight, 7, 2);
                return true;
            }
            (op, precedence) = (BinaryOp.Greater, ComparisonPrecedence);
            return true;
        }
        return false;
    }

    private Expr ParseUnary()
    {
        var start = Current.Span.Start;
        if (At(TokenKind.Minus) || At(TokenKind.Bang))
        {
            var op = Advance().Kind == TokenKind.Minus ? UnaryOp.Negate : UnaryOp.Not;
            var operand = ParseUnary();
            return new UnaryExpr(op, operand, SpanFrom(start));
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var start = Current.Span.Start;
        var expr = ParsePrimary();
        while (true)
        {
            if (At(TokenKind.OpenParen))
            {
                Advance();
                var arguments = ImmutableArray.CreateBuilder<Expr>();
                while (!At(TokenKind.CloseParen) && !At(TokenKind.EndOfFile))
                {
                    arguments.Add(ParseExpr());
                    if (!Eat(TokenKind.Comma))
                    {
                        break;
                    }
                }
                Expect(TokenKind.CloseParen);
                expr = new CallExpr(expr, arguments.ToImmutable(), SpanFrom(start));
            }
            else if (At(TokenKind.Dot))
            {
                Advance();
                var member = ParseIdentifier();
                expr = new MemberExpr(expr, member, SpanFrom(start));
            }
            else
            {
                return expr;
            }
        }
    }

    private Expr ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.IntLiteral:
            {
                Advance();
                var value = _stream.Ints[token.Data];
                return new IntLiteralExpr(value.Value, value.Suffix, token.Span);
            }
            case TokenKind.FloatLiteral:
            {
                Advance();
                var value = _stream.Floats[token.Data];
                return new FloatLiteralExpr(value.Digits, value.Suffix, token.Span);
            }
            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpr(_stream.Strings[token.Data], token.Span);
            case TokenKind.CharLiteral:
                Advance();
                return new CharLiteralExpr(_stream.Chars[token.Data], token.Span);
            case TokenKind.TrueKeyword:
            case TokenKind.FalseKeyword:
                Advance();
                return new BoolLiteralExpr(token.Kind == TokenKind.TrueKeyword, token.Span);
            case TokenKind.Identifier:
            {
                var name = ParseIdentifier();
                var typeArguments = IsGenericArgumentListAhead() ? ParseGenericArguments() : [];
                return new NameExpr(name, typeArguments, SpanFrom(token.Span.Start));
            }
            case TokenKind.OpenParen:
            {
                Advance();
                var inner = ParseExpr();
                Expect(TokenKind.CloseParen);
                return new ParenExpr(inner, SpanFrom(token.Span.Start));
            }
            default:
                Error(DiagnosticCodes.ExpectedExpression, token.Span,
                    $"expected expression, found {TokenFacts.Describe(token.Kind)}");
                // Leave closing delimiters and terminators for the enclosing construct.
                if (token.Kind is TokenKind.Semicolon or TokenKind.CloseParen or TokenKind.CloseBrace
                    or TokenKind.CloseBracket or TokenKind.Comma or TokenKind.EndOfFile)
                {
                    return new ErrorExpr(new TextSpan(token.Span.Start, 0));
                }
                Advance();
                return new ErrorExpr(token.Span);
        }
    }

    /// <summary>
    /// After an identifier: is the input <c>'&lt;' Type (',' Type)* '&gt;' '('</c>? Only then is '&lt;'
    /// the start of generic arguments rather than less-than (grammar.md §2.1).
    /// </summary>
    private bool IsGenericArgumentListAhead()
    {
        if (!At(TokenKind.Less))
        {
            return false;
        }
        var i = 1;
        while (true)
        {
            if (Peek(i).Kind != TokenKind.Identifier)
            {
                return false;
            }
            i++;
            while (Peek(i).Kind == TokenKind.Dot && Peek(i + 1).Kind == TokenKind.Identifier)
            {
                i += 2;
            }
            if (Peek(i).Kind == TokenKind.Comma)
            {
                i++;
                continue;
            }
            return Peek(i).Kind == TokenKind.Greater && Peek(i + 1).Kind == TokenKind.OpenParen;
        }
    }

    private ImmutableArray<TypeSyntax> ParseGenericArguments()
    {
        Expect(TokenKind.Less);
        var arguments = ImmutableArray.CreateBuilder<TypeSyntax>();
        do
        {
            arguments.Add(ParseType());
        }
        while (Eat(TokenKind.Comma));
        Expect(TokenKind.Greater);
        return arguments.ToImmutable();
    }
}
