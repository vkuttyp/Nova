using System.Numerics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Semantics;

/// <summary>
/// Properties of the compilation target that affect semantics. This is the only source of
/// target layout facts: never use host .NET properties (docs/decisions/0001).
/// </summary>
public sealed record TargetInfo(string Name, int PointerBits)
{
    /// <summary>LP64: x86_64 and aarch64 CosmoOS, and 64-bit Linux/macOS hosts.</summary>
    public static readonly TargetInfo Lp64 = new("lp64", 64);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names the Nova type kinds.")]
public enum TypeKind
{
    Integer,
    Bool,
    Void,
    /// <summary>The type of expressions that never complete (Process.exit). Not nameable.</summary>
    Never,
    /// <summary>A string literal argument of a Console built-in. Not nameable, not storable.</summary>
    StringLiteral,
    /// <summary>The type of an expression that already produced a diagnostic; suppresses cascades.</summary>
    Error,
}

/// <summary>A Nova type. Types are compared by reference: each exists once per <see cref="TypeTable"/>.</summary>
public sealed class NovaType
{
    internal NovaType(TypeKind kind, string name, int bits = 0, bool isSigned = false)
    {
        Kind = kind;
        Name = name;
        Bits = bits;
        IsSigned = isSigned;
        if (kind == TypeKind.Integer)
        {
            Min = isSigned ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
            Max = isSigned ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
        }
    }

    public TypeKind Kind { get; }

    public string Name { get; }

    /// <summary>Bit width of an integer type; 0 otherwise.</summary>
    public int Bits { get; }

    public bool IsSigned { get; }

    public BigInteger Min { get; }

    public BigInteger Max { get; }

    public bool IsInteger => Kind == TypeKind.Integer;

    public bool IsUnsignedInteger => Kind == TypeKind.Integer && !IsSigned;

    /// <summary>Error or never: such operands are accepted silently (their problem is already reported or moot).</summary>
    public bool IsBottom => Kind is TypeKind.Error or TypeKind.Never;

    public bool Fits(BigInteger value) => value >= Min && value <= Max;

    public override string ToString() => Name;
}

/// <summary>The built-in types for one target.</summary>
public sealed class TypeTable
{
    private readonly Dictionary<string, NovaType> _byName;

    public TypeTable(TargetInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        I8 = new NovaType(TypeKind.Integer, "i8", 8, true);
        I16 = new NovaType(TypeKind.Integer, "i16", 16, true);
        I32 = new NovaType(TypeKind.Integer, "i32", 32, true);
        I64 = new NovaType(TypeKind.Integer, "i64", 64, true);
        ISize = new NovaType(TypeKind.Integer, "isize", target.PointerBits, true);
        U8 = new NovaType(TypeKind.Integer, "u8", 8);
        U16 = new NovaType(TypeKind.Integer, "u16", 16);
        U32 = new NovaType(TypeKind.Integer, "u32", 32);
        U64 = new NovaType(TypeKind.Integer, "u64", 64);
        USize = new NovaType(TypeKind.Integer, "usize", target.PointerBits);
        Bool = new NovaType(TypeKind.Bool, "bool");
        Void = new NovaType(TypeKind.Void, "void");
        Never = new NovaType(TypeKind.Never, "never");
        StringLiteral = new NovaType(TypeKind.StringLiteral, "string literal");
        Error = new NovaType(TypeKind.Error, "<error>");

        _byName = new Dictionary<string, NovaType>(StringComparer.Ordinal);
        foreach (var t in new[] { I8, I16, I32, I64, ISize, U8, U16, U32, U64, USize, Bool, Void })
        {
            _byName.Add(t.Name, t);
        }
    }

    public TargetInfo Target { get; }

    public NovaType I8 { get; }
    public NovaType I16 { get; }
    public NovaType I32 { get; }
    public NovaType I64 { get; }
    public NovaType ISize { get; }
    public NovaType U8 { get; }
    public NovaType U16 { get; }
    public NovaType U32 { get; }
    public NovaType U64 { get; }
    public NovaType USize { get; }
    public NovaType Bool { get; }
    public NovaType Void { get; }
    public NovaType Never { get; }
    public NovaType StringLiteral { get; }
    public NovaType Error { get; }

    /// <summary>Looks up a nameable built-in type (type-system.md §1).</summary>
    public NovaType? Lookup(string name) => _byName.GetValueOrDefault(name);

    public NovaType? ForSuffix(IntSuffix suffix) => suffix switch
    {
        IntSuffix.I8 => I8,
        IntSuffix.I16 => I16,
        IntSuffix.I32 => I32,
        IntSuffix.I64 => I64,
        IntSuffix.ISize => ISize,
        IntSuffix.U8 => U8,
        IntSuffix.U16 => U16,
        IntSuffix.U32 => U32,
        IntSuffix.U64 => U64,
        IntSuffix.USize => USize,
        _ => null,
    };

    /// <summary>Is every value of <paramref name="from"/> representable in <paramref name="to"/>? (type-system.md §7)</summary>
    public static bool IsLosslessCast(NovaType from, NovaType to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (ReferenceEquals(from, to))
        {
            return true;
        }
        if (from.Kind == TypeKind.Bool)
        {
            return to.IsInteger;
        }
        return from.IsInteger && to.IsInteger && to.Min <= from.Min && to.Max >= from.Max;
    }
}
