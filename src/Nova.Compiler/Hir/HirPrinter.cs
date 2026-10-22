using System.Globalization;
using System.Text;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Hir;

/// <summary>
/// Prints HIR as indented S-expressions with types (`novac check --dump-hir`). The format is
/// stable: tests/corpus/check-pass/*.hir snapshots depend on it.
/// Locals print as <c>name#id</c>; typed expressions as <c>expr:type</c>.
/// </summary>
public sealed class HirPrinter
{
    private readonly SymbolTable _symbols;
    private readonly StringBuilder _sb = new();
    private int _indent;

    private HirPrinter(SymbolTable symbols) => _symbols = symbols;

    public static string Print(HirProgram program, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        var printer = new HirPrinter(symbols);
        printer.PrintProgram(program);
        return printer._sb.ToString();
    }

    private void Line(string text) => _sb.Append(' ', _indent * 2).Append(text).Append('\n');

    private void PrintProgram(HirProgram program)
    {
        Line("(program");
        _indent++;
        foreach (var constant in program.Constants)
        {
            Line($"(const {_symbols.GetName(constant.Name)}: {constant.Type} = {(constant.Value is null ? "?" : Expr(constant.Value))})");
        }
        foreach (var function in program.Functions)
        {
            var parameters = string.Join(" ", function.Parameters.Select(p => $"({Local(p)}: {p.Type})"));
            var owner = function.Owner is null ? "" : $"{function.Owner}.";
            Line($"(fn {owner}{_symbols.GetName(function.Name)} ({parameters}) -> {function.ReturnType}");
            _indent++;
            if (function.Body is not null)
            {
                Stmt(function.Body);
            }
            _indent--;
            Line(")");
        }
        _indent--;
        Line(")");
    }

    private string Local(HirLocal local) => $"{_symbols.GetName(local.Name)}#{local.Id}";

    private string Place(HirPlace place) =>
        Local(place.Root) + string.Concat(place.Path.Select(f => "." + _symbols.GetName(f.Name)));

    private void Nested(string head, params HirStmt?[] children)
    {
        Line($"({head}");
        _indent++;
        foreach (var child in children)
        {
            if (child is not null)
            {
                Stmt(child);
            }
        }
        _indent--;
        Line(")");
    }

    private void Stmt(HirStmt stmt)
    {
        switch (stmt)
        {
            case HirBlock block when block.Statements.IsEmpty:
                Line("(block)");
                break;
            case HirBlock block:
                Nested("block", [.. block.Statements]);
                break;
            case HirLocalDecl decl:
                Line($"({(decl.Local.IsMutable ? "var" : "let")} {Local(decl.Local)}: {decl.Local.Type}" +
                     (decl.Initializer is null ? ")" : $" = {Expr(decl.Initializer)})"));
                break;
            case HirAssign assign:
                Line($"({(assign.Op is null ? "=" : OpText(assign.Op.Value) + "=")} {Place(assign.Target)} {Expr(assign.Value)})");
                break;
            case HirIf ifStmt when ifStmt.Else is null:
                Nested($"if {Expr(ifStmt.Condition)}", ifStmt.Then);
                break;
            case HirIf ifStmt:
                Line($"(if {Expr(ifStmt.Condition)}");
                _indent++;
                Stmt(ifStmt.Then);
                Nested("else", ifStmt.Else);
                _indent--;
                Line(")");
                break;
            case HirWhile whileStmt:
                Nested($"while {Expr(whileStmt.Condition)}", whileStmt.Body);
                break;
            case HirLoop loop:
                Nested("loop", loop.Body);
                break;
            case HirBreak:
                Line("(break)");
                break;
            case HirContinue:
                Line("(continue)");
                break;
            case HirReturn ret:
                Line(ret.Value is null ? "(return)" : $"(return {Expr(ret.Value)})");
                break;
            case HirExprStmt exprStmt:
                Line($"(expr {Expr(exprStmt.Expression)})");
                break;
            default:
                throw new InvalidOperationException($"unknown HIR statement {stmt.GetType().Name}");
        }
    }

    private string Expr(HirExpr expr) => expr switch
    {
        HirIntConst i => $"{i.Value.ToString(CultureInfo.InvariantCulture)}:{i.Type}",
        HirBoolConst b => b.Value ? "true" : "false",
        HirStringConst s => "\"" + s.Value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"",
        HirLocalRef l => Local(l.Local),
        HirUnary u => $"({(u.Op == UnaryOp.Negate ? "neg" : "not")} {Expr(u.Operand)}):{u.Type}",
        HirBinary b => $"({OpText(b.Op)} {Expr(b.Left)} {Expr(b.Right)}):{b.Type}",
        HirCast c => $"(as {Expr(c.Operand)} {c.Type})",
        HirTruncate t => $"(truncate {t.Type} {Expr(t.Operand)})",
        HirCall c => $"(call {(c.Callee.Owner is { } owner ? owner + "." : "")}{_symbols.GetName(c.Callee.Name)}{Args(c.Arguments)}):{c.Type}",
        HirBuiltinCall c => $"({BuiltinName(c.Function)}{Args(c.Arguments)})",
        HirFieldAccess f => $"(. {Expr(f.Target)} {_symbols.GetName(f.Field.Name)}):{f.Type}",
        HirStructLiteral s => s.Fields.IsEmpty
            ? $"(new {s.Type})"
            : $"(new {s.Type} {string.Join(" ", s.Fields.Select(f => $"({_symbols.GetName(f.Field.Name)} {Expr(f.Value)})"))})",
        HirError => "<error>",
        _ => throw new InvalidOperationException($"unknown HIR expression {expr.GetType().Name}"),
    };

    private string Args(IEnumerable<HirExpr> arguments) => string.Concat(arguments.Select(a => " " + Expr(a)));

    private static string BuiltinName(BuiltinFunction function) => function switch
    {
        BuiltinFunction.ConsoleWrite => "Console.write",
        BuiltinFunction.ConsoleWriteLine => "Console.writeLine",
        _ => "Process.exit",
    };

    private static string OpText(BinaryOp op) => op switch
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
        _ => "%",
    };
}
