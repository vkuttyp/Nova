using System.Collections.Immutable;
using Nova.Compiler.Hir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Nir;

// Nova IR: a control-flow graph per function (docs/intermediate-representation.md §3).

public sealed class NirProgram
{
    internal NirProgram(ImmutableArray<NirFunction> functions) => Functions = functions;

    public ImmutableArray<NirFunction> Functions { get; }
}

public sealed class NirLocal
{
    internal NirLocal(int id, NovaType type, Symbol? name)
    {
        Id = id;
        Type = type;
        Name = name;
    }

    public int Id { get; }

    public NovaType Type { get; }

    /// <summary>The source name, or null for a compiler temporary.</summary>
    public Symbol? Name { get; }
}

public sealed class NirFunction
{
    internal NirFunction(HirFunction source, ImmutableArray<NirLocal> locals, int parameterCount, ImmutableArray<NirBlock> blocks)
    {
        Source = source;
        Locals = locals;
        ParameterCount = parameterCount;
        Blocks = blocks;
    }

    public HirFunction Source { get; }

    public Symbol Name => Source.Name;

    public NovaType ReturnType => Source.ReturnType;

    /// <summary>All locals; the first <see cref="ParameterCount"/> are the parameters in order.</summary>
    public ImmutableArray<NirLocal> Locals { get; }

    public int ParameterCount { get; }

    /// <summary>Reachable blocks only; <c>Blocks[0]</c> is the entry.</summary>
    public ImmutableArray<NirBlock> Blocks { get; }
}

public sealed class NirBlock
{
    internal NirBlock(int id) => Id = id;

    public int Id { get; }

    public List<NirStatement> Statements { get; } = [];

    public NirTerminator? Terminator { get; internal set; }
}

// ---- Operands ----

public abstract record NirOperand(NovaType Type);

public sealed record NirLocalOperand(NirLocal Local) : NirOperand(Local.Type);

/// <summary>An integer or bool constant (bools use 0/1).</summary>
public sealed record NirConst(Int128 Value, NovaType Type) : NirOperand(Type);

/// <summary>A string literal; only as an argument of a Console built-in.</summary>
public sealed record NirString(string Value, NovaType Type) : NirOperand(Type);

// ---- Rvalues ----

public abstract record NirRvalue(NovaType Type);

public sealed record NirUse(NirOperand Operand) : NirRvalue(Operand.Type);

/// <summary>A binary operator with the semantics of type-system.md §6; may panic at <see cref="Span"/>.</summary>
public sealed record NirBinary(BinaryOp Op, NirOperand Left, NirOperand Right, NovaType Type, SourceText Source, TextSpan Span) : NirRvalue(Type);

public sealed record NirUnary(UnaryOp Op, NirOperand Operand, NovaType Type, SourceText Source, TextSpan Span) : NirRvalue(Type);

public sealed record NirCast(NirOperand Operand, NovaType Type) : NirRvalue(Type);

public sealed record NirTruncate(NirOperand Operand, NovaType Type) : NirRvalue(Type);

public sealed record NirCall(HirFunction Callee, ImmutableArray<NirOperand> Arguments, NovaType Type) : NirRvalue(Type);

public sealed record NirBuiltin(BuiltinFunction Function, ImmutableArray<NirOperand> Arguments, NovaType Type) : NirRvalue(Type);

// ---- Statements and terminators ----

public abstract record NirStatement;

public sealed record NirAssign(NirLocal Target, NirRvalue Value) : NirStatement;

/// <summary>Evaluates an rvalue for its effect (a call whose result is unused).</summary>
public sealed record NirEval(NirRvalue Value) : NirStatement;

public abstract record NirTerminator;

public sealed record NirGoto(NirBlock Target) : NirTerminator;

public sealed record NirBranch(NirOperand Condition, NirBlock Then, NirBlock Else) : NirTerminator;

public sealed record NirReturn(NirOperand? Value) : NirTerminator;

public sealed record NirExit(NirOperand Code) : NirTerminator;

public sealed record NirUnreachable : NirTerminator;
