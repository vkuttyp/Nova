using System.Collections.Immutable;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Hir;

// Typed high-level IR (docs/architecture.md §2.2). Every expression carries its resolved type and
// every name its resolved definition. Sugar is removed: parentheses, else-if chains (nested ifs),
// expression-bodied functions (a block with a return), and constant references (replaced by values).

public sealed class HirProgram
{
    internal HirProgram(ImmutableArray<HirFunction> functions, ImmutableArray<HirConst> constants, ImmutableArray<NovaType> structs)
    {
        Functions = functions;
        Constants = constants;
        Structs = structs;
    }

    /// <summary>Struct types in declaration order.</summary>
    public ImmutableArray<NovaType> Structs { get; }

    /// <summary>Functions in declaration order (file order, then position).</summary>
    public ImmutableArray<HirFunction> Functions { get; }

    public ImmutableArray<HirConst> Constants { get; }
}

public sealed class HirConst
{
    internal HirConst(Symbol name, NovaType type, SourceText source, TextSpan span)
    {
        Name = name;
        Type = type;
        Source = source;
        Span = span;
    }

    public Symbol Name { get; }

    public NovaType Type { get; }

    public SourceText Source { get; }

    public TextSpan Span { get; }

    /// <summary>The evaluated value: <see cref="HirIntConst"/>, <see cref="HirBoolConst"/> or <see cref="HirError"/>.</summary>
    public HirExpr? Value { get; internal set; }
}

public sealed class HirLocal
{
    internal HirLocal(int id, Symbol name, NovaType type, bool isMutable, bool isParameter, TextSpan span)
    {
        Id = id;
        Name = name;
        Type = type;
        IsMutable = isMutable;
        IsParameter = isParameter;
        Span = span;
    }

    /// <summary>Index within the function: parameters first, then locals in declaration order.</summary>
    public int Id { get; }

    public Symbol Name { get; }

    public NovaType Type { get; }

    public bool IsMutable { get; }

    public bool IsParameter { get; }

    public TextSpan Span { get; }
}

public sealed class HirFunction
{
    private readonly List<HirLocal> _locals = [];

    internal HirFunction(int id, Symbol name, NovaType returnType, SourceText source, FunctionDecl syntax, NovaType? owner = null)
    {
        Id = id;
        Name = name;
        ReturnType = returnType;
        Source = source;
        Syntax = syntax;
        Owner = owner;
    }

    /// <summary>The struct this function is declared in, or null for a top-level function.</summary>
    public NovaType? Owner { get; }

    /// <summary>True for a method: its first parameter is the <c>this</c> receiver (type-system.md §12).</summary>
    public bool IsMethod => Syntax.Receiver is not null && Owner is not null;

    public int Id { get; }

    public Symbol Name { get; }

    public NovaType ReturnType { get; }

    public SourceText Source { get; }

    public FunctionDecl Syntax { get; }

    public ImmutableArray<HirLocal> Parameters { get; internal set; } = [];

    /// <summary>All locals, parameters included; <c>Locals[i].Id == i</c>.</summary>
    public IReadOnlyList<HirLocal> Locals => _locals;

    public HirBlock? Body { get; internal set; }

    internal HirLocal AddLocal(Symbol name, NovaType type, bool isMutable, bool isParameter, TextSpan span)
    {
        var local = new HirLocal(_locals.Count, name, type, isMutable, isParameter, span);
        _locals.Add(local);
        return local;
    }
}

// ---- Statements ----

public abstract record HirStmt(TextSpan Span);

public sealed record HirBlock(ImmutableArray<HirStmt> Statements, TextSpan Span) : HirStmt(Span);

public sealed record HirLocalDecl(HirLocal Local, HirExpr? Initializer, TextSpan Span) : HirStmt(Span);

/// <summary>A local or a field path rooted at a local: <c>p</c>, <c>p.a.b</c>.</summary>
public sealed record HirPlace(HirLocal Root, ImmutableArray<StructField> Path, NovaType Type);

/// <summary>Assignment to a place. <see cref="Op"/> is null for plain '=', else the compound operator.</summary>
public sealed record HirAssign(HirPlace Target, BinaryOp? Op, HirExpr Value, TextSpan Span) : HirStmt(Span);

public sealed record HirIf(HirExpr Condition, HirBlock Then, HirStmt? Else, TextSpan Span) : HirStmt(Span);

public sealed record HirWhile(HirExpr Condition, HirBlock Body, TextSpan Span) : HirStmt(Span);

public sealed record HirLoop(HirBlock Body, TextSpan Span) : HirStmt(Span);

/// <summary>Leaves the innermost enclosing loop.</summary>
public sealed record HirBreak(TextSpan Span) : HirStmt(Span);

/// <summary>Jumps to the next iteration of the innermost enclosing loop.</summary>
public sealed record HirContinue(TextSpan Span) : HirStmt(Span);

public sealed record HirReturn(HirExpr? Value, TextSpan Span) : HirStmt(Span);

public sealed record HirExprStmt(HirExpr Expression, TextSpan Span) : HirStmt(Span);

// ---- Expressions ----

public abstract record HirExpr(NovaType Type, TextSpan Span);

/// <summary>An integer constant; always within the range of <see cref="HirExpr.Type"/>.</summary>
public sealed record HirIntConst(Int128 Value, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

public sealed record HirBoolConst(bool Value, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>A string literal; only appears as an argument of a Console built-in.</summary>
public sealed record HirStringConst(string Value, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

public sealed record HirLocalRef(HirLocal Local, TextSpan Span) : HirExpr(Local.Type, Span);

public sealed record HirUnary(UnaryOp Op, HirExpr Operand, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

public sealed record HirBinary(BinaryOp Op, HirExpr Left, HirExpr Right, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>A lossless conversion (<c>as</c>) to <see cref="HirExpr.Type"/>.</summary>
public sealed record HirCast(HirExpr Operand, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>truncate&lt;T&gt;(e)</c>: value modulo 2^bits(T), reinterpreted in T.</summary>
public sealed record HirTruncate(HirExpr Operand, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

public sealed record HirCall(HirFunction Callee, ImmutableArray<HirExpr> Arguments, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

public enum BuiltinFunction
{
    ConsoleWrite,
    ConsoleWriteLine,
    ProcessExit,
}

public sealed record HirBuiltinCall(BuiltinFunction Function, ImmutableArray<HirExpr> Arguments, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>Reads field <see cref="Field"/> of a struct value.</summary>
public sealed record HirFieldAccess(HirExpr Target, StructField Field, TextSpan Span) : HirExpr(Field.Type, Span);

/// <summary>A struct literal. <see cref="Fields"/> is in source order, which is also evaluation order.</summary>
public sealed record HirStructLiteral(ImmutableArray<(StructField Field, HirExpr Value)> Fields, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>Stands in for an expression that produced a diagnostic.</summary>
public sealed record HirError(NovaType Type, TextSpan Span) : HirExpr(Type, Span);
