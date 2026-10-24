using System.Text;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Interpreter;

/// <summary>Where the interpreted program's input and output go, and its configuration (intermediate-representation.md §5.4).</summary>
public sealed class InterpreterOptions
{
    public required Stream StandardOutput { get; init; }

    public required Stream StandardError { get; init; }

    /// <summary>Standard input; null reads the process's own.</summary>
    public Stream? StandardInput { get; init; }

    /// <summary>The program's arguments, starting with its name (argv[0]).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["program"];

    /// <summary>NOVA_FAIL_ALLOC_AT: this allocation attempt (1-based) fails; 0 = none.</summary>
    public ulong FailAllocationAt { get; init; }

    /// <summary>NOVA_ALLOC_REPORT: print the allocation counters when <c>main</c> returns.</summary>
    public bool AllocationReport { get; init; }

    /// <summary>Apply M5's placement: stack-placed boxes live in the frame, uncounted (intermediate-representation.md §5.5a).</summary>
    public bool Placement { get; init; }

    /// <summary>If set, every executed <c>Drop</c> is appended as <c>function line:column type</c> (§5.5a).</summary>
    public List<string>? DropTrace { get; init; }

    /// <summary>The directory relative file paths and child processes resolve against; null: the process's own.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Children of <c>Process.run</c> inherit the process's own standard streams (the CLI), instead of being copied into the streams above.</summary>
    public bool InheritStandardStreams { get; init; }
}

/// <summary>
/// The NIR reference interpreter (intermediate-representation.md §5): runs checked NIR with the target's
/// memory layout and the runtime's algorithms, so its output, exit status and allocation counts equal the
/// native program's. Memory-model violations are interpreter faults (exit status 70).
/// </summary>
public sealed partial class NirInterpreter
{
    public const int PanicExitStatus = 134; // 128 + SIGABRT, as abort() gives natively
    public const int FaultExitStatus = 70;

    private readonly NirProgram _program;
    private readonly InterpreterOptions _options;
    private readonly Dictionary<HirFunction, NirFunction> _functions = [];
    private readonly Memory _memory = new();
    private readonly Dictionary<string, ulong> _literals = new(StringComparer.Ordinal);

    // The current position, for fault and panic locations.
    private SourceText? _source;
    private TextSpan _span;

    private readonly SymbolTable _symbols;
    private readonly Analysis.PlacementAnalysis? _placement;

    private NirInterpreter(NirProgram program, SymbolTable symbols, InterpreterOptions options)
    {
        _program = program;
        _symbols = symbols;
        _options = options;
        _placement = options.Placement ? Analysis.PlacementAnalysis.Analyze(program, symbols) : null;
        foreach (var function in program.Functions)
        {
            _functions[function.Source] = function;
        }
        _emptyVector = _memory.Allocate(AllocationKind.Static, 0, initialised: true, "the empty-vector sentinel").Base;
    }

    /// <summary>Runs <c>main</c> and returns the exit status, as the native program would.</summary>
    public static int Run(NirProgram program, SymbolTable symbols, InterpreterOptions options)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(options);
        var main = program.Functions.FirstOrDefault(f => f.Source.Owner is null && symbols.GetName(f.Name) == "main")
            ?? throw new InvalidOperationException("the program has no 'main'");
        var interpreter = new NirInterpreter(program, symbols, options);
        var status = 0;
        Exception? failure = null;
        // Calls recurse on the host stack: give it room for deep Nova recursion (§5.3).
        var thread = new Thread(() =>
        {
            try
            {
                status = interpreter.RunMain(main);
            }
            catch (Exception e) when (e is not (InterpreterFault or NovaPanic or ExitRequest))
            {
                failure = e;
            }
        }, 1 << 30);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("internal interpreter error: " + failure.Message, failure);
        }
        return status;
    }

    private int RunMain(NirFunction main)
    {
        try
        {
            try
            {
                StartRuntime();
                var result = Call(main, []);
                var code = result is null ? 0 : (int)(uint)result.Bits(0, 4);
                FinishRuntime();
                return code & 0xFF;
            }
            catch (InterpreterFault fault)
            {
                WriteError(Encoding.UTF8.GetBytes($"nova interp: fault: {fault.Message} at {fault.Location ?? Here()}\n"));
                return FaultExitStatus;
            }
            catch (NovaPanic panic)
            {
                WriteError(Encoding.UTF8.GetBytes($"nova panic: {panic.Message} at {panic.Location}\n"));
                return PanicExitStatus;
            }
            catch (ExitRequest exit)
            {
                return exit.Code & 0xFF;
            }
        }
        finally
        {
            _options.StandardOutput.Flush();
            _options.StandardError.Flush();
        }
    }

    private string Here() => _source is null ? "<runtime>" : Location(_source, _span);

    private static string Location(SourceText source, TextSpan span)
    {
        var position = source.GetLinePosition(span.Start);
        return $"{source.Path}:{position.Line}:{position.Column}";
    }

    // ---- Calls and frames (§5.3) ----

    private sealed class Frame(NirFunction function, Allocation[] locals, Analysis.FunctionPlacement? boxes)
    {
        public NirFunction Function { get; } = function;

        public Allocation[] Locals { get; } = locals;

        /// <summary>The function's stack-placed boxes (M5), and the slots made so far.</summary>
        public Analysis.FunctionPlacement? Boxes { get; } = boxes;

        public List<Allocation> Slots { get; } = [];
    }

    private Value? Call(NirFunction function, Value[] arguments)
    {
        var locals = new Allocation[function.Locals.Count];
        for (var i = 0; i < locals.Length; i++)
        {
            locals[i] = NewLocal(function.Locals[i]);
        }
        var frame = new Frame(function, locals, _placement?.For(function));
        for (var i = 0; i < arguments.Length; i++)
        {
            _memory.Store(locals[i].Base, arguments[i]);
        }
        var (savedSource, savedSpan) = (_source, _span);
        try
        {
            var block = function.Blocks[0];
            while (true)
            {
                foreach (var statement in block.Statements)
                {
                    (_source, _span) = (function.Source.Source, statement.Span);
                    Execute(frame, statement);
                }
                var terminator = block.Terminator!;
                (_source, _span) = (function.Source.Source, terminator.Span);
                switch (terminator)
                {
                    case NirGoto g:
                        block = g.Target;
                        break;
                    case NirBranch b:
                        block = Operand(frame, b.Condition).Bits(0, 1) != 0 ? b.Then : b.Else;
                        break;
                    case NirSwitch s:
                    {
                        var value = Integer(Operand(frame, s.Value), s.Value.Type);
                        block = s.Otherwise;
                        foreach (var (caseValue, target) in s.Cases)
                        {
                            if (value == caseValue)
                            {
                                block = target;
                                break;
                            }
                        }
                        break;
                    }
                    case NirReturn r:
                        return r.Value is null ? null : Operand(frame, r.Value);
                    case NirExit exit:
                        _options.StandardOutput.Flush();
                        throw new ExitRequest((int)Integer(Operand(frame, exit.Code), exit.Code.Type));
                    case NirPanic panic:
                        throw new NovaPanic(panic.Kind == NirPanicKind.UnwrapNull ? "unwrapped null" : "unwrapped an Err value",
                            Location(function.Source.Source, terminator.Span));
                    case NirUnreachable:
                        throw new NovaPanic("entered unreachable code", Location(function.Source.Source, function.Source.Syntax.Name.Span));
                    default:
                        throw new InvalidOperationException($"cannot interpret terminator {terminator}");
                }
            }
        }
        catch (InterpreterFault fault) when (fault.Location is null)
        {
            fault.Location = Here();
            throw;
        }
        finally
        {
            foreach (var local in frame.Locals.Concat(frame.Slots))
            {
                if (local.Live)
                {
                    local.End();
                }
            }
            (_source, _span) = (savedSource, savedSpan);
        }
    }

    /// <summary>A local's storage: zero-filled and initialised, as the generated C declares it (§5.2).</summary>
    private Allocation NewLocal(NirLocal local) =>
        _memory.Allocate(AllocationKind.Local, SizeOf(local.Type), initialised: true, local.Name is null ? $"temporary _{local.Id}" : $"local _{local.Id}");

    private static int SizeOf(NovaType type) => TypeTable.LayoutOf(type).Size;

    private static int AlignOf(NovaType type) => TypeTable.LayoutOf(type).Align;

    // ---- Places ----

    /// <summary>
    /// The address of a place. A deref of a slice, vector or stable vector stays at the value itself (its
    /// elements' pointer and length are the value's), as in the generated C (c-backend.md §2).
    /// </summary>
    private ulong Address(Frame frame, NirPlace place, bool write)
    {
        var allocation = frame.Locals[place.Local.Id];
        if (!allocation.Live)
        {
            if (!write)
            {
                throw new InterpreterFault($"use of a dead local (_{place.Local.Id})");
            }
            // A new incarnation: references to the old one stay dead (§5.2).
            allocation = frame.Locals[place.Local.Id] = NewLocal(place.Local);
        }
        var address = allocation.Base;
        var type = place.Local.Type;
        NovaType? stable = null;
        foreach (var projection in place.Projections)
        {
            switch (projection.Kind)
            {
                case ProjectionKind.Field when projection.Field!.IsRegionPart:
                    break; // a scratch frame's borrow-checker part is the frame itself (§19.5)
                case ProjectionKind.Field when type.Struct is { IsImported: true }:
                    // Its layout is the C compiler's, from headers the interpreter does not have (type-system.md §31.4).
                    throw new InterpreterFault($"access to a field of the imported C type '{type}', whose layout only the C compiler knows");
                case ProjectionKind.Field:
                    address += (ulong)projection.Field!.Offset;
                    break;
                case ProjectionKind.Downcast:
                    address += (ulong)TypeTable.EnumLayout(type).UnionOffset;
                    break;
                case ProjectionKind.Deref when type.IsStableVector || type.IsSlice || type.IsVector:
                    break;
                case ProjectionKind.Deref when type.IsRawPointer:
                    address = CheckAligned(Pointer(address), type.Pointee!);
                    break;
                case ProjectionKind.Deref:
                    address = Pointer(address); // a reference, a box, or a pool box's first field
                    break;
                case ProjectionKind.Index:
                {
                    var operand = (NirOperand)projection.Index!;
                    var index = (ulong)Integer(Operand(frame, operand), operand.Type);
                    var size = (ulong)SizeOf(type.Element!);
                    address = stable is not null ? StableVectorAt(address, index, size)
                        : type.IsUnsized ? Pointer(address) + index * size
                        : address + index * size;
                    break;
                }
            }
            stable = projection.IsDeref && type.IsStableVector ? type : null; // the stable vector whose elements `type` is
            type = projection.Apply(type);
        }
        return address;
    }

    private ulong Pointer(ulong address) => _memory.LoadBits(address, 8);

    /// <summary>C function addresses (type-system.md §29.5): far above every allocation's address ((id + 1) &lt;&lt; 32).</summary>
    private const ulong FunctionAddressBase = 0xFFFF_0000_0000_0000;

    private readonly List<HirFunction> _addressed = [];

    private ulong FunctionAddress(HirFunction function)
    {
        var index = _addressed.IndexOf(function);
        if (index < 0)
        {
            index = _addressed.Count;
            _addressed.Add(function);
        }
        return FunctionAddressBase + (ulong)index * 16u;
    }

    /// <summary>A call through a C function pointer: to the exported function, or to the C library model for an extern.</summary>
    private Value? CallThrough(ulong address, Value[] arguments)
    {
        if (address == 0)
        {
            throw new InterpreterFault("call through a null function pointer");
        }
        var index = (address - FunctionAddressBase) / 16u;
        if (address < FunctionAddressBase || (address - FunctionAddressBase) % 16u != 0 || index >= (ulong)_addressed.Count)
        {
            throw new InterpreterFault($"call through an invalid function pointer 0x{address:x}");
        }
        var function = _addressed[(int)index];
        return function.IsExtern ? CallC(function, arguments) : Call(_functions[function], arguments);
    }

    /// <summary>Unsafe code's accesses are checked like any other, and for alignment too (unsafe.md §5).</summary>
    private static ulong CheckAligned(ulong address, NovaType pointee)
    {
        var align = (ulong)AlignOf(pointee);
        if (address != 0 && address % align != 0)
        {
            throw new InterpreterFault($"misaligned access: a '{pointee}' at 0x{address:x} needs alignment {align}");
        }
        return address;
    }

    /// <summary>The address a raw pointer value holds, checked for alignment (null and bounds are checked by the access).</summary>
    private static ulong RawAddress(Value pointer, NovaType pointee) => CheckAligned(pointer.Bits(0, 8), pointee);

    private readonly Dictionary<NirPlace, NovaType> _placeTypes = new(ReferenceEqualityComparer.Instance);

    /// <summary>A place's type, cached per place object (computing it walks the projections).</summary>
    private NovaType PlaceType(NirPlace place)
    {
        if (!_placeTypes.TryGetValue(place, out var type))
        {
            type = _placeTypes[place] = place.Type;
        }
        return type;
    }

    // ---- Operands and rvalues ----

    private Value Operand(Frame frame, NirOperand operand)
    {
        switch (operand)
        {
            case NirPlaceOperand { Place: var place, IsMove: var isMove }:
            {
                var address = Address(frame, place, write: false);
                var size = SizeOf(PlaceType(place));
                var value = _memory.Load(address, size);
                if (isMove)
                {
                    _memory.Uninitialise(address, size); // a move leaves the place uninitialised (§14.3)
                }
                return value;
            }
            case NirConst c:
                return c.Type.Kind == TypeKind.Bool ? Value.Of(c.Value != 0 ? 1u : 0u, 1) : FromInteger(c.Value, c.Type);
            case NirString s:
                return View(Literal(s.Value), (ulong)Encoding.UTF8.GetByteCount(s.Value));
            default:
                throw new InvalidOperationException($"cannot interpret operand {operand}");
        }
    }

    /// <summary>The static bytes of a string literal (one allocation per distinct text).</summary>
    private ulong Literal(string text)
    {
        if (!_literals.TryGetValue(text, out var address))
        {
            address = _literals[text] = _memory.Static(Encoding.UTF8.GetBytes(text), "a string literal").Base;
        }
        return address;
    }

    private static Value View(ulong pointer, ulong length)
    {
        var value = Value.Of(pointer, 8);
        var result = Value.Uninitialised(16);
        result.Put(0, value);
        result.Put(8, Value.Of(length, 8));
        return result;
    }

    private static Int128 Integer(Value value, NovaType type)
    {
        var size = SizeOf(type); // integers, bool, and descriptors (int32_t)
        var bits = value.Bits(0, size);
        if (type.Kind == TypeKind.Integer && type.IsSigned && size < 8 && (bits >> (8 * size - 1) & 1) != 0)
        {
            bits |= ulong.MaxValue << (8 * size);
        }
        return type.Kind == TypeKind.Integer && type.IsSigned ? (long)bits : bits;
    }

    /// <summary>The value modulo 2^bits, as the type's bytes (two's complement).</summary>
    private static Value FromInteger(Int128 value, NovaType type)
    {
        var size = SizeOf(type); // integers, bool, and descriptors (int32_t)
        return Value.Of((ulong)(value & ulong.MaxValue), size);
    }

    private static (Int128 Min, Int128 Max) Range(NovaType type) => type.IsSigned
        ? (-(Int128.One << (type.Bits - 1)), (Int128.One << (type.Bits - 1)) - 1)
        : (Int128.Zero, (Int128.One << type.Bits) - 1);

    /// <summary>
    /// An arithmetic, bitwise or shift operator with the semantics of <see cref="IntegerSemantics.TryEvaluate"/>
    /// (type-system.md §6), in Int128: operands are at most 64 bits wide. Null result: the panic reason.
    /// </summary>
    private static (Int128 Result, string? Panic) Arithmetic(BinaryOp op, Int128 a, Int128 b, NovaType type)
    {
        var (min, max) = Range(type);
        Int128 result;
        switch (op)
        {
            case BinaryOp.Add:
                result = a + b;
                break;
            case BinaryOp.Subtract:
                result = a - b;
                break;
            case BinaryOp.Multiply when !type.IsSigned:
            {
                var product = (UInt128)a * (UInt128)b; // two u64 values: below 2^128
                return product > (UInt128)max ? (0, "integer overflow") : ((Int128)product, null);
            }
            case BinaryOp.Multiply:
                result = a * b; // two i64 values: at most 2^126
                break;
            case BinaryOp.Divide or BinaryOp.Remainder when b == 0:
                return (0, "division by zero");
            case BinaryOp.Divide:
                result = a / b; // truncates toward zero
                break;
            case BinaryOp.Remainder:
                return (a % b, null); // the dividend's sign; MIN % -1 == 0
            case BinaryOp.BitAnd:
                return (a & b, null);
            case BinaryOp.BitOr:
                return (a | b, null);
            case BinaryOp.BitXor:
                return (a ^ b, null);
            case BinaryOp.ShiftLeft or BinaryOp.ShiftRight when b < 0 || b >= type.Bits:
                return (0, "shift amount out of range");
            case BinaryOp.ShiftLeft:
            {
                var shifted = (a << (int)b) & max; // bits shifted out are discarded (left operands are unsigned)
                return (shifted, null);
            }
            case BinaryOp.ShiftRight:
                return (a >> (int)b, null);
            default:
                throw new InvalidOperationException($"operator {op} must have been lowered");
        }
        return result < min || result > max ? (0, "integer overflow") : (result, null);
    }

    private Value? Rvalue(Frame frame, NirRvalue rvalue)
    {
        switch (rvalue)
        {
            case NirUse use:
                return Operand(frame, use.Operand);
            case NirBinary binary:
                return Binary(frame, binary);
            case NirUnary { Op: UnaryOp.Not } not:
                return Value.Of(Operand(frame, not.Operand).Bits(0, 1) == 0 ? 1u : 0u, 1);
            case NirUnary negate:
            {
                var operand = Integer(Operand(frame, negate.Operand), negate.Type);
                if (-operand < Range(negate.Type).Min || -operand > Range(negate.Type).Max)
                {
                    throw new NovaPanic("integer overflow", Location(negate.Source, negate.Span));
                }
                return FromInteger(-operand, negate.Type);
            }
            case NirCast { Operand.Type.IsStringView: true } bytes:
                return Operand(frame, bytes.Operand); // `s.bytes()`: the same pointer and length (§22.2)
            case NirCast cast when cast.Type.Kind is TypeKind.Integer or TypeKind.Bool && cast.Operand.Type.Kind is TypeKind.Integer or TypeKind.Bool:
            {
                var value = Integer(Operand(frame, cast.Operand), cast.Operand.Type);
                return cast.Type.Kind == TypeKind.Bool ? Value.Of(value == 0 ? 0u : 1u, 1) : FromInteger(value, cast.Type);
            }
            case NirCast cast:
                return Operand(frame, cast.Operand); // reference and other same-representation conversions
            case NirTruncate truncate:
                return FromInteger(Integer(Operand(frame, truncate.Operand), truncate.Operand.Type), truncate.Type);
            case NirCall call:
            {
                var arguments = call.Arguments.Select(a => Operand(frame, a)).ToArray();
                return call.Callee.IsExtern ? CallC(call.Callee, arguments) : Call(_functions[call.Callee], arguments);
            }
            case NirAggregate aggregate:
            {
                var fields = aggregate.Type.Struct!.Fields;
                var value = Value.Uninitialised(SizeOf(aggregate.Type));
                if (fields.Count == 0)
                {
                    return Value.Of(0, 1); // `nv_empty`
                }
                for (var i = 0; i < fields.Count; i++)
                {
                    value.Put(fields[i].Offset, Operand(frame, aggregate.Fields[i]));
                }
                return value;
            }
            case NirEnumAggregate enumValue:
                return EnumValue(enumValue.Type, enumValue.Variant, [.. enumValue.Payload.Select(p => Operand(frame, p))]);
            case NirArray array:
            {
                var size = SizeOf(array.Type.Element!);
                var value = Value.Uninitialised(SizeOf(array.Type));
                for (var i = 0; i < array.Elements.Length; i++)
                {
                    value.Put(i * size, Operand(frame, array.Elements[i]));
                }
                return value;
            }
            case NirRef { Place.Type.IsUnsized: true } reborrow:
                // A slice reborrow: the place of `s.*` is the slice `s` itself.
                return _memory.Load(Address(frame, reborrow.Place, write: false), 16);
            case NirRef reference:
                return Value.Of(Address(frame, reference.Place, write: false), 8);
            case NirSlice slice:
            {
                var baseAddress = Address(frame, slice.Base, write: false);
                var start = slice.Base.Type.IsUnsized ? Pointer(baseAddress) : baseAddress;
                var low = (ulong)Integer(Operand(frame, slice.Low), slice.Low.Type);
                var high = (ulong)Integer(Operand(frame, slice.High), slice.High.Type);
                return View(start + low * (ulong)SizeOf(slice.Base.Type.Element!), high - low);
            }
            case NirLength length:
            {
                // `.len`: after the pointer of a slice or vector, after the 32 chunk pointers of a stable vector.
                var container = length.Slice.Projections.IsEmpty || !length.Slice.Projections[^1].IsDeref
                    ? length.Slice.Type
                    : new NirPlace(length.Slice.Local, length.Slice.Projections.RemoveAt(length.Slice.Projections.Length - 1)).Type;
                return _memory.Load(Address(frame, length.Slice, write: false) + (container.IsStableVector ? 256UL : 8UL), 8);
            }
            case NirDiscriminant discriminant:
            {
                var (_, _, tagSize, _) = TypeTable.EnumLayout(discriminant.Place.Type);
                return Value.Of(_memory.LoadBits(Address(frame, discriminant.Place, write: false), tagSize), 4);
            }
            case NirBuiltin builtin:
                return Builtin(frame, builtin);
            case NirFunctionAddress address:
                return Value.Of(FunctionAddress(address.Function), 8);
            default:
                throw new InvalidOperationException($"cannot interpret rvalue {rvalue}");
        }
    }

    /// <summary>An enum value: the tag, then the payloads at their offsets in the union; the rest uninitialised.</summary>
    private static Value EnumValue(NovaType type, EnumVariant variant, Value[] payload)
    {
        var (size, _, tagSize, unionOffset) = TypeTable.EnumLayout(type);
        var value = Value.Uninitialised(size);
        value.Put(0, Value.Of((ulong)variant.Index, tagSize));
        for (var i = 0; i < payload.Length; i++)
        {
            value.Put(unionOffset + variant.Payload[i].Offset, payload[i]);
        }
        return value;
    }

    private Value Binary(Frame frame, NirBinary binary)
    {
        var left = Operand(frame, binary.Left);
        var right = Operand(frame, binary.Right);
        var type = binary.Left.Type;
        bool? equal = null;
        if (type.Kind == TypeKind.Handle)
        {
            equal = left.Bits(0, 8) == right.Bits(0, 8);
        }
        else if (type.IsStringView)
        {
            equal = Bytes(left).AsSpan().SequenceEqual(Bytes(right));
        }
        if (equal is { } isEqual)
        {
            return Value.Of(isEqual == (binary.Op == BinaryOp.Equal) ? 1u : 0u, 1);
        }
        var a = Integer(left, type);
        var b = Integer(right, binary.Right.Type);
        var compared = binary.Op switch
        {
            BinaryOp.Equal => a == b,
            BinaryOp.NotEqual => a != b,
            BinaryOp.Less => a < b,
            BinaryOp.LessEqual => a <= b,
            BinaryOp.Greater => a > b,
            BinaryOp.GreaterEqual => a >= b,
            _ => (bool?)null,
        };
        if (compared is { } truth)
        {
            return Value.Of(truth ? 1u : 0u, 1);
        }
        var (result, panic) = Arithmetic(binary.Op, a, b, type);
        if (panic is not null)
        {
            throw new NovaPanic(panic, Location(binary.Source, binary.Span));
        }
        return FromInteger(result, binary.Type);
    }

    /// <summary>The bytes a slice or string view points to (a use).</summary>
    private byte[] Bytes(Value slice)
    {
        var pointer = slice.Bits(0, 8);
        var length = slice.Bits(8, 8);
        if (length == 0)
        {
            return [];
        }
        var value = _memory.Load(pointer, checked((int)length));
        return value.Data.Length == 0 ? [] : CheckedData(value);
    }

    private static byte[] CheckedData(Value value)
    {
        if (Array.IndexOf(value.Init, false) >= 0)
        {
            throw new InterpreterFault("use of uninitialised memory");
        }
        return value.Data;
    }

    // ---- Statements ----

    private void Execute(Frame frame, NirStatement statement)
    {
        switch (statement)
        {
            case NirAssign { Value: NirBuiltin { Function: BuiltinFunction.ScratchEnter } } enter:
                _memory.Store(Address(frame, enter.Target, write: true), ScratchEnterRoot(Location(frame.Function.Source.Source, enter.Span)));
                break;
            case NirAssign { Value: NirBuiltin { Function: BuiltinFunction.BoxCreate } create } site
                when frame.Boxes is { } boxes && boxes.IsSite(site, out _):
            {
                // A stack-placed box (§5.5a): the value goes into a slot of the frame, uncounted, and cannot fail.
                var value = Operand(frame, create.Arguments[^1]);
                var slot = _memory.Allocate(AllocationKind.Local, value.Size, initialised: false, "a stack-placed box");
                frame.Slots.Add(slot);
                _memory.Store(slot.Base, value);
                _memory.Store(Address(frame, site.Target, write: true), Ok(create.Type, Value.Of(slot.Base, 8)));
                break;
            }
            case NirAssign { Value: NirArrayRepeat repeat } assign:
            {
                var element = Operand(frame, repeat.Value);
                var target = Address(frame, assign.Target, write: true);
                for (var i = 0L; i < repeat.Type.ArrayLength; i++)
                {
                    _memory.Store(target + (ulong)(i * element.Size), element);
                }
                break;
            }
            case NirAssign assign:
            {
                var value = Rvalue(frame, assign.Value)!;
                _memory.Store(Address(frame, assign.Target, write: true), value);
                break;
            }
            case NirUnpack unpack:
            {
                var source = Address(frame, unpack.Source, write: false) + (ulong)TypeTable.EnumLayout(unpack.Source.Type).UnionOffset;
                for (var i = 0; i < unpack.Targets.Length; i++)
                {
                    if (unpack.Targets[i] is { } target)
                    {
                        var field = unpack.Variant.Payload[i];
                        var value = _memory.Load(source + (ulong)field.Offset, SizeOf(field.Type));
                        _memory.Store(Address(frame, new NirPlace(target), write: true), value);
                    }
                }
                break;
            }
            case NirBoxFree free when IsPlacedBox(frame, free.Box):
                if (free.Flag is null || Flag(frame, free.Flag))
                {
                    _memory.AllocationAt(Pointer(Address(frame, free.Box, write: false))).End();
                }
                break;
            case NirBoxFree free:
                if (free.Flag is null || Flag(frame, free.Flag))
                {
                    var box = Address(frame, free.Box, write: false);
                    if (free.Box.Type.IsPoolBox)
                    {
                        PoolRelease(Pointer(box + 8), Pointer(box));
                    }
                    else
                    {
                        FreeBox(Pointer(box), free.Box.Type);
                    }
                }
                break;
            case NirBoundsCheck check:
                if (Integer(Operand(frame, check.Index), check.Index.Type) >= Integer(Operand(frame, check.Length), check.Length.Type))
                {
                    throw new NovaPanic("index out of bounds", Location(frame.Function.Source.Source, check.Span));
                }
                break;
            case NirRangeCheck check:
            {
                var low = Integer(Operand(frame, check.Low), check.Low.Type);
                var high = Integer(Operand(frame, check.High), check.High.Type);
                var here = Location(frame.Function.Source.Source, check.Span);
                if (check.Text is { } text)
                {
                    var bytes = Bytes(_memory.Load(Address(frame, text, write: false), 16));
                    if (low > high || high > bytes.Length)
                    {
                        throw new NovaPanic("slice range out of bounds", here);
                    }
                    bool Boundary(Int128 i) => i >= bytes.Length || (bytes[(int)i] & 0xC0) != 0x80;
                    if (!Boundary(low) || !Boundary(high))
                    {
                        throw new NovaPanic("string slice not on a character boundary", here);
                    }
                }
                else if (low > high || high > Integer(Operand(frame, check.Length), check.Length.Type))
                {
                    throw new NovaPanic("slice range out of bounds", here);
                }
                break;
            }
            case NirStorageDead dead:
                frame.Locals[dead.Local.Id].End();
                break;
            case NirDrop drop:
                if (drop.Flag is null || Flag(frame, drop.Flag))
                {
                    if (_options.DropTrace is { } trace)
                    {
                        var position = frame.Function.Source.Source.GetLinePosition(drop.Span.Start);
                        trace.Add($"{_symbols.GetName(frame.Function.Name)} {position} {drop.Place.Type}");
                    }
                    var address = Address(frame, drop.Place, write: false);
                    if (IsPlacedBox(frame, drop.Place))
                    {
                        // A stack-placed box: destroy the contents, then end the slot instead of freeing (§5.5a).
                        var contents = Pointer(address);
                        if (TypeTable.NeedsDrop(drop.Place.Type.Pointee!))
                        {
                            Destroy(drop.Place.Type.Pointee!, contents);
                        }
                        _memory.AllocationAt(contents).End();
                    }
                    else
                    {
                        Destroy(drop.Place.Type, address);
                    }
                }
                break;
            case NirEval { Value: var value }:
                _ = Rvalue(frame, value);
                break;
            default:
                throw new InvalidOperationException($"cannot interpret statement {statement}");
        }
    }

    private static bool IsPlacedBox(Frame frame, NirPlace place) =>
        place.IsLocal && frame.Boxes is { } boxes && boxes.BoxLocals.Contains(place.Local);

    private bool Flag(Frame frame, NirLocal flag) => _memory.LoadBits(Address(frame, new NirPlace(flag), write: false), 1) != 0;

    /// <summary>A Nova panic: the message, then abort (§5.1).</summary>
    private sealed class NovaPanic(string reason, string location) : Exception(reason)
    {
        public string Location { get; } = location;
    }

    private sealed class ExitRequest(int code) : Exception("Process.exit")
    {
        public int Code { get; } = code;
    }
}
