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
    /// <summary>A user-defined enum (type-system.md §15); see <see cref="NovaType.Enum"/>.</summary>
    Enum,
    /// <summary><c>borrow T</c> / <c>borrow mut T</c> (type-system.md §13); see <see cref="NovaType.Pointee"/>.</summary>
    Reference,
    /// <summary>
    /// <c>[T; N]</c>, or the unsized <c>[T]</c> that slices refer to (type-system.md §16): <c>Slice&lt;T&gt;</c> is a
    /// shared reference to <c>[T]</c> and <c>SliceMut&lt;T&gt;</c> a mutable one. <c>[T]</c> is never a value type.
    /// </summary>
    Array,
    /// <summary>
    /// <c>Box&lt;T, A&gt;</c> (type-system.md §18.2): owns one <c>T</c> (<see cref="NovaType.Pointee"/>) from allocator
    /// type <see cref="NovaType.Allocator"/>. Dereferenced like a reference, but owned.
    /// </summary>
    Box,
    /// <summary>A built-in region type (type-system.md §19): <see cref="NovaType.Region"/> says which.</summary>
    Region,
}

/// <summary>The built-in region types (ADR 0004).</summary>
public enum RegionKind
{
    /// <summary>Not a region.</summary>
    None,
    /// <summary><c>Arena</c> (type-system.md §19.2): owns its chunks; not scoped.</summary>
    Arena,
    /// <summary><c>FixedBufferAllocator</c> (§19.4): scoped (a mutable loan of its storage); no drop.</summary>
    FixedBuffer,
    /// <summary><c>ScratchFrame</c> (§19.5): scoped (a nested frame borrows its parent); releases at drop.</summary>
    ScratchFrame,
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

    /// <summary>
    /// A part of a scratch frame that only the borrow checker sees (its memory or its allocation
    /// right, type-system.md §19.5). Its place is the frame itself.
    /// </summary>
    public bool IsRegionPart { get; internal init; }
}

public enum ProjectionKind
{
    Field,
    Deref,
    /// <summary>Views an enum value as one variant, whose payloads are then <see cref="Field"/> projections (M2d).</summary>
    Downcast,
    /// <summary>An array or slice element; <see cref="Projection.Index"/> holds the index (HIR expression or NIR operand).</summary>
    Index,
}

/// <summary>One step of a place: a field, a deref through a reference, an enum variant, or an element.</summary>
public readonly record struct Projection(ProjectionKind Kind, StructField? Field = null, EnumVariant? Variant = null, object? Index = null)
{
    public Projection(StructField field)
        : this(ProjectionKind.Field, field)
    {
    }

    public static Projection Deref => new(ProjectionKind.Deref);

    public static Projection Downcast(EnumVariant variant) => new(ProjectionKind.Downcast, Variant: variant);

    public static Projection At(object index) => new(ProjectionKind.Index, Index: index);

    public bool IsDeref => Kind == ProjectionKind.Deref;

    public bool IsField => Kind == ProjectionKind.Field;

    /// <summary>The type after this step, given the type before it.</summary>
    public NovaType Apply(NovaType type) => Kind switch
    {
        ProjectionKind.Field => Field!.Type,
        ProjectionKind.Deref => type.Pointee!,
        ProjectionKind.Downcast => type,
        _ => type.Element!,
    };

    public override string ToString() => Kind switch
    {
        ProjectionKind.Field => $"field {Field!.Index}",
        ProjectionKind.Downcast => $"as {Variant!.Index}",
        _ => Kind.ToString(),
    };
}

/// <summary>An enum variant; its payloads are fields named _0, _1, ... (type-system.md §15).</summary>
public sealed class EnumVariant
{
    internal EnumVariant(Symbol name, int index, TextSpan span)
    {
        Name = name;
        Index = index;
        Span = span;
    }

    public Symbol Name { get; }

    /// <summary>Declaration order; also the tag value.</summary>
    public int Index { get; }

    public TextSpan Span { get; }

    public List<StructField> Payload { get; } = [];
}

public sealed class EnumDefinition
{
    internal EnumDefinition(EnumDecl? syntax, SourceText? source)
    {
        Syntax = syntax;
        Source = source;
    }

    /// <summary>The declaration; null for the built-in <c>Result&lt;T, E&gt;</c>.</summary>
    public EnumDecl? Syntax { get; }

    public SourceText? Source { get; }

    /// <summary>For <c>Result&lt;T, E&gt;</c> (type-system.md §17): T and E. Null for user enums.</summary>
    public (NovaType Ok, NovaType Err)? ResultTypes { get; internal set; }

    public List<EnumVariant> Variants { get; } = [];

    public bool IsCopy { get; internal set; } = true;

    public bool NeedsDrop { get; internal set; }

    public bool IsRecursive { get; internal set; }

    internal (int Size, int Align, int TagSize, int UnionOffset)? Layout { get; set; }

    public EnumVariant? FindVariant(Symbol name) => Variants.Find(v => v.Name == name);
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

    internal NovaType(string name, EnumDefinition definition)
        : this(TypeKind.Enum, name) => Enum = definition;

    internal NovaType(NovaType pointee, bool isMutable)
        : this(TypeKind.Reference, pointee.IsUnsized
            ? (isMutable ? "SliceMut<" : "Slice<") + pointee.Element!.Name + ">"
            : (isMutable ? "borrow mut " : "borrow ") + pointee.Name)
    {
        Pointee = pointee;
        IsMutableReference = isMutable;
    }

    internal NovaType(RegionKind region, string name)
        : this(TypeKind.Region, name) => Region = region;

    internal NovaType(NovaType contents, NovaType allocator, bool defaultAllocator)
        : this(TypeKind.Box, defaultAllocator ? $"Box<{contents.Name}>" : $"Box<{contents.Name}, {allocator.Name}>")
    {
        Pointee = contents;
        Allocator = allocator;
    }

    internal NovaType(NovaType element, long length)
        : this(TypeKind.Array, length < 0 ? $"[{element.Name}]" : $"[{element.Name}; {length}]")
    {
        Element = element;
        ArrayLength = length;
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

    /// <summary>The definition of an enum type; null for every other kind.</summary>
    public EnumDefinition? Enum { get; }

    /// <summary>The element type of an array; null for every other kind (M2d).</summary>
    public NovaType? Element { get; }

    /// <summary>N for <c>[T; N]</c>; -1 for the unsized <c>[T]</c>.</summary>
    public long ArrayLength { get; }

    /// <summary>The unsized <c>[T]</c>: only behind a slice.</summary>
    public bool IsUnsized => Kind == TypeKind.Array && ArrayLength < 0;

    /// <summary><c>Slice&lt;T&gt;</c> or <c>SliceMut&lt;T&gt;</c>.</summary>
    public bool IsSlice => Kind == TypeKind.Reference && Pointee!.IsUnsized;

    /// <summary>The referent type of a reference, or the contents of a box; null for every other kind.</summary>
    public NovaType? Pointee { get; }

    /// <summary>The allocator type of a box; null for every other kind.</summary>
    public NovaType? Allocator { get; }

    public bool IsBox => Kind == TypeKind.Box;

    /// <summary>Which region type this is; <see cref="RegionKind.None"/> for every other kind.</summary>
    public RegionKind Region { get; }

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
        Arena = new NovaType(RegionKind.Arena, "Arena");
        FixedBufferAllocator = new NovaType(RegionKind.FixedBuffer, "FixedBufferAllocator");
        ScratchFrame = new NovaType(RegionKind.ScratchFrame, "ScratchFrame");

        _byName = new Dictionary<string, NovaType>(StringComparer.Ordinal);
        foreach (var t in new[] { I8, I16, I32, I64, ISize, U8, U16, U32, U64, USize, Bool, Void, Arena, FixedBufferAllocator, ScratchFrame })
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
    public NovaType Arena { get; }
    public NovaType FixedBufferAllocator { get; }
    public NovaType ScratchFrame { get; }

    public static NovaType CreateStruct(string name, StructDecl syntax, SourceText source) =>
        new(name, new StructDefinition(syntax, source));

    public static NovaType CreateEnum(string name, EnumDecl syntax, SourceText source) =>
        new(name, new EnumDefinition(syntax, source));

    private readonly Dictionary<(NovaType, bool), NovaType> _references = [];
    private readonly Dictionary<(NovaType, long), NovaType> _arrays = [];
    private readonly List<NovaType> _composites = [];

    /// <summary>Array and slice types in creation order (deterministic: checking order is), for the C backend.</summary>
    public IReadOnlyList<NovaType> Composites => _composites;

    /// <summary>The unique <c>[T; N]</c>, or <c>[T]</c> when <paramref name="length"/> is -1.</summary>
    public NovaType ArrayOf(NovaType element, long length)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_arrays.TryGetValue((element, length), out var array))
        {
            array = new NovaType(element, length);
            _arrays.Add((element, length), array);
            if (length >= 0)
            {
                _composites.Add(array);
            }
        }
        return array;
    }

    /// <summary><c>Slice&lt;T&gt;</c> or <c>SliceMut&lt;T&gt;</c>.</summary>
    public NovaType SliceOf(NovaType element, bool isMutable) => ReferenceTo(ArrayOf(element, -1), isMutable);

    private readonly Dictionary<(NovaType, NovaType), NovaType> _results = [];
    private readonly Dictionary<(NovaType, NovaType), NovaType> _boxes = [];

    /// <summary>The unique <c>Box&lt;contents, allocator&gt;</c>; <paramref name="isDefault"/> when the allocator is <c>SystemHeap</c>.</summary>
    public NovaType BoxOf(NovaType contents, NovaType allocator, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(allocator);
        if (!_boxes.TryGetValue((contents, allocator), out var box))
        {
            box = new NovaType(contents, allocator, isDefault);
            _boxes.Add((contents, allocator), box);
            _composites.Add(box);
        }
        return box;
    }

    /// <summary>
    /// The unique <c>Result&lt;ok, err&gt;</c>: a built-in enum with variants <c>Ok(ok)</c> (no payload for
    /// <c>void</c>) and <c>Err(err)</c> (type-system.md §17.1). The caller supplies the interned names.
    /// </summary>
    public NovaType ResultOf(NovaType ok, NovaType err, Symbol okName, Symbol errName, Symbol payloadName)
    {
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(err);
        if (!_results.TryGetValue((ok, err), out var result))
        {
            var definition = new EnumDefinition(null, null) { ResultTypes = (ok, err) };
            var okVariant = new EnumVariant(okName, 0, default);
            if (ok.Kind != TypeKind.Void)
            {
                okVariant.Payload.Add(new StructField(payloadName, ok, 0, default));
            }
            var errVariant = new EnumVariant(errName, 1, default);
            errVariant.Payload.Add(new StructField(payloadName, err, 0, default));
            definition.Variants.Add(okVariant);
            definition.Variants.Add(errVariant);
            result = new NovaType($"Result<{ok.Name}, {err.Name}>", definition);
            _results.Add((ok, err), result);
            _composites.Add(result);
        }
        return result;
    }

    /// <summary>The unique reference type to <paramref name="pointee"/> (types are compared by reference).</summary>
    public NovaType ReferenceTo(NovaType pointee, bool isMutable)
    {
        ArgumentNullException.ThrowIfNull(pointee);
        if (!_references.TryGetValue((pointee, isMutable), out var reference))
        {
            reference = new NovaType(pointee, isMutable);
            _references.Add((pointee, isMutable), reference);
            if (pointee.IsUnsized)
            {
                _composites.Add(reference);
            }
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
                // LP64 targets only in M2 (TargetInfo.PointerBits is asserted in the generated C).
                return type.IsSlice ? (16, 8) : (8, 8); // a slice is (pointer, u64 length)
            case TypeKind.Box:
                return (8, 8);
            case TypeKind.Region:
                // nv_arena, nv_fba, nv_scratch in nova/rt.h (8-byte fields; asserted there).
                return type.Region switch { RegionKind.Arena => (40, 8), RegionKind.FixedBuffer => (24, 8), _ => (32, 8) };
            case TypeKind.Array:
            {
                var (elementSize, elementAlign) = LayoutOf(type.Element!);
                return ((int)(elementSize * type.ArrayLength), elementAlign); // N0259 bounds the size
            }
            case TypeKind.Enum:
            {
                var (size, align, _, _) = EnumLayout(type);
                return (size, align);
            }
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

    /// <summary>
    /// <c>struct { uintN_t tag; union { struct { payload } v_V; … } u; }</c> by C's rules
    /// (type-system.md §15.1). The union is omitted when no variant has a payload.
    /// </summary>
    public static (int Size, int Align, int TagSize, int UnionOffset) EnumLayout(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var definition = type.Enum!;
        if (definition.Layout is { } cached)
        {
            return cached;
        }
        var tagSize = definition.Variants.Count <= 256 ? 1 : definition.Variants.Count <= 65536 ? 2 : 4;
        int unionSize = 0, unionAlign = 1;
        foreach (var variant in definition.Variants.Where(v => v.Payload.Count > 0))
        {
            int offset = 0, align = 1;
            foreach (var field in variant.Payload)
            {
                var (fieldSize, fieldAlign) = LayoutOf(field.Type);
                offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign;
                field.Offset = offset;
                offset += fieldSize;
                align = Math.Max(align, fieldAlign);
            }
            unionSize = Math.Max(unionSize, (offset + align - 1) / align * align);
            unionAlign = Math.Max(unionAlign, align);
        }
        var totalAlign = Math.Max(tagSize, unionSize == 0 ? 1 : unionAlign);
        var unionOffset = (tagSize + unionAlign - 1) / unionAlign * unionAlign;
        var end = unionSize == 0 ? tagSize : unionOffset + unionSize;
        var layout = ((end + totalAlign - 1) / totalAlign * totalAlign, totalAlign, tagSize, unionSize == 0 ? -1 : unionOffset);
        definition.Layout = layout;
        return layout;
    }

    /// <summary>Copy types are duplicated by use; every other type is moved (type-system.md §14.1).</summary>
    public static bool IsCopy(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Kind switch
        {
            TypeKind.Struct => type.Struct!.IsCopy,
            TypeKind.Enum when type.Enum!.ResultTypes is var (ok, err) => IsCopy(ok) && IsCopy(err),
            TypeKind.Enum => type.Enum!.IsCopy,
            TypeKind.Reference => !type.IsMutableReference,
            TypeKind.Box or TypeKind.Region => false,
            _ => true,
        };
    }

    public static bool NeedsDrop(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsBox || type.Region is RegionKind.Arena or RegionKind.ScratchFrame)
        {
            return true;
        }
        if (type.Enum?.ResultTypes is var (ok, err))
        {
            return NeedsDrop(ok) || NeedsDrop(err); // T and E may be declared later than the Result is named
        }
        return type.Struct is { NeedsDrop: true } || type.Enum is { NeedsDrop: true };
    }

    /// <summary>
    /// May a value of this type hold loans (type-system.md §19.1)? References, slices, and results
    /// with a scoped <c>T</c> or <c>E</c>. Scoped types follow the reference placement rules (N0246).
    /// </summary>
    public static bool IsScoped(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsReference || type.Region is RegionKind.FixedBuffer or RegionKind.ScratchFrame
            || (type.Enum?.ResultTypes is var (ok, err) && (IsScoped(ok) || IsScoped(err)));
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
