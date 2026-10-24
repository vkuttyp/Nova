using System.Collections.Immutable;
using System.Numerics;
using Nova.Compiler.Hir;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Semantics;

/// <summary>
/// Properties of the compilation target that affect semantics. This is the only source of
/// target layout facts: never use host .NET properties (docs/decisions/0001).
/// </summary>
/// <summary>A compilation profile (cosmoos/profiles.md): what a package may use, and which runtime it is linked with.</summary>
public enum Profile
{
    /// <summary>A C library and an operating system: the default (Linux, macOS, Windows, CosmoOS userland).</summary>
    Hosted,

    /// <summary>No C library and no operating system (§4): no heap, no I/O, no `main`; the environment supplies `nova_panic`.</summary>
    Freestanding,
}

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
    /// <summary>
    /// <c>Vector&lt;T, A&gt;</c> or <c>String&lt;A&gt;</c> (type-system.md §23): owns a growable buffer from allocator
    /// type <see cref="NovaType.Allocator"/>. <see cref="NovaType.Pointee"/> is the unsized <c>[T]</c> (or <c>str</c>)
    /// it dereferences to, like a box: elements are places of the vector.
    /// </summary>
    Vector,
    /// <summary>
    /// <c>StableVector&lt;T, A&gt;</c> (type-system.md §24.2): grow-only chunks; dereferences like a vector to the
    /// unsized <c>[T]</c> (<see cref="NovaType.Pointee"/>), but its elements are not contiguous.
    /// </summary>
    StableVector,
    /// <summary><c>SlotMap&lt;T, A&gt;</c> (§24.1): values of <see cref="NovaType.Element"/> in generation-checked slots.</summary>
    SlotMap,
    /// <summary><c>Handle&lt;T&gt;</c> (§24.1): a Copy (index, generation) naming a slot-map value of <see cref="NovaType.Element"/>.</summary>
    Handle,
    /// <summary>An operating-system resource (type-system.md §26): <see cref="NovaType.Resource"/> says which.</summary>
    Resource,
    /// <summary>A type parameter inside a generic declaration (type-system.md §20.3): opaque.</summary>
    TypeParameter,
    /// <summary>
    /// <c>*T</c> or <c>*mut T</c> (type-system.md §27): a Copy address of <see cref="NovaType.Pointee"/> (or <c>void</c>),
    /// with no loans. Never dereferenced implicitly.
    /// </summary>
    RawPointer,
    /// <summary><c>CChar</c> (type-system.md §28.3): C's <c>char</c>, only ever a pointee.</summary>
    CChar,
    /// <summary>
    /// <c>extern "C" fn(…) -> R</c> (type-system.md §29.5): the address of a C function, with
    /// <see cref="NovaType.ParameterTypes"/> and <see cref="NovaType.ResultType"/>.
    /// </summary>
    FunctionPointer,
}

/// <summary>
/// An interface bound <c>T: I&lt;Args&gt;</c> (type-system.md §21.2). <see cref="Interface"/> identifies the
/// interface declaration (the checker's entry); <see cref="Arguments"/> may mention type parameters.
/// </summary>
public sealed record InterfaceBound(object Interface, string Name, ImmutableArray<NovaType> Arguments)
{
    public override string ToString() => Arguments.IsEmpty ? Name : $"{Name}<{string.Join(", ", Arguments)}>";
}

/// <summary>The built-in region types (ADR 0004).</summary>
/// <summary>The built-in operating-system resources (type-system.md §26).</summary>
public enum ResourceKind
{
    None,
    File,
    TcpListener,
    TcpStream,
}

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
    /// <summary><c>Pool&lt;T&gt;</c> (§19.6): <see cref="NovaType.Element"/> is T; its boxes are <c>PoolBox&lt;T&gt;</c>.</summary>
    Pool,
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

    /// <summary>A field of a <c>@repr(C)</c> struct: named as in Nova in C, too (type-system.md §28.5).</summary>
    public bool HasNovaCName { get; internal set; }

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

    /// <summary>For an instantiation of a generic enum (§20.4): its type arguments; empty otherwise.</summary>
    public ImmutableArray<NovaType> TypeArguments { get; internal set; } = [];

    /// <summary>For an optional <c>T?</c> (type-system.md §22.1): T. Its variants are <c>null</c> (0) and <c>Some</c> (1).</summary>
    public NovaType? OptionType { get; internal set; }

    public List<EnumVariant> Variants { get; } = [];

    public bool IsCopy { get; internal set; } = true;

    public bool NeedsDrop { get; internal set; }

    public bool IsRecursive { get; internal set; }

    /// <summary>The integer type of <c>@repr(uN)</c> / <c>@repr(iN)</c> (type-system.md §28.5); null otherwise.</summary>
    public NovaType? Repr { get; internal set; }

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

    /// <summary>For an instantiation of a generic struct (§20.4): its type arguments; empty otherwise.</summary>
    public ImmutableArray<NovaType> TypeArguments { get; internal set; } = [];

    /// <summary>Set if the struct contains itself by value (N0227); such a struct has no layout.</summary>
    public bool IsRecursive { get; internal set; }

    /// <summary><c>@repr(C)</c> (type-system.md §28.5): in C it is <c>struct Name</c> with members named as in Nova.</summary>
    public bool IsReprC { get; internal set; }

    /// <summary>For a user-defined allocator (type-system.md §30.2): its <c>allocate</c> and <c>deallocate</c>.</summary>
    public Hir.HirFunction? AllocateMethod { get; internal set; }

    public Hir.HirFunction? DeallocateMethod { get; internal set; }

    /// <summary>
    /// An imported C type (type-system.md §31): its layout is the C compiler's, so Nova has none, and it is named
    /// <see cref="CSpelling"/> in C.
    /// </summary>
    public bool IsImported { get; internal set; }

    /// <summary>For an imported C type: <c>struct name</c>, or its <c>@ctype</c> name (type-system.md §31.1).</summary>
    public string? CSpelling { get; internal set; }

    /// <summary><c>@noncopy</c> (OD-7, type-system.md §29.2): a move type even if every field is Copy.</summary>
    public bool IsNonCopy { get; internal set; }

    /// <summary>The <c>n</c> of <c>@align(n)</c> (type-system.md §29.4), or 1.</summary>
    public int ExplicitAlign { get; internal set; } = 1;

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
        : this(TypeKind.Reference, pointee.IsStr ? "StringView"
            : pointee.IsUnsized ? (isMutable ? "SliceMut<" : "Slice<") + pointee.Element!.Name + ">"
            : (isMutable ? "borrow mut " : "borrow ") + pointee.Name)
    {
        Pointee = pointee;
        IsMutableReference = isMutable;
    }

    /// <summary>A raw pointer (§27.1); the third parameter only selects this constructor.</summary>
    internal NovaType(NovaType pointee, bool isMutable, TypeKind kind)
        : this(kind, (isMutable ? "*mut " : "*") + pointee.Name)
    {
        Pointee = pointee;
        IsMutablePointer = isMutable;
    }

    /// <summary>True for <c>*mut T</c>.</summary>
    public bool IsMutablePointer { get; }

    /// <summary>A C function pointer type (§29.5).</summary>
    internal NovaType(ImmutableArray<NovaType> parameters, NovaType result)
        : this(TypeKind.FunctionPointer,
            $"extern \"C\" fn({string.Join(", ", parameters.Select(p => p.Name))}){(result.Kind == TypeKind.Void ? "" : " -> " + result.Name)}")
    {
        ParameterTypes = parameters;
        ResultType = result;
    }

    /// <summary>For a function pointer type: its parameter types; empty otherwise.</summary>
    public ImmutableArray<NovaType> ParameterTypes { get; } = [];

    /// <summary>For a function pointer type: its result type; null otherwise.</summary>
    public NovaType? ResultType { get; }

    public bool IsFunctionPointer => Kind == TypeKind.FunctionPointer;

    public bool IsRawPointer => Kind == TypeKind.RawPointer;

    /// <summary>A type parameter (§20.3).</summary>
    internal NovaType(string name)
        : this(TypeKind.TypeParameter, name)
    {
    }

    /// <summary>For a type parameter: its interface bounds (§21.2).</summary>
    public List<InterfaceBound> Bounds { get; } = [];

    /// <summary>For a type parameter: bounded by <c>Copy</c>, so a Copy type that never needs drop (§21.2).</summary>
    public bool IsCopyBound { get; internal set; }

    internal NovaType(RegionKind region, string name, NovaType? element = null)
        : this(TypeKind.Region, name)
    {
        Region = region;
        Element = element;
    }

    internal NovaType(NovaType contents, NovaType allocator, bool defaultAllocator)
        : this(TypeKind.Box, allocator.Region == RegionKind.Pool ? $"PoolBox<{contents.Name}>"
            : defaultAllocator ? $"Box<{contents.Name}>" : $"Box<{contents.Name}, {allocator.Name}>")
    {
        Pointee = contents;
        Allocator = allocator;
    }

    internal NovaType(NovaType elements, NovaType allocator, string name, TypeKind kind = TypeKind.Vector)
        : this(kind, name)
    {
        if (kind == TypeKind.SlotMap)
        {
            Element = elements;
        }
        else
        {
            Pointee = elements;
        }
        Allocator = allocator;
    }

    /// <summary><c>File</c>, <c>TcpListener</c> or <c>TcpStream</c> (§26): owns a descriptor, closed by drop.</summary>
    internal NovaType(ResourceKind resource, string name)
        : this(TypeKind.Resource, name) => Resource = resource;

    /// <summary><c>Handle&lt;T&gt;</c>.</summary>
    internal NovaType(NovaType element, string name)
        : this(TypeKind.Handle, name) => Element = element;

    internal NovaType(NovaType element, long length, bool isStr = false)
        : this(TypeKind.Array, isStr ? "str" : length < 0 ? $"[{element.Name}]" : $"[{element.Name}; {length}]")
    {
        Element = element;
        ArrayLength = length;
        IsStr = isStr;
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

    private static int s_nextId;

    /// <summary>A unique number per type object: types compare by reference, and this keys caches by identity.</summary>
    public int Id { get; } = Interlocked.Increment(ref s_nextId);

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

    /// <summary>
    /// The unsized UTF-8 text a <c>StringView</c> refers to (§22.2): bytes like <c>[u8]</c>, but a distinct
    /// type, so a view is not a <c>Slice&lt;u8&gt;</c>. Not nameable.
    /// </summary>
    public bool IsStr { get; }

    /// <summary><c>StringView</c>: a shared reference to <c>str</c>.</summary>
    public bool IsStringView => Kind == TypeKind.Reference && Pointee!.IsStr;

    /// <summary>The referent type of a reference, or the contents of a box; null for every other kind.</summary>
    public NovaType? Pointee { get; }

    /// <summary>The allocator type of a box; null for every other kind.</summary>
    public NovaType? Allocator { get; }

    public bool IsBox => Kind == TypeKind.Box;

    /// <summary><c>Vector&lt;T, A&gt;</c> or <c>String&lt;A&gt;</c> (§23).</summary>
    public bool IsVector => Kind == TypeKind.Vector;

    /// <summary><c>StableVector&lt;T, A&gt;</c> (§24.2).</summary>
    public bool IsStableVector => Kind == TypeKind.StableVector;

    /// <summary><c>String&lt;A&gt;</c> (§23.2): a vector of UTF-8 text.</summary>
    public bool IsString => Kind == TypeKind.Vector && Pointee!.IsStr;

    /// <summary>
    /// Does the type mention a type parameter (§20.3)? Such types belong to generic code, which is
    /// checked but never compiled to C.
    /// </summary>
    public bool IsGeneric => Kind == TypeKind.TypeParameter
        || (Pointee?.IsGeneric ?? false) || (Element?.IsGeneric ?? false)
        || (Struct?.TypeArguments.Any(a => a.IsGeneric) ?? false)
        || (Enum?.TypeArguments.Any(a => a.IsGeneric) ?? false)
        || (Enum?.ResultTypes is var (ok, err) && (ok.IsGeneric || err.IsGeneric))
        || (Enum?.OptionType?.IsGeneric ?? false);

    /// <summary><c>PoolBox&lt;T&gt;</c> (type-system.md §19.6): a box whose allocator is a pool; scoped.</summary>
    public bool IsPoolBox => Kind == TypeKind.Box && Allocator!.Region == RegionKind.Pool;

    /// <summary>Which region type this is; <see cref="RegionKind.None"/> for every other kind.</summary>
    public RegionKind Region { get; }

    /// <summary>Which resource type this is (§26); <see cref="ResourceKind.None"/> for every other kind.</summary>
    public ResourceKind Resource { get; }

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
        Str = new NovaType(U8, -1, isStr: true);
        Error = new NovaType(TypeKind.Error, "<error>");
        Arena = new NovaType(RegionKind.Arena, "Arena");
        FixedBufferAllocator = new NovaType(RegionKind.FixedBuffer, "FixedBufferAllocator");
        ScratchFrame = new NovaType(RegionKind.ScratchFrame, "ScratchFrame");
        File = new NovaType(ResourceKind.File, "File");
        CChar = new NovaType(TypeKind.CChar, "CChar");
        TcpListener = new NovaType(ResourceKind.TcpListener, "TcpListener");
        TcpStream = new NovaType(ResourceKind.TcpStream, "TcpStream");

        _byName = new Dictionary<string, NovaType>(StringComparer.Ordinal);
        foreach (var t in new[] { I8, I16, I32, I64, ISize, U8, U16, U32, U64, USize, Bool, Void, CChar, Arena, FixedBufferAllocator, ScratchFrame, File, TcpListener, TcpStream })
        {
            _byName.Add(t.Name, t);
        }
        StringView = ReferenceTo(Str, isMutable: false);
        _byName.Add(StringView.Name, StringView);
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

    /// <summary><c>CChar</c> (§28.3).</summary>
    public NovaType CChar { get; }
    public NovaType File { get; }
    public NovaType TcpListener { get; }
    public NovaType TcpStream { get; }

    /// <summary>The unsized text behind a <c>StringView</c> (§22.2).</summary>
    public NovaType Str { get; }

    /// <summary><c>StringView</c> (§22.2).</summary>
    public NovaType StringView { get; }
    public NovaType Error { get; }
    public NovaType Arena { get; }
    public NovaType FixedBufferAllocator { get; }
    public NovaType ScratchFrame { get; }

    public static NovaType CreateStruct(string name, StructDecl syntax, SourceText source) =>
        new(name, new StructDefinition(syntax, source));

    /// <summary>A fresh type parameter (each declaration's parameters are distinct types).</summary>
    public static NovaType CreateTypeParameter(string name) => new(name);

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

    private readonly Dictionary<string, NovaType> _functionPointers = [];

    /// <summary>The unique <c>extern "C" fn(…) -> R</c> with these types (§29.5), keyed by the types' identities.</summary>
    public NovaType FunctionPointerTo(ImmutableArray<NovaType> parameters, NovaType result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var key = string.Join(",", parameters.Select(p => p.Id)) + "->" + result.Id;
        if (!_functionPointers.TryGetValue(key, out var type))
        {
            type = new NovaType(parameters, result);
            _functionPointers.Add(key, type);
            _functionPointerList.Add(type);
        }
        return type;
    }

    private readonly List<NovaType> _functionPointerList = [];

    /// <summary>Function pointer types in creation order (deterministic), for the C typedefs.</summary>
    public IReadOnlyList<NovaType> FunctionPointers => _functionPointerList;

    private readonly Dictionary<(NovaType, bool), NovaType> _pointers = [];

    /// <summary>The unique <c>*T</c> or <c>*mut T</c> (§27.1).</summary>
    public NovaType PointerTo(NovaType pointee, bool isMutable)
    {
        ArgumentNullException.ThrowIfNull(pointee);
        if (!_pointers.TryGetValue((pointee, isMutable), out var pointer))
        {
            pointer = new NovaType(pointee, isMutable, TypeKind.RawPointer);
            _pointers.Add((pointee, isMutable), pointer);
        }
        return pointer;
    }

    /// <summary><c>Slice&lt;T&gt;</c> or <c>SliceMut&lt;T&gt;</c>.</summary>
    public NovaType SliceOf(NovaType element, bool isMutable) => ReferenceTo(ArrayOf(element, -1), isMutable);

    private readonly Dictionary<(NovaType, NovaType), NovaType> _results = [];
    private readonly Dictionary<(NovaType, NovaType), NovaType> _boxes = [];
    private readonly Dictionary<NovaType, NovaType> _options = [];

    /// <summary>The unique optional <c>value?</c> (§22.1): an enum with variants <c>null</c> and <c>Some(value)</c>.</summary>
    public NovaType OptionOf(NovaType value, Symbol nullName, Symbol someName, Symbol payloadName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_options.TryGetValue(value, out var option))
        {
            var definition = new EnumDefinition(null, null) { OptionType = value };
            definition.Variants.Add(new EnumVariant(nullName, 0, default));
            var some = new EnumVariant(someName, 1, default);
            some.Payload.Add(new StructField(payloadName, value, 0, default));
            definition.Variants.Add(some);
            option = new NovaType($"{value.Name}?", definition);
            _options.Add(value, option);
            _composites.Add(option);
        }
        return option;
    }

    private readonly Dictionary<NovaType, NovaType> _pools = [];

    /// <summary>The unique <c>Pool&lt;T&gt;</c> (type-system.md §19.6).</summary>
    public NovaType PoolOf(NovaType element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_pools.TryGetValue(element, out var pool))
        {
            pool = new NovaType(RegionKind.Pool, $"Pool<{element.Name}>", element);
            _pools.Add(element, pool);
        }
        return pool;
    }

    /// <summary>The unique <c>PoolBox&lt;T&gt;</c>: a box whose allocator is <c>Pool&lt;T&gt;</c>.</summary>
    public NovaType PoolBoxOf(NovaType element) => BoxOf(element, PoolOf(element), isDefault: false);

    /// <summary>The unique <c>Box&lt;contents, allocator&gt;</c>; <paramref name="isDefault"/> when the allocator is <c>SystemHeap</c>.</summary>
    private readonly Dictionary<(NovaType, NovaType), NovaType> _vectors = [];

    /// <summary>
    /// The unique <c>Vector&lt;element, allocator&gt;</c> (§23.1), or <c>String&lt;allocator&gt;</c> (§23.2) when
    /// <paramref name="element"/> is null.
    /// </summary>
    public NovaType VectorOf(NovaType? element, NovaType allocator, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        var elements = element is null ? Str : ArrayOf(element, -1);
        if (!_vectors.TryGetValue((elements, allocator), out var vector))
        {
            var name = element is null
                ? isDefault ? "String" : $"String<{allocator.Name}>"
                : isDefault ? $"Vector<{element.Name}>" : $"Vector<{element.Name}, {allocator.Name}>";
            vector = new NovaType(elements, allocator, name);
            _vectors.Add((elements, allocator), vector);
            _composites.Add(vector);
        }
        return vector;
    }

    private readonly Dictionary<(TypeKind, NovaType, NovaType?), NovaType> _containers = [];

    /// <summary>The unique <c>StableVector&lt;T, A&gt;</c> or <c>SlotMap&lt;T, A&gt;</c> (§24).</summary>
    public NovaType ContainerOf(TypeKind kind, NovaType element, NovaType allocator, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(allocator);
        if (!_containers.TryGetValue((kind, element, allocator), out var container))
        {
            var baseName = kind == TypeKind.SlotMap ? "SlotMap" : "StableVector";
            var name = isDefault ? $"{baseName}<{element.Name}>" : $"{baseName}<{element.Name}, {allocator.Name}>";
            container = new NovaType(kind == TypeKind.SlotMap ? element : ArrayOf(element, -1), allocator, name, kind);
            _containers.Add((kind, element, allocator), container);
            _composites.Add(container);
        }
        return container;
    }

    /// <summary>The unique <c>Handle&lt;T&gt;</c> (§24.1).</summary>
    public NovaType HandleOf(NovaType element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_containers.TryGetValue((TypeKind.Handle, element, null), out var handle))
        {
            handle = new NovaType(element, $"Handle<{element.Name}>");
            _containers.Add((TypeKind.Handle, element, null), handle);
        }
        return handle;
    }

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
            case TypeKind.RawPointer:
                return (8, 8); // one address (§27.1)
            case TypeKind.CChar:
                return (1, 1); // C's char (§28.3)
            case TypeKind.FunctionPointer:
                return (8, 8); // one code address (§29.5)
            case TypeKind.Box:
                return type.IsPoolBox ? (16, 8) : (8, 8); // a pool box also points to its pool
            case TypeKind.Vector:
                return (24, 8); // (pointer, u64 length, u64 capacity)
            case TypeKind.StableVector:
                return (264, 8); // 32 chunk pointers, u64 length
            case TypeKind.SlotMap:
                return (40, 8); // pointer, u64 length, capacity, count, u32 free head
            case TypeKind.Handle:
                return (8, 4); // u32 index, u32 generation
            case TypeKind.Resource:
                return (4, 4); // an int32_t descriptor
            case TypeKind.Region:
                // nv_arena, nv_fba, nv_scratch in nova/rt.h (8-byte fields; asserted there).
                return type.Region switch
                {
                    RegionKind.Arena => (40, 8),
                    RegionKind.FixedBuffer => (24, 8),
                    RegionKind.Pool => (48, 8),
                    _ => (32, 8),
                };
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
                if (definition.IsImported)
                {
                    throw new InvalidOperationException($"imported C type '{type}' has no Nova layout (type-system.md §31.2)");
                }
                int offset = 0, align = definition.ExplicitAlign; // `@align(n)` (§29.4), else 1
                foreach (var field in definition.Fields)
                {
                    var (fieldSize, fieldAlign) = LayoutOf(field.Type);
                    offset = (offset + fieldAlign - 1) / fieldAlign * fieldAlign;
                    field.Offset = offset;
                    offset += fieldSize;
                    align = Math.Max(align, fieldAlign);
                }
                var size = ((definition.Fields.Count == 0 ? 1 : offset) + align - 1) / align * align;
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
        // `@repr(uN)` / `@repr(iN)` fixes the tag's width (§28.5).
        var tagSize = definition.Repr is { } repr ? repr.Bits / 8
            : definition.Variants.Count <= 256 ? 1 : definition.Variants.Count <= 65536 ? 2 : 4;
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
            TypeKind.TypeParameter => type.IsCopyBound, // a move type (§20.3) unless bounded by Copy (§21.2)
            TypeKind.Struct => type.Struct!.IsCopy,
            TypeKind.Enum when type.Enum!.ResultTypes is var (ok, err) => IsCopy(ok) && IsCopy(err),
            TypeKind.Enum when type.Enum!.OptionType is { } value => IsCopy(value),
            TypeKind.Enum => type.Enum!.IsCopy,
            TypeKind.Reference => !type.IsMutableReference,
            TypeKind.Box or TypeKind.Region or TypeKind.Vector or TypeKind.StableVector or TypeKind.SlotMap or TypeKind.Resource => false,
            _ => true,
        };
    }

    public static bool NeedsDrop(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Kind == TypeKind.TypeParameter)
        {
            return !type.IsCopyBound;
        }
        if (type.IsBox || type.Kind is TypeKind.Vector or TypeKind.StableVector or TypeKind.SlotMap or TypeKind.Resource || type.Region is RegionKind.Arena or RegionKind.ScratchFrame or RegionKind.Pool)
        {
            return true;
        }
        if (type.Enum?.OptionType is { } value)
        {
            return NeedsDrop(value);
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
        return type.IsReference || type.IsPoolBox || type.Region is RegionKind.FixedBuffer or RegionKind.ScratchFrame
            || (type.Enum?.ResultTypes is var (ok, err) && (IsScoped(ok) || IsScoped(err)))
            || (type.Enum?.OptionType is { } value && IsScoped(value))
            // §25.3: region parameters, scoped type arguments, scoped collection elements.
            || (type.Struct is { } s && (!s.Syntax.RegionParameters.IsEmpty || s.TypeArguments.Any(IsScoped)))
            || (type.Enum is { Syntax: { } e } definition && (!e.RegionParameters.IsEmpty || definition.TypeArguments.Any(IsScoped)))
            || (type.Kind is TypeKind.Vector or TypeKind.StableVector && IsScoped(type.Pointee!.Element!))
            || (type.Kind == TypeKind.SlotMap && IsScoped(type.Element!));
    }

    /// <summary>
    /// Can destroying a value of <paramref name="type"/> read the loans it holds (type-system.md §25.7 rule 6)? Only
    /// a user <c>drop</c> block of a scoped struct can, directly or inside the value (fields, payloads, elements).
    /// </summary>
    public static bool DropReadsLoans(NovaType type) => DropReadsLoans(type, []);

    private static bool DropReadsLoans(NovaType type, HashSet<NovaType> visiting)
    {
        if (!IsScoped(type) || !NeedsDrop(type) || !visiting.Add(type))
        {
            return false;
        }
        return type switch
        {
            { Struct: { } s } => s.DropFunction is not null || s.Fields.Any(f => DropReadsLoans(f.Type, visiting)),
            { Enum: { } e } => e.Variants.Any(v => v.Payload.Any(f => DropReadsLoans(f.Type, visiting))),
            { Kind: TypeKind.Vector or TypeKind.StableVector } => DropReadsLoans(type.Pointee!.Element!, visiting),
            { Kind: TypeKind.SlotMap } => DropReadsLoans(type.Element!, visiting),
            _ => false,
        };
    }

    /// <summary>
    /// Does a value of <paramref name="type"/> contain a scoped region value (a fixed-buffer allocator, a
    /// scratch frame) or a pool box? Those stay out of fields, collections and type arguments even
    /// where other scoped values may go (type-system.md §25.4): they must stay pinned or LIFO.
    /// </summary>
    public static bool HoldsRegionValue(NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Region is RegionKind.FixedBuffer or RegionKind.ScratchFrame || type.IsPoolBox
            || (type.Enum?.ResultTypes is var (ok, err) && (HoldsRegionValue(ok) || HoldsRegionValue(err)))
            || (type.Enum?.OptionType is { } value && HoldsRegionValue(value))
            || (type.Struct?.TypeArguments.Any(HoldsRegionValue) ?? false)
            || (type.Enum?.TypeArguments.Any(HoldsRegionValue) ?? false)
            || (type.Kind is TypeKind.Vector or TypeKind.StableVector && HoldsRegionValue(type.Pointee!.Element!))
            || (type.Kind == TypeKind.SlotMap && HoldsRegionValue(type.Element!));
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
