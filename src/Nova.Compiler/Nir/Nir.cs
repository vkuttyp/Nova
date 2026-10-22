using System.Collections.Immutable;
using Nova.Compiler.Hir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Nir;

// Nova IR: a control-flow graph per function (docs/intermediate-representation.md §3).

public sealed class NirProgram
{
    internal NirProgram(ImmutableArray<NirFunction> functions, ImmutableArray<NovaType> structs)
    {
        Functions = functions;
        Structs = structs;
    }

    public ImmutableArray<NirFunction> Functions { get; }

    /// <summary>Struct types in declaration order.</summary>
    public ImmutableArray<NovaType> Structs { get; }
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

    /// <summary>A `using` binding: cannot be moved out of (type-system.md §14.6).</summary>
    public bool IsUsing { get; init; }
}

public sealed class NirFunction
{
    private readonly List<NirLocal> _locals;

    internal NirFunction(HirFunction source, IEnumerable<NirLocal> locals, int parameterCount, ImmutableArray<NirBlock> blocks)
    {
        Source = source;
        _locals = [.. locals];
        ParameterCount = parameterCount;
        Blocks = blocks;
    }

    public HirFunction Source { get; }

    public Symbol Name => Source.Name;

    public NovaType ReturnType => Source.ReturnType;

    /// <summary>All locals; the first <see cref="ParameterCount"/> are the parameters in order.</summary>
    public IReadOnlyList<NirLocal> Locals => _locals;

    /// <summary>Adds a compiler local after lowering (drop flags).</summary>
    internal NirLocal AddLocal(NovaType type)
    {
        var local = new NirLocal(_locals.Count, type, null);
        _locals.Add(local);
        return local;
    }

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

/// <summary>A local followed by projections: <c>_3</c>, <c>_3.a.b</c>, <c>(*_2).f</c>.</summary>
public sealed record NirPlace(NirLocal Local, ImmutableArray<Projection> Projections)
{
    public NirPlace(NirLocal local)
        : this(local, [])
    {
    }

    public NovaType Type
    {
        get
        {
            var type = Local.Type;
            foreach (var projection in Projections)
            {
                type = projection.Apply(type);
            }
            return type;
        }
    }

    public bool IsLocal => Projections.IsEmpty;

    public NirPlace Field(StructField field) => new(Local, Projections.Add(new Projection(field)));

    public NirPlace Deref() => new(Local, Projections.Add(Projection.Deref));

    public NirPlace Project(Projection projection) => new(Local, Projections.Add(projection));

    /// <summary>Only field projections: a move path (no deref, downcast or element).</summary>
    public bool IsFieldPath => Projections.All(p => p.IsField);

    public bool Equals(NirPlace? other) =>
        other is not null && other.Local == Local && Projections.SequenceEqual(other.Projections);

    public override int GetHashCode() => HashCode.Combine(Local, Projections.Length);
}

/// <summary>Reads the value in a place: a copy, or a move (the place becomes uninitialised, type-system.md §14.3).</summary>
public sealed record NirPlaceOperand(NirPlace Place, bool IsMove = false) : NirOperand(Place.Type)
{
    public NirPlaceOperand(NirLocal local)
        : this(new NirPlace(local))
    {
    }
}

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

/// <summary><c>&amp;place</c>: creates a loan of <see cref="Place"/> (lifetimes.md §2).</summary>
public sealed record NirRef(bool IsMutable, NirPlace Place, NovaType Type) : NirRvalue(Type);

/// <summary>The tag (variant index) of the enum value in <see cref="Place"/>, as a u32.</summary>
public sealed record NirDiscriminant(NirPlace Place, NovaType Type) : NirRvalue(Type);

/// <summary>An enum value of one variant with its payload operands.</summary>
public sealed record NirEnumAggregate(EnumVariant Variant, ImmutableArray<NirOperand> Payload, NovaType Type) : NirRvalue(Type);

/// <summary>A struct value built from its field values, given in declaration order.</summary>
public sealed record NirAggregate(ImmutableArray<NirOperand> Fields, NovaType Type) : NirRvalue(Type);

public sealed record NirCall(HirFunction Callee, ImmutableArray<NirOperand> Arguments, NovaType Type) : NirRvalue(Type);

public sealed record NirBuiltin(BuiltinFunction Function, ImmutableArray<NirOperand> Arguments, NovaType Type) : NirRvalue(Type);

// ---- Statements and terminators ----

/// <summary>A statement. <see cref="Span"/> is the source construct it came from (for diagnostics).</summary>
public abstract record NirStatement
{
    public TextSpan Span { get; init; }
}

/// <summary>The local's storage ends: scope exit, or the end of the statement for a temporary.</summary>
public sealed record NirStorageDead(NirLocal Local) : NirStatement;

/// <summary>
/// Destroys the value in <see cref="Place"/> (its drop glue). Inserted by drop elaboration; when
/// <see cref="Flag"/> is set, only if that bool local is true at runtime (type-system.md §14.5).
/// </summary>
public sealed record NirDrop(NirPlace Place, NirLocal? Flag) : NirStatement;

public sealed record NirAssign(NirPlace Target, NirRvalue Value) : NirStatement;

/// <summary>Evaluates an rvalue for its effect (a call whose result is unused).</summary>
public sealed record NirEval(NirRvalue Value) : NirStatement;

public abstract record NirTerminator
{
    public TextSpan Span { get; init; }

    public virtual IEnumerable<NirBlock> Successors => [];
}

public sealed record NirGoto(NirBlock Target) : NirTerminator
{
    public override IEnumerable<NirBlock> Successors => [Target];
}

public sealed record NirBranch(NirOperand Condition, NirBlock Then, NirBlock Else) : NirTerminator
{
    public override IEnumerable<NirBlock> Successors => [Then, Else];
}

/// <summary>Jumps to the case whose value equals <see cref="Value"/>, else to <see cref="Otherwise"/> (match, M2d).</summary>
public sealed record NirSwitch(NirOperand Value, ImmutableArray<(Int128 Value, NirBlock Target)> Cases, NirBlock Otherwise) : NirTerminator
{
    public override IEnumerable<NirBlock> Successors => [.. Cases.Select(c => c.Target), Otherwise];
}

public sealed record NirReturn(NirOperand? Value) : NirTerminator;

public sealed record NirExit(NirOperand Code) : NirTerminator;

public sealed record NirUnreachable : NirTerminator;
