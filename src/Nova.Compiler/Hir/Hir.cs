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

    /// <summary>The <c>@header</c>s of <c>extern "C"</c> blocks, each once, in declaration order (type-system.md §31.3).</summary>
    public ImmutableArray<string> Headers { get; init; } = [];
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

    /// <summary>Declared with <c>using</c>: cannot be moved out of (type-system.md §14.6).</summary>
    public bool IsUsing { get; internal set; }
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

    /// <summary>For an instantiated generic function (§20.4): its own type arguments; empty otherwise.</summary>
    public ImmutableArray<NovaType> TypeArguments { get; internal set; } = [];

    /// <summary>The type parameters in scope (the function's and its owner's), mapped to their types.</summary>
    internal IReadOnlyDictionary<Symbol, NovaType> Environment { get; set; } = new Dictionary<Symbol, NovaType>();

    /// <summary>
    /// Generic code (§20.3): a type parameter is in scope unsubstituted. It is checked (types, moves,
    /// borrows) but never compiled.
    /// </summary>
    public bool IsGeneric => Environment.Values.Any(t => t.IsGeneric) || (Owner?.IsGeneric ?? false);

    /// <summary>
    /// A monomorphised copy of generic code: its generic version was checked (§20.3), so its own checks
    /// would only repeat those diagnostics.
    /// </summary>
    public bool IsInstantiation => Environment.Count > 0 && !IsGeneric;

    /// <summary>How many instantiations led here (§20.4: at most 64).</summary>
    internal int InstantiationDepth { get; set; }

    /// <summary>Where an instantiation was first written, for N0274 (type-system.md §25.4).</summary>
    internal (SourceText Source, TextSpan Span)? InstantiationSite { get; set; }

    /// <summary>Declared in an <c>extern "C"</c> block: defined in C (type-system.md §28.2).</summary>
    public bool IsExtern => Syntax.IsExtern;

    /// <summary>The C symbol of an <c>@export</c> function (type-system.md §28.4); null otherwise.</summary>
    public string? ExportName { get; internal set; }

    /// <summary>True for a method: its first parameter is the <c>this</c> receiver (type-system.md §12).</summary>
    public bool IsMethod => Syntax.Receiver is not null && Owner is not null;

    public ReceiverKind ReceiverKind => IsMethod ? Syntax.Receiver!.Kind : ReceiverKind.Value;

    /// <summary>
    /// For a function returning a reference: indices into <see cref="Parameters"/> of the reference
    /// parameters the result may be derived from (lifetimes.md §7). Empty otherwise.
    /// </summary>
    public ImmutableArray<int> ReturnSources { get; internal set; } = [];

    /// <summary>
    /// Parameter flows into <c>borrow mut</c> parameters (type-system.md §25.6): <c>(Target, Source)</c> means the
    /// callee may store the loans of parameter <c>Source</c> in what parameter <c>Target</c> refers to.
    /// </summary>
    public ImmutableArray<(int Target, int Source)> MutableFlows { get; internal set; } = [];

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

/// <summary>
/// A local followed by projections: <c>p</c>, <c>p.a.b</c>, <c>(*r).f</c>, <c>a[i]</c>. An Index projection
/// holds the index as a <see cref="HirExpr"/>, evaluated (and bounds-checked) when the place is lowered.
/// </summary>
public sealed record HirPlace(HirLocal Root, ImmutableArray<Projection> Projections, NovaType Type)
{
    public bool IsLocal => Projections.IsEmpty;
}

/// <summary>Assignment to a place. <see cref="Op"/> is null for plain '=', else the compound operator.</summary>
public sealed record HirAssign(HirPlace Target, BinaryOp? Op, HirExpr Value, TextSpan Span) : HirStmt(Span);

public sealed record HirIf(HirExpr Condition, HirBlock Then, HirStmt? Else, TextSpan Span) : HirStmt(Span);

public sealed record HirWhile(HirExpr Condition, HirBlock Body, TextSpan Span) : HirStmt(Span);

public sealed record HirLoop(HirBlock Body, TextSpan Span) : HirStmt(Span);

/// <summary>
/// <c>for</c> (type-system.md §16.4). With <see cref="End"/>, <see cref="Start"/>..End is an integer range;
/// without, <see cref="Start"/> is the sequence: a value of array type or an unsized <c>[T]</c> behind a slice.
/// </summary>
public sealed record HirFor(HirLocal Variable, HirExpr Start, HirExpr? End, HirBlock Body, TextSpan Span) : HirStmt(Span);

/// <summary>Leaves the innermost enclosing loop.</summary>
public sealed record HirBreak(TextSpan Span) : HirStmt(Span);

/// <summary>Jumps to the next iteration of the innermost enclosing loop.</summary>
public sealed record HirContinue(TextSpan Span) : HirStmt(Span);

public sealed record HirReturn(HirExpr? Value, TextSpan Span) : HirStmt(Span);

public sealed record HirExprStmt(HirExpr Expression, TextSpan Span) : HirStmt(Span);

/// <summary>A `match` statement (type-system.md §15.2); arms are tried in order.</summary>
public sealed record HirMatch(HirExpr Scrutinee, ImmutableArray<HirArm> Arms, TextSpan Span) : HirStmt(Span)
{
    /// <summary>The match moves non-Copy payloads out of its value scrutinee (type-system.md §18.4).</summary>
    public bool Consumes { get; init; }
}

public enum HirPatternKind
{
    Wildcard,
    Variant,
    IntegerLiteral,
    Bool,
}

/// <summary>
/// One arm. <see cref="Variant"/> is set for variant patterns; <see cref="Value"/> for literal ones.
/// Each binding receives payload <c>PayloadIndex</c>: a copy, or a reference when the scrutinee is a reference.
/// </summary>
public sealed record HirArm(
    HirPatternKind Kind,
    EnumVariant? Variant,
    Int128 Value,
    ImmutableArray<(HirLocal Local, int PayloadIndex)> Bindings,
    HirBlock Body,
    TextSpan Span)
{
    /// <summary>The pattern lists the payloads (<c>E.V(…)</c>); in a consuming match all of them move out.</summary>
    public bool ListsPayloads { get; init; }
}

/// <summary>Runs <see cref="Body"/> when the enclosing block exits (type-system.md §14.7).</summary>
public sealed record HirDefer(HirStmt Body, TextSpan Span) : HirStmt(Span);

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
    /// <summary><c>Box.create(v)</c> / <c>Box.createIn(a, v)</c>: arguments are the value (and the allocator first).</summary>
    BoxCreate,
    /// <summary><c>FailingAllocator.failAfter(n)</c>.</summary>
    FailAfter,
    /// <summary><c>FailingAllocator.reset()</c>.</summary>
    FailReset,
    /// <summary><c>Arena.create(allocator, chunkSize)</c> (type-system.md §19.2).</summary>
    ArenaCreate,
    /// <summary><c>region.alloc(v)</c>: arguments are a shared reference to the region (any region type), then the value.</summary>
    ArenaAlloc,
    /// <summary><c>arena.allocArray(n, fill)</c>: the arena reference, the count, the fill value.</summary>
    ArenaAllocArray,
    /// <summary><c>arena.reset()</c>: a mutable reference to the arena.</summary>
    ArenaReset,
    /// <summary><c>FixedBufferAllocator.over(mut storage)</c>: a <c>SliceMut&lt;u8&gt;</c> of the storage (§19.4).</summary>
    FixedBufferOver,
    /// <summary><c>Scratch.enter()</c>: a root frame (§19.5).</summary>
    ScratchEnter,
    /// <summary><c>frame.enter()</c>: a mutable reference to the parent frame.</summary>
    ScratchNest,
    /// <summary><c>Pool.create(allocator, capacity)</c> (§19.6).</summary>
    PoolCreate,
    /// <summary><c>pool.take(v)</c>: a shared reference to the pool, then the value.</summary>
    PoolTake,
    /// <summary><c>Vector&lt;T&gt;.create()</c> / <c>String.create()</c> (type-system.md §23): no arguments.</summary>
    VectorCreate,
    /// <summary><c>Vector&lt;T&gt;.withCapacity(n)</c> / <c>String.withCapacity(n)</c>: the count.</summary>
    VectorWithCapacity,
    /// <summary><c>v.push(x)</c>: a mutable reference to the vector, then the value.</summary>
    VectorPush,
    /// <summary><c>v.pop()</c>: a mutable reference to the vector.</summary>
    VectorPop,
    /// <summary><c>v.clear()</c> / <c>s.clear()</c>: a mutable reference to the vector or string.</summary>
    VectorClear,
    /// <summary><c>v.capacity</c>: a shared reference to the vector or string.</summary>
    VectorCapacity,
    /// <summary><c>s.append(text)</c>: a mutable reference to the string, then the view.</summary>
    StringAppend,
    /// <summary><c>sv.push(x)</c> (§24.2): a shared reference to the stable vector, then the value.</summary>
    StableVectorPush,
    /// <summary><c>m.insert(v)</c> (§24.1): a mutable reference to the map, then the value.</summary>
    SlotMapInsert,
    /// <summary><c>m.get(h)</c> / <c>m.getMut(h)</c>: a shared or mutable reference to the map, then the handle.</summary>
    SlotMapGet,
    /// <summary><c>m.remove(h)</c>: a mutable reference to the map, then the handle.</summary>
    SlotMapRemove,
    /// <summary><c>m.contains(h)</c>: a shared reference to the map, then the handle.</summary>
    SlotMapContains,
    /// <summary><c>m.length</c>: a shared reference to the map.</summary>
    SlotMapLength,
    /// <summary><c>File.open(path)</c> (type-system.md §26.3).</summary>
    FileOpen,
    /// <summary><c>File.create(path)</c>.</summary>
    FileCreate,
    /// <summary><c>File.remove(path)</c>.</summary>
    FileRemove,
    /// <summary><c>f.read(mut buffer)</c> on a file or stream: a mutable reference to it, then the buffer.</summary>
    ResourceRead,
    /// <summary><c>f.write(bytes)</c> on a file or stream: a mutable reference to it, then the bytes.</summary>
    ResourceWrite,
    /// <summary><c>Console.read(mut buffer)</c>: standard input.</summary>
    ConsoleRead,
    /// <summary><c>Console.writeBytes(bytes)</c>.</summary>
    ConsoleWriteBytes,
    /// <summary><c>Console.writeError(text)</c>: standard error.</summary>
    ConsoleWriteError,
    /// <summary><c>Console.writeErrorLine(text?)</c>: standard error, then a newline.</summary>
    ConsoleWriteErrorLine,
    /// <summary><c>TcpListener.bind(address, port)</c> (§26.4).</summary>
    TcpListen,
    /// <summary><c>listener.port()</c>: a shared reference to the listener.</summary>
    TcpPort,
    /// <summary><c>listener.accept()</c>: a shared reference to the listener.</summary>
    TcpAccept,
    /// <summary><c>TcpStream.connect(address, port)</c>.</summary>
    TcpConnect,
    /// <summary><c>stream.shutdownWrite()</c>: a mutable reference to the stream.</summary>
    TcpShutdownWrite,
    /// <summary><c>e.message()</c> on an <c>IoError</c> value (§26.2).</summary>
    IoErrorMessage,
    /// <summary><c>Process.argumentCount()</c> (§26.1).</summary>
    ArgumentCount,
    /// <summary><c>Process.argument(i)</c>.</summary>
    Argument,
    /// <summary><c>Process.run(program, arguments)</c> (§26.5): the program, then a shared reference to a <c>Vector&lt;StringView&gt;</c>.</summary>
    ProcessRun,
    /// <summary><c>p.read()</c> (§27.3): the pointer.</summary>
    PtrRead,
    /// <summary><c>p.write(v)</c>: the pointer, then the value.</summary>
    PtrWrite,
    /// <summary><c>p.add(n)</c>: the pointer and a <c>usize</c>.</summary>
    PtrAdd,
    /// <summary><c>p.sub(n)</c>: the pointer and a <c>usize</c>.</summary>
    PtrSub,
    /// <summary><c>p.offset(n)</c>: the pointer and an <c>isize</c>.</summary>
    PtrOffset,
    /// <summary><c>p.isNull()</c>.</summary>
    PtrIsNull,
    /// <summary><c>s.ptr</c> (§27.2): the slice or string view.</summary>
    SlicePtr,
    /// <summary><c>Slice.fromRaw(p, n)</c> / <c>SliceMut.fromRaw(p, n)</c> (§27.3): the pointer and a <c>usize</c>.</summary>
    SliceFromRaw,
    /// <summary><c>p.readVolatile()</c> (§29.1).</summary>
    PtrReadVolatile,
    /// <summary><c>p.writeVolatile(v)</c> (§29.1): the pointer, then the value.</summary>
    PtrWriteVolatile,
    /// <summary><c>Cpu.spinHint()</c> (§29.3).</summary>
    SpinHint,
    /// <summary><c>Target.isX86_64()</c> (§29.3): decided by the C compiler.</summary>
    TargetIsX8664,
    /// <summary><c>Target.isAArch64()</c> (§29.3).</summary>
    TargetIsAArch64,
    /// <summary><c>Target.pointerBits()</c> (§29.3): a constant from <c>TargetInfo</c>, never emitted.</summary>
    TargetPointerBits,
    /// <summary><c>f(args)</c> through a C function pointer (§29.5): the pointer, then the arguments.</summary>
    CallPointer,
}

public sealed record HirBuiltinCall(BuiltinFunction Function, ImmutableArray<HirExpr> Arguments, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>Reads field <see cref="Field"/> of a struct value.</summary>
public sealed record HirFieldAccess(HirExpr Target, StructField Field, TextSpan Span) : HirExpr(Field.Type, Span);

/// <summary>An enum value <c>E.V(payload…)</c> (type-system.md §15.1).</summary>
public sealed record HirEnumValue(EnumVariant Variant, ImmutableArray<HirExpr> Payload, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>A struct literal. <see cref="Fields"/> is in source order, which is also evaluation order.</summary>
public sealed record HirStructLiteral(ImmutableArray<(StructField Field, HirExpr Value)> Fields, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>borrow p</c> / <c>borrow mut p</c> of a place (type-system.md §13.2).</summary>
public sealed record HirBorrow(bool IsMutable, HirPlace Place, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>An implicit call-site borrow of a value: the value lives in a temporary until the end of the statement.</summary>
public sealed record HirBorrowTemp(bool IsMutable, HirExpr Value, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>The C address of an extern or exported function, as a C function pointer (type-system.md §29.5).</summary>
public sealed record HirFunctionAddress(HirFunction Function, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>
/// <c>borrow</c> of a place reached through a reference that is not itself a place, such as a call's result:
/// <c>regs.getMut().data</c> (type-system.md §13.2). <see cref="Path"/> is a chain of fields, elements and derefs
/// ending in a deref of that reference, which lives in a temporary until the end of the statement.
/// </summary>
public sealed record HirBorrowPath(bool IsMutable, HirExpr Path, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>A new reference to the referent of <see cref="Reference"/>: a reborrow, or a mut-to-shared coercion.</summary>
public sealed record HirReborrow(bool IsMutable, HirExpr Reference, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>move p</c>: moves the value out of a place, leaving it uninitialised (type-system.md §14.3).</summary>
public sealed record HirMove(HirPlace Place, TextSpan Span) : HirExpr(Place.Type, Span);

/// <summary>Reads the value a reference points to (auto-deref, type-system.md §13.3).</summary>
public sealed record HirDeref(HirExpr Reference, TextSpan Span) : HirExpr(Reference.Type.Pointee!, Span);

/// <summary><c>[a, b, c]</c>.</summary>
public sealed record HirArrayLiteral(ImmutableArray<HirExpr> Elements, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>[v; N]</c>: N copies of the value.</summary>
public sealed record HirArrayRepeat(HirExpr Value, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>a[i]</c>: <see cref="Target"/> is an array or an unsized <c>[T]</c> (a deref'd slice); bounds-checked.</summary>
public sealed record HirIndex(HirExpr Target, HirExpr Index, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>a[lo..hi]</c>: a slice of the array or <c>[T]</c> in <see cref="Base"/>; a missing bound is 0 or the length.</summary>
public sealed record HirSlice(bool IsMutable, HirPlace Base, HirExpr? Low, HirExpr? High, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>The length of the slice <see cref="Slice"/> (an array's length is a constant).</summary>
public sealed record HirSliceLength(HirExpr Slice, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>
/// <c>e?</c> (type-system.md §17.4): the <c>Ok</c> payload of <see cref="Operand"/>, or an early return of
/// its <c>Err</c>. <see cref="HirExpr.Type"/> is the payload type (<c>void</c> for <c>Result&lt;void, E&gt;</c>).
/// </summary>
public sealed record HirTry(HirExpr Operand, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>e!</c>: the <c>Ok</c> payload of <see cref="Operand"/>, or a panic.</summary>
public sealed record HirUnwrap(HirExpr Operand, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary><c>o == null</c> (<see cref="IsNull"/>) or <c>o != null</c>; <see cref="Reference"/> is a shared borrow of the optional (§22.1).</summary>
public sealed record HirIsNull(HirExpr Reference, bool IsNull, NovaType Type, TextSpan Span) : HirExpr(Type, Span);

/// <summary>Stands in for an expression that produced a diagnostic.</summary>
public sealed record HirError(NovaType Type, TextSpan Span) : HirExpr(Type, Span);
