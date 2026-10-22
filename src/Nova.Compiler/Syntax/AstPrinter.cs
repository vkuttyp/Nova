using System.Globalization;
using System.Text;

namespace Nova.Compiler.Syntax;

/// <summary>
/// Prints an AST as indented S-expressions. The format is stable: snapshot tests
/// (tests/corpus/parse-pass/*.ast) depend on it, so change it only together with the snapshots.
/// </summary>
public sealed class AstPrinter
{
    private readonly SymbolTable _symbols;
    private readonly StringBuilder _sb = new();
    private int _indent;

    private AstPrinter(SymbolTable symbols) => _symbols = symbols;

    public static string Print(CompilationUnit unit, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(symbols);
        var printer = new AstPrinter(symbols);
        printer.PrintUnit(unit);
        return printer._sb.ToString();
    }

    /// <summary>Prints a single expression on one line (used by unit tests).</summary>
    public static string PrintExpr(Expr expr, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(symbols);
        return new AstPrinter(symbols).Expr(expr);
    }

    private void Line(string text)
    {
        _sb.Append(' ', _indent * 2).Append(text).Append('\n');
    }

    private void PrintUnit(CompilationUnit unit)
    {
        Line("(unit");
        _indent++;
        if (unit.Namespace is { } ns)
        {
            Line($"(namespace {Name(ns.Name)})");
        }
        foreach (var item in unit.Items)
        {
            PrintItem(item);
        }
        _indent--;
        Line(")");
    }

    private void PrintItem(Item item)
    {
        var visibility = item.Visibility.ToString().ToLowerInvariant();
        switch (item)
        {
            case FunctionDecl fn:
            {
                var parameters = string.Join(" ", fn.Parameters.Select(p => $"({Name(p.Name)} {Type(p.Type)})"));
                var returns = fn.ReturnType is null ? "" : $" (returns {Type(fn.ReturnType)})";
                Line($"(fn {visibility} {Name(fn.Name)} (params{(parameters.Length > 0 ? " " + parameters : "")}){returns}");
                _indent++;
                if (fn.Body is not null)
                {
                    PrintStmt(fn.Body);
                }
                else if (fn.ExpressionBody is not null)
                {
                    Line($"(=> {Expr(fn.ExpressionBody)})");
                }
                _indent--;
                Line(")");
                break;
            }
            case ConstDecl c:
                Line($"(const {visibility} {Name(c.Name)} {Type(c.Type)} {Expr(c.Value)})");
                break;
            default:
                throw new InvalidOperationException($"unknown item {item.GetType().Name}");
        }
    }

    private void PrintStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case BlockStmt block:
                if (block.Statements.IsEmpty)
                {
                    Line("(block)");
                    break;
                }
                Line("(block");
                _indent++;
                foreach (var s in block.Statements)
                {
                    PrintStmt(s);
                }
                _indent--;
                Line(")");
                break;
            case LocalDeclStmt local:
            {
                var sb = new StringBuilder(local.IsMutable ? "(var " : "(let ").Append(Name(local.Name));
                if (local.Type is not null)
                {
                    sb.Append(" : ").Append(Type(local.Type));
                }
                if (local.Initializer is not null)
                {
                    sb.Append(" = ").Append(Expr(local.Initializer));
                }
                Line(sb.Append(')').ToString());
                break;
            }
            case IfStmt ifStmt:
                Line($"(if {Expr(ifStmt.Condition)}");
                _indent++;
                PrintStmt(ifStmt.Then);
                if (ifStmt.Else is not null)
                {
                    Line("(else");
                    _indent++;
                    PrintStmt(ifStmt.Else);
                    _indent--;
                    Line(")");
                }
                _indent--;
                Line(")");
                break;
            case WhileStmt whileStmt:
                Line($"(while {Expr(whileStmt.Condition)}");
                _indent++;
                PrintStmt(whileStmt.Body);
                _indent--;
                Line(")");
                break;
            case LoopStmt loop:
                Line("(loop");
                _indent++;
                PrintStmt(loop.Body);
                _indent--;
                Line(")");
                break;
            case BreakStmt:
                Line("(break)");
                break;
            case ContinueStmt:
                Line("(continue)");
                break;
            case ReturnStmt ret:
                Line(ret.Value is null ? "(return)" : $"(return {Expr(ret.Value)})");
                break;
            case AssignStmt assign:
                Line($"({AssignOpText(assign.Op)} {Expr(assign.Target)} {Expr(assign.Value)})");
                break;
            case ExprStmt exprStmt:
                Line($"(expr {Expr(exprStmt.Expression)})");
                break;
            default:
                throw new InvalidOperationException($"unknown statement {stmt.GetType().Name}");
        }
    }

    private string Expr(Expr expr) => expr switch
    {
        IntLiteralExpr i => i.Value.ToString(CultureInfo.InvariantCulture) + SuffixText(i.Suffix),
        FloatLiteralExpr f => f.Digits + f.Suffix switch { FloatSuffix.F32 => "f32", FloatSuffix.F64 => "f64", _ => "" },
        StringLiteralExpr s => Quote(s.Value, '"'),
        CharLiteralExpr c => Quote(char.ConvertFromUtf32(c.Value), '\''),
        BoolLiteralExpr b => b.Value ? "true" : "false",
        NameExpr n when n.TypeArguments.IsEmpty => _symbols.GetName(n.Name.Name),
        NameExpr n => $"(generic {_symbols.GetName(n.Name.Name)} {string.Join(" ", n.TypeArguments.Select(Type))})",
        ParenExpr p => $"(paren {Expr(p.Inner)})",
        UnaryExpr u => $"({(u.Op == UnaryOp.Negate ? "neg" : "not")} {Expr(u.Operand)})",
        BinaryExpr b => $"({BinaryOpText(b.Op)} {Expr(b.Left)} {Expr(b.Right)})",
        CastExpr c => $"(as {Expr(c.Operand)} {Type(c.Type)})",
        CallExpr c => c.Arguments.IsEmpty
            ? $"(call {Expr(c.Callee)})"
            : $"(call {Expr(c.Callee)} {string.Join(" ", c.Arguments.Select(Expr))})",
        MemberExpr m => $"(. {Expr(m.Target)} {_symbols.GetName(m.Member.Name)})",
        ErrorExpr => "<error>",
        _ => throw new InvalidOperationException($"unknown expression {expr.GetType().Name}"),
    };

    private string Type(TypeSyntax type) => type switch
    {
        NamedType n => Name(n.Name),
        MissingType => "<missing-type>",
        _ => throw new InvalidOperationException($"unknown type syntax {type.GetType().Name}"),
    };

    private string Name(Identifier id) => _symbols.GetName(id.Name);

    private string Name(QualifiedName name) => string.Join(".", name.Parts.Select(Name));

    private static string SuffixText(IntSuffix suffix) =>
        suffix == IntSuffix.None ? "" : suffix.ToString().ToLowerInvariant();

    private static string BinaryOpText(BinaryOp op) => op switch
    {
        BinaryOp.LogicalOr => "||",
        BinaryOp.LogicalAnd => "&&",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.BitOr => "|",
        BinaryOp.BitXor => "^",
        BinaryOp.BitAnd => "&",
        BinaryOp.ShiftLeft => "<<",
        BinaryOp.ShiftRight => ">>",
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Remainder => "%",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private static string AssignOpText(AssignOp op) => op switch
    {
        AssignOp.Assign => "=",
        AssignOp.Add => "+=",
        AssignOp.Subtract => "-=",
        AssignOp.Multiply => "*=",
        AssignOp.Divide => "/=",
        AssignOp.Remainder => "%=",
        AssignOp.BitAnd => "&=",
        AssignOp.BitOr => "|=",
        AssignOp.BitXor => "^=",
        AssignOp.ShiftLeft => "<<=",
        AssignOp.ShiftRight => ">>=",
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    /// <summary>Quotes text using Nova escape syntax; non-printable and non-ASCII scalars become \u{...}.</summary>
    private static string Quote(string value, char quote)
    {
        var sb = new StringBuilder().Append(quote);
        for (var i = 0; i < value.Length; i += char.IsSurrogatePair(value, i) ? 2 : 1)
        {
            var scalar = char.ConvertToUtf32(value, i);
            switch (scalar)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\\': sb.Append("\\\\"); break;
                case 0: sb.Append("\\0"); break;
                case var q when q == quote: sb.Append('\\').Append(quote); break;
                case >= 0x20 and < 0x7F: sb.Append((char)scalar); break;
                default: sb.Append("\\u{").Append(scalar.ToString("X", CultureInfo.InvariantCulture)).Append('}'); break;
            }
        }
        return sb.Append(quote).ToString();
    }
}
