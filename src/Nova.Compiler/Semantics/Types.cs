using System.Numerics;
using Nova.Compiler.Hir;
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
    /// <summary>A user-defined struct (type-system.md §12); see <see cref="NovaType.Struct"/>.</summary>
    Struct,
    /// <summary><c>borrow T</c> / <c>borrow mut T</c> (type-system.md §13); see <see cref="NovaType.Pointee"/>.</summary>
    Reference,
}

public sealed class StructField
{
    internal StructField(Symbol name, NovaType type, int index, TextSpan span)
    {
        Name = name;
        Type = type;
        Index = index;
        Span = span;
    }

    public Symbol Name { get; }

    public NovaType Type { get; }

    /// <summary>Declaration order; also the C layout order.</summary>
    public int Index { get; }

    public TextSpan Span { get; }

    /// <summary>Byte offset in the target layout (set by <see cref="TypeTable.LayoutOf"/>).</summary>
    public int Offset { get; internal set; }
}

/// <summary>One step of a place: a struct field, or <c>Deref</c> (through a reference) when <see cref="Field"/> is null.</summary>
public readonly record struct Projection(StructField? Field)
{
    public static Projection Deref => default;

    public bool IsDeref => Field is null;

    public override string ToString() => IsDeref ? "*" : $"field {Field!.Index}";
}

/// <summary>The members of a struct type. Filled in by the type checker after all items are declared.</summary>
public sealed class StructDefinition
{
    private readonly List<StructField> _fields = [];

    internal StructDefinition(StructDecl syntax, SourceText source)
    {
        Syntax = syntax;
        Source = source;
    }

    public StructDecl Syntax { get; }

    public SourceText Source { get; }

    public IReadOnlyList<StructField> Fields => _fields;

    /// <summary>Methods and static functions, by name.</summary>
    public Dictionary<Symbol, HirFunction> Functions { get; } = [];

    /// <summary>Set if the struct contains itself by value (N0227); such a struct has no layout.</summary>
    public bool IsRecursive { get; internal set; }

    /// <summary>The body of the struct's <c>drop</c> block as a <c>borrow mut this</c> method, if any.</summary>
    public HirFunction? DropFunction { get; internal set; }

    /// <summary>Copy iff no drop block and all fields Copy (type-system.md §14.1). Set after members are resolved.</summary>
    public bool IsCopy { get; internal set; } = true;

    /// <summary>Has a drop block or a field that needs drop (type-system.md §14.1).</summary>
    public bool NeedsDrop { get; internal set; }

    internal (int Size, int Align)? Layout { get; set; }

    public StructField? FindField(Symbol name) => _fields.Find(f => f.Name == name);

    internal StructField AddField(Symbol name, NovaType type, TextSpan span)
    {
        var field = new StructField(name, type, _fields.Count, span);
        _fields.Add(field);
        return field;
    }
}

/// <summary>A Nova type. Types are compared by reference: each exists once per <see cref="TypeTable"/>.</summary>
public sealed class NovaType
{
    internal NovaType(string name, StructDefinition definition)
        : this(TypeKind.Struct, name) => Struct = definition;

    internal NovaType(NovaType pointee, bool isMutable)
        : this(TypeKind.Reference, (isMutable ? "borrow mut " : "borrow ") + pointee.Name)
    {
        Pointee = pointee;
        IsMutableReference = isMutable;
    }

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

    /// <summary>The definition of a struct type; null for every other kind.</summary>
    public StructDefinition? Struct { get; }

    /// <summary>The referent type of a reference; null for every other kind.</summary>
    public NovaType? Pointee { get; }

    /// <summary>True for <c>borrow mut T</c>.</summary>
    public bool IsMutableReference { get; }

    public bool IsReference => Kind == TypeKind.Reference;

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

    public static NovaType CreateStruct(string name, StructDecl syntax, SourceText source) =>
        new(name, new StructDefinition(syntax, source));

    private readonly Dictionary<(NovaType, bool), NovaType> _references = [];

    /// <summary>The unique reference type to <paramref name="pointee"/> (types are compared by reference).</summary>
    public NovaType ReferenceTo(NovaType pointee, bool isMutable)
    {
        ArgumentNullException.ThrowIfNull(pointee);
        if (!_references.TryGetValue((pointee, isMutable), out var reference))
        {
            reference = new NovaType(pointee, isMutable);
            _references.Add((pointee, isMutable), reference);
        }
        return reference;
    }

    /// <summary>
    /// Size and alignment for the target, by C's rules (type-system.md §12). Struct field offsets are
    /// stored on the fields. An empty struct is given one byte, because the C backend emits a placeholder
    /// member: C has no empty structs.
    /// </summary>
    public static (int Size, int Align) LayoutOf(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        switch (type.Kind)
        {
            case TypeKind.Integer:
                return (type.Bits / 8, type.Bits / 8);
            case TypeKind.Bool:
                return (1, 1);
            case TypeKind.Reference:
                return (8, 8); // LP64 targets only in M2 (TargetInfo.PointerBits is asserted in the generated C)
            case TypeKind.Struct:
            {
                var definition = type.Struct!;
                if (definition.Layout is { } cached)
                {
                    return cached;
                }
                if (definition.IsRecursive)
                {
                    throw new InvalidOperationException($"recursive struct '{type}' has no layout");
                }
                int offset = 0, align = 1;
                foreach (var field in definition.Fields)
                {
                    var (fieldSize, fieldAlign) = LayoutOf(field.Type);
                    offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign;
                    field.Offset = offset;
                    offset += fieldSize;
                    align = Math.Max(align, fieldAlign);
                }
                var size = definition.Fields.Count == 0 ? 1 : (offset + align - 1) / align * align;
                definition.Layout = (size, align);
                return (size, align);
            }
            default:
                throw new InvalidOperationException($"type '{type}' has no layout");
        }
    }

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

    /// <summary>Copy types are duplicated by use; every other type is moved (type-system.md §14.1).</summary>
    public static bool IsCopy(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Kind switch
        {
            TypeKind.Struct => type.Struct!.IsCopy,
            TypeKind.Reference => !type.IsMutableReference,
            _ => true,
        };
    }

    public static bool NeedsDrop(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Struct is { NeedsDrop: true };
    }

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
