using Nova.Compiler.Diagnostics;
using Nova.Compiler.Syntax;
using static Nova.Compiler.Tests.TestHelpers;

namespace Nova.Compiler.Tests;

public class ParserTests
{
    // One case per adjacent precedence pair of grammar.md §2.1, in both orders where it matters.
    [Theory]
    [InlineData("a || b && c", "(|| a (&& b c))")]
    [InlineData("a && b || c", "(|| (&& a b) c)")]
    [InlineData("a && b == c", "(&& a (== b c))")]
    [InlineData("a == b | c", "(== a (| b c))")]
    [InlineData("a | b ^ c", "(| a (^ b c))")]
    [InlineData("a ^ b & c", "(^ a (& b c))")]
    [InlineData("a & b << c", "(& a (<< b c))")]
    [InlineData("a << b + c", "(<< a (+ b c))")]
    [InlineData("a >> b - c", "(>> a (- b c))")]
    [InlineData("a + b * c", "(+ a (* b c))")]
    [InlineData("a * b + c", "(+ (* a b) c)")]
    [InlineData("a - b - c", "(- (- a b) c)")]
    [InlineData("a / b % c", "(% (/ a b) c)")]
    [InlineData("a * b as u64", "(* a (as b u64))")]
    [InlineData("-a as i64", "(as (neg a) i64)")]
    [InlineData("x as u32 as u64", "(as (as x u32) u64)")]
    [InlineData("- -a", "(neg (neg a))")]
    [InlineData("!a && b", "(&& (not a) b)")]
    [InlineData("-f(x).y", "(neg (. (call f x) y))")]
    [InlineData("(a + b) * c", "(* (paren (+ a b)) c)")]
    [InlineData("x & 1 == 0", "(== (& x 1) 0)")]
    [InlineData("a < b && c > d", "(&& (< a b) (> c d))")]
    [InlineData("a >= b", "(>= a b)")]
    public void Expression_precedence(string input, string expected) =>
        Assert.Equal(expected, ParseExpr(input));

    [Fact]
    public void Separated_greater_than_tokens_are_not_a_shift()
    {
        var (_, _, diagnostics) = Parse("fn f() { let x = a > > b; }");
        Assert.Equal([DiagnosticCodes.ExpectedExpression], Codes(diagnostics));
    }

    [Theory]
    [InlineData("truncate<i64>(x)", "(call (generic truncate i64) x)")]
    [InlineData("convert<A.B, C>(x, y)", "(call (generic convert A.B C) x y)")]
    [InlineData("a < b", "(< a b)")]
    [InlineData("(a < b) > (c)", "(> (paren (< a b)) (paren c))")]
    public void Generic_arguments_require_a_following_call(string input, string expected) =>
        Assert.Equal(expected, ParseExpr(input));

    [Theory]
    [InlineData("42", "42")]
    [InlineData("42u8", "42u8")]
    [InlineData("1.5f32", "1.5f32")]
    [InlineData("\"a\\nb\"", "\"a\\nb\"")]
    [InlineData("'\\u{e9}'", "'\\u{E9}'")]
    [InlineData("true", "true")]
    [InlineData("f()", "(call f)")]
    [InlineData("f(a, b,)", "(call f a b)")]
    [InlineData("Console.writeLine(\"hi\")", "(call (. Console writeLine) \"hi\")")]
    public void Primary_and_postfix_expressions(string input, string expected) =>
        Assert.Equal(expected, ParseExpr(input));

    [Theory]
    [InlineData("a < b < c")]
    [InlineData("a == b != c")]
    [InlineData("a <= b >= c")]
    public void Comparisons_cannot_be_chained(string input)
    {
        var (_, _, diagnostics) = Parse($"fn f() {{ let x = {input}; }}");
        Assert.Equal([DiagnosticCodes.ChainedComparison], Codes(diagnostics));
    }

    [Fact]
    public void Shift_right_assignment_is_formed_from_adjacent_tokens()
    {
        var (unit, symbols, diagnostics) = Parse("fn f() { x >>= 1; y > >= 2; }");
        // 'y > >= 2' is not '>>=': the expression 'y > (error)' fails.
        Assert.Equal([DiagnosticCodes.ExpectedExpression], Codes(diagnostics));
        var printed = AstPrinter.Print(unit, symbols);
        Assert.Contains("(>>= x 1)", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_semicolon_is_reported_after_the_previous_token()
    {
        var (_, _, diagnostics) = Parse("fn f() {\n    let x = 1\n    let y = 2;\n}");
        var diagnostic = Assert.Single(diagnostics.Items);
        Assert.Equal(DiagnosticCodes.ExpectedToken, diagnostic.Code);
        Assert.Equal(new LinePosition(2, 14), diagnostic.Position);
    }

    [Fact]
    public void Missing_close_brace_before_next_function_recovers_both_functions()
    {
        var (unit, _, diagnostics) = Parse("fn a() { let x = 1;\nfn b() { }");
        Assert.Equal([DiagnosticCodes.ExpectedToken], Codes(diagnostics));
        Assert.Equal(2, unit.Items.Length);
    }

    [Fact]
    public void One_diagnostic_per_broken_statement()
    {
        var (unit, _, diagnostics) = Parse("""
            fn f() {
                let = 1;
                return 1 +;
                x = ;
                let ok = 2;
            }
            """);
        Assert.Equal(
            [DiagnosticCodes.ExpectedToken, DiagnosticCodes.ExpectedExpression, DiagnosticCodes.ExpectedExpression],
            Codes(diagnostics));
        var fn = Assert.IsType<FunctionDecl>(Assert.Single(unit.Items));
        Assert.IsType<LocalDeclStmt>(fn.Body!.Statements[^1]);
    }

    [Fact]
    public void Spans_cover_whole_constructs()
    {
        const string text = "fn add(a: i32, b: i32) -> i32 => a + b;";
        var (unit, _, diagnostics) = Parse(text);
        Assert.Empty(diagnostics.Items);
        var fn = Assert.IsType<FunctionDecl>(Assert.Single(unit.Items));
        Assert.Equal(new TextSpan(0, text.Length), fn.Span);
        Assert.Equal("a: i32", text.Substring(fn.Parameters[0].Span.Start, fn.Parameters[0].Span.Length));
        Assert.Equal("a + b", text.Substring(fn.ExpressionBody!.Span.Start, fn.ExpressionBody.Span.Length));
    }

    /// <summary>Random token soup must never hang or crash the parser (grammar.md §3 progress rule).</summary>
    [Fact]
    public void Parser_terminates_on_random_input()
    {
        string[] fragments =
        [
            "fn", "let", "var", "if", "else", "while", "loop", "return", "break", "const", "public", "namespace",
            "(", ")", "{", "}", ",", ";", ":", ".", "->", "=>", "=", "+", "-", "*", "<", ">", ">=", "==", "&&",
            "as", "x", "i32", "1", "\"s\"", "'c'", "#", "/*", "\n",
        ];
        var random = new Random(12345); // fixed seed: deterministic test
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var count = random.Next(1, 40);
            var text = string.Join(" ", Enumerable.Range(0, count).Select(_ => fragments[random.Next(fragments.Length)]));
            var (unit, symbols, _) = Parse(text);
            Assert.NotNull(AstPrinter.Print(unit, symbols));
        }
    }
}
