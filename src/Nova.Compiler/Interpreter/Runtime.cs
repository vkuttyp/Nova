using System.Numerics;
using System.Text;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;

namespace Nova.Compiler.Interpreter;

// The runtime (intermediate-representation.md §5.1): nova_rt_core.c and the emitter's helpers
// (c-backend.md §2), step for step, on the interpreter's memory. Comments name the C they follow.
public sealed partial class NirInterpreter
{
    private const uint AllocSystem = 0;
    private const uint AllocFailing = 1;
    private const ulong ArenaHeader = 16;
    private const ulong ScratchChunk = 65536;
    private const int StableVectorChunks = 32;

    private readonly ulong _emptyVector; // nova_vec_empty: never dereferenced

    // nova_rt_core.c: allocation accounting and fault injection.
    private ulong _attempts;
    private ulong _live;
    private bool _failingArmed;
    private ulong _failingLeft;

    // nova_rt_core.c: the scratch stack.
    private ulong _scratchChunk;
    private ulong _scratchCur;
    private ulong _scratchEnd;
    private ulong _scratchDepth;

    private void StartRuntime()
    {
        StartArguments();
    }

    /// <summary>nova_rt_finish: the NOVA_ALLOC_REPORT line.</summary>
    private void FinishRuntime()
    {
        if (_options.AllocationReport)
        {
            WriteError(Encoding.ASCII.GetBytes($"nova alloc: attempts={_attempts} live={_live}\n"));
        }
    }

    private void WriteOutput(ReadOnlySpan<byte> bytes) => _options.StandardOutput.Write(bytes);

    /// <summary>nova_platform_write_stderr: standard output is flushed first.</summary>
    private void WriteError(ReadOnlySpan<byte> bytes)
    {
        _options.StandardOutput.Flush();
        _options.StandardError.Write(bytes);
    }

    // ---- Heap ----

    private static uint AllocatorId(NovaType allocator) => allocator.Name == "FailingAllocator" ? AllocFailing : AllocSystem;

    /// <summary>nova_alloc: 0 on failure.</summary>
    private ulong Alloc(ulong size, uint allocator)
    {
        _attempts++;
        if (allocator == AllocFailing && _failingArmed)
        {
            if (_failingLeft == 0)
            {
                return 0;
            }
            _failingLeft--;
        }
        if (_options.FailAllocationAt != 0 && _attempts == _options.FailAllocationAt)
        {
            return 0;
        }
        if (size > int.MaxValue)
        {
            return 0; // malloc would fail
        }
        _live++;
        return _memory.Allocate(AllocationKind.Heap, size == 0 ? 1 : (int)size, initialised: false, "a heap allocation").Base;
    }

    private void Free(ulong pointer, uint allocator)
    {
        _ = allocator;
        _live--;
        _memory.Free(pointer);
    }

    /// <summary>nova_vec_grow: the buffer with room for <paramref name="extra"/> more elements, or 0 with nothing changed.</summary>
    private ulong VectorGrow(ulong pointer, ulong length, ulong capacityAddress, ulong extra, ulong size, uint allocator)
    {
        if (extra > ulong.MaxValue - length)
        {
            return 0;
        }
        var need = length + extra;
        var capacity = _memory.LoadBits(capacityAddress, 8);
        if (need <= capacity)
        {
            return pointer;
        }
        var next = capacity > ulong.MaxValue / 2 ? ulong.MaxValue : capacity * 2;
        next = Math.Max(Math.Max(next, 4), need);
        if (size == 0 || next > ulong.MaxValue / size)
        {
            return 0;
        }
        var fresh = Alloc(next * size, allocator);
        if (fresh == 0)
        {
            return 0;
        }
        if (length * size > 0)
        {
            _memory.Copy(fresh, pointer, checked((int)(length * size)));
        }
        if (capacity != 0)
        {
            Free(pointer, allocator);
        }
        _memory.StoreBits(capacityAddress, next, 8);
        return fresh;
    }

    private static int ChunkOf(ulong j) => 60 - BitOperations.LeadingZeroCount(j);

    /// <summary>nova_svec_at.</summary>
    private ulong StableVectorAt(ulong vector, ulong index, ulong size)
    {
        var j = index + 8;
        var k = ChunkOf(j);
        return Pointer(vector + (ulong)(8 * k)) + (j - (8UL << k)) * size;
    }

    /// <summary>nova_svec_slot: the slot for element <paramref name="length"/>; 0 if full or out of memory.</summary>
    private ulong StableVectorSlot(ulong chunks, ulong length, ulong size, uint allocator)
    {
        if (length > ulong.MaxValue - 8)
        {
            return 0;
        }
        var j = length + 8;
        var k = ChunkOf(j);
        if (k >= StableVectorChunks)
        {
            return 0;
        }
        var chunkAddress = chunks + (ulong)(8 * k);
        if (Pointer(chunkAddress) == 0)
        {
            var count = 8UL << k;
            if (size == 0 || count > ulong.MaxValue / size)
            {
                return 0;
            }
            var chunk = Alloc(count * size, allocator);
            if (chunk == 0)
            {
                return 0;
            }
            _memory.StoreBits(chunkAddress, chunk, 8);
        }
        return Pointer(chunkAddress) + (j - (8UL << k)) * size;
    }

    // ---- Regions (nova_rt_core.c) ----

    private static ulong AlignUp(ulong address, ulong align) => (address + (align - 1)) & ~(align - 1);

    /// <summary>nova_arena_alloc on the nv_arena at <paramref name="arena"/> (chunks, cur, end, chunk_size, allocator).</summary>
    private ulong ArenaAlloc(ulong arena, ulong size, ulong align)
    {
        var cur = Pointer(arena + 8);
        var end = Pointer(arena + 16);
        var aligned = AlignUp(cur, align);
        if (cur != 0 && aligned >= cur && size <= end - aligned)
        {
            _memory.StoreBits(arena + 8, aligned + size, 8);
            return aligned;
        }
        var need = size + ArenaHeader;
        if (need < size)
        {
            return 0;
        }
        var total = Math.Max(need, _memory.LoadBits(arena + 24, 8));
        var chunk = Alloc(total, (uint)_memory.LoadBits(arena + 32, 8));
        if (chunk == 0)
        {
            return 0;
        }
        _memory.StoreBits(chunk, Pointer(arena), 8); // the previous chunk's address
        _memory.StoreBits(arena, chunk, 8);
        _memory.StoreBits(arena + 16, chunk + total, 8);
        var data = chunk + ArenaHeader;
        _memory.StoreBits(arena + 8, data + size, 8);
        return data;
    }

    /// <summary>nova_arena_reset (also its drop).</summary>
    private void ArenaReset(ulong arena)
    {
        var chunk = Pointer(arena);
        var allocator = (uint)_memory.LoadBits(arena + 32, 8);
        while (chunk != 0)
        {
            var previous = Pointer(chunk);
            Free(chunk, allocator);
            chunk = previous;
        }
        _memory.StoreBits(arena, 0, 8);
        _memory.StoreBits(arena + 8, 0, 8);
        _memory.StoreBits(arena + 16, 0, 8);
    }

    /// <summary>fba_alloc on the nv_fba at <paramref name="fba"/> (base, capacity, used).</summary>
    private ulong FixedBufferAlloc(ulong fba, ulong size, ulong align)
    {
        var start = Pointer(fba) + _memory.LoadBits(fba + 16, 8);
        var aligned = AlignUp(start, align);
        var offset = aligned - Pointer(fba);
        var capacity = _memory.LoadBits(fba + 8, 8);
        if (aligned < start || offset > capacity || size > capacity - offset)
        {
            return 0;
        }
        _memory.StoreBits(fba + 16, offset + size, 8);
        return aligned;
    }

    /// <summary>nova_scratch_enter_root: an nv_scratch (chunk, cur, end, depth).</summary>
    private Value ScratchEnterRoot(string location)
    {
        if (_scratchDepth != 0)
        {
            throw new NovaPanic("a scratch frame is already active; nest with frame.enter()", location);
        }
        _scratchDepth = 1;
        return ScratchFrame(1);
    }

    private Value ScratchFrame(ulong depth)
    {
        var frame = Value.Uninitialised(32);
        frame.Put(0, Value.Of(_scratchChunk, 8));
        frame.Put(8, Value.Of(_scratchCur, 8));
        frame.Put(16, Value.Of(_scratchEnd, 8));
        frame.Put(24, Value.Of(depth, 8));
        return frame;
    }

    /// <summary>nova_scratch_drop.</summary>
    private void ScratchDrop(ulong frame)
    {
        var entry = Pointer(frame);
        while (_scratchChunk != entry)
        {
            var previous = Pointer(_scratchChunk);
            Free(_scratchChunk, AllocSystem);
            _scratchChunk = previous;
        }
        _scratchCur = Pointer(frame + 8);
        _scratchEnd = Pointer(frame + 16);
        _scratchDepth = _memory.LoadBits(frame + 24, 8) - 1;
    }

    private ulong ScratchAlloc(ulong size, ulong align)
    {
        var aligned = AlignUp(_scratchCur, align);
        if (_scratchCur != 0 && aligned >= _scratchCur && size <= _scratchEnd - aligned)
        {
            _scratchCur = aligned + size;
            return aligned;
        }
        var need = size + ArenaHeader;
        if (need < size)
        {
            return 0;
        }
        var total = Math.Max(need, ScratchChunk);
        var chunk = Alloc(total, AllocSystem);
        if (chunk == 0)
        {
            return 0;
        }
        _memory.StoreBits(chunk, _scratchChunk, 8);
        _scratchChunk = chunk;
        _scratchEnd = chunk + total;
        _scratchCur = chunk + ArenaHeader + size;
        return chunk + ArenaHeader;
    }

    /// <summary>nova_region_alloc, dispatching on the region a reference names.</summary>
    private ulong RegionAlloc(ulong region, NovaType regionReference, ulong size, ulong align) =>
        (regionReference.Pointee ?? regionReference).Region switch
        {
            RegionKind.Arena => ArenaAlloc(region, size, align),
            RegionKind.FixedBuffer => FixedBufferAlloc(region, size, align),
            _ => ScratchAlloc(size, align),
        };

    /// <summary>nova_pool_take on the nv_pool at <paramref name="pool"/> (slots, free, capacity, used, slot_size, allocator).</summary>
    private ulong PoolTake(ulong pool, ulong size)
    {
        if (Pointer(pool) == 0)
        {
            var slot = Math.Max(size, 8UL);
            slot = (slot + 15) & ~15UL;
            var capacity = _memory.LoadBits(pool + 16, 8);
            if (capacity == 0 || slot > ulong.MaxValue / capacity)
            {
                return 0;
            }
            var slots = Alloc(slot * capacity, (uint)_memory.LoadBits(pool + 40, 8));
            if (slots == 0)
            {
                return 0;
            }
            _memory.StoreBits(pool, slots, 8);
            _memory.StoreBits(pool + 32, slot, 8);
        }
        var free = Pointer(pool + 8);
        if (free != 0)
        {
            _memory.StoreBits(pool + 8, Pointer(free), 8);
            return free;
        }
        var used = _memory.LoadBits(pool + 24, 8);
        if (used == _memory.LoadBits(pool + 16, 8))
        {
            return 0;
        }
        _memory.StoreBits(pool + 24, used + 1, 8);
        return Pointer(pool) + _memory.LoadBits(pool + 32, 8) * used;
    }

    private void PoolRelease(ulong pool, ulong slot)
    {
        _memory.StoreBits(slot, Pointer(pool + 8), 8);
        _memory.StoreBits(pool + 8, slot, 8);
    }

    // ---- Results and options ----

    private static Value Ok(NovaType result, Value? payload) =>
        EnumValue(result, result.Enum!.Variants[0], payload is null ? [] : [payload]);

    private static Value Err(NovaType result, Value payload) => EnumValue(result, result.Enum!.Variants[1], [payload]);

    /// <summary><c>Err(AllocError.OutOfMemory)</c> (tag 0).</summary>
    private static Value OutOfMemory(NovaType result)
    {
        var error = result.Enum!.ResultTypes!.Value.Err;
        return Err(result, EnumValue(error, error.Enum!.Variants[0], []));
    }

    private static Value None(NovaType option) => EnumValue(option, option.Enum!.Variants[0], []);

    private static Value Some(NovaType option, Value value) => EnumValue(option, option.Enum!.Variants[1], [value]);

    /// <summary>Destroys a value that is not in memory (a parameter of a C helper that drops it).</summary>
    private void DestroyValue(NovaType type, Value value)
    {
        if (!TypeTable.NeedsDrop(type))
        {
            return;
        }
        var temporary = _memory.Allocate(AllocationKind.Local, value.Size, initialised: true, "a helper's parameter");
        _memory.Store(temporary.Base, value);
        Destroy(type, temporary.Base);
        temporary.End();
    }

    // ---- Drop glue (c-backend.md §2: the order of the emitted nvD_ functions) ----

    private void Destroy(NovaType type, ulong address)
    {
        switch (type)
        {
            case { Kind: TypeKind.Resource }:
                CloseDescriptor((int)(uint)_memory.LoadBits(address, 4));
                return;
            case { Kind: TypeKind.Region }:
                switch (type.Region)
                {
                    case RegionKind.Arena:
                        ArenaReset(address);
                        break;
                    case RegionKind.Pool:
                        if (Pointer(address) != 0)
                        {
                            Free(Pointer(address), (uint)_memory.LoadBits(address + 40, 8));
                        }
                        break;
                    case RegionKind.ScratchFrame:
                        ScratchDrop(address);
                        break;
                }
                return;
            case { IsPoolBox: true }:
                if (TypeTable.NeedsDrop(type.Pointee!))
                {
                    Destroy(type.Pointee!, Pointer(address));
                }
                PoolRelease(Pointer(address + 8), Pointer(address));
                return;
            case { IsBox: true } when LeadsTo(type.Pointee!, type):
            {
                // Tail iteration (type-system.md §18.5).
                var allocator = AllocatorId(type.Allocator!);
                var node = Pointer(address);
                while (node != 0)
                {
                    var next = DestroyExceptTail(type.Pointee!, node, type);
                    Free(node, allocator);
                    node = next;
                }
                return;
            }
            case { IsStableVector: true }:
            {
                var element = type.Pointee!.Element!;
                if (TypeTable.NeedsDrop(element))
                {
                    var length = _memory.LoadBits(address + 256, 8);
                    for (ulong i = 0; i < length; i++)
                    {
                        Destroy(element, StableVectorAt(address, i, (ulong)SizeOf(element)));
                    }
                }
                var allocator = AllocatorId(type.Allocator!);
                for (var k = 0; k < StableVectorChunks; k++)
                {
                    if (Pointer(address + (ulong)(8 * k)) is var chunk and not 0)
                    {
                        Free(chunk, allocator);
                    }
                }
                return;
            }
            case { Kind: TypeKind.SlotMap }:
            {
                var (slotSize, valueOffset) = SlotLayout(type.Element!);
                if (TypeTable.NeedsDrop(type.Element!))
                {
                    var length = _memory.LoadBits(address + 8, 8);
                    for (ulong i = 0; i < length; i++)
                    {
                        var slot = Pointer(address) + i * slotSize;
                        if ((_memory.LoadBits(slot, 4) & 1) != 0)
                        {
                            Destroy(type.Element!, slot + valueOffset);
                        }
                    }
                }
                if (_memory.LoadBits(address + 16, 8) != 0)
                {
                    Free(Pointer(address), AllocatorId(type.Allocator!));
                }
                return;
            }
            case { IsVector: true }:
            {
                var element = type.Pointee!.Element!;
                if (TypeTable.NeedsDrop(element))
                {
                    var length = _memory.LoadBits(address + 8, 8);
                    for (ulong i = 0; i < length; i++)
                    {
                        Destroy(element, Pointer(address) + i * (ulong)SizeOf(element));
                    }
                }
                if (_memory.LoadBits(address + 16, 8) != 0)
                {
                    Free(Pointer(address), AllocatorId(type.Allocator!));
                }
                return;
            }
            case { IsBox: true }:
                if (TypeTable.NeedsDrop(type.Pointee!))
                {
                    Destroy(type.Pointee!, Pointer(address));
                }
                Free(Pointer(address), AllocatorId(type.Allocator!));
                return;
            case { Enum: { } definition }:
            {
                var (_, _, tagSize, unionOffset) = TypeTable.EnumLayout(type);
                var variant = definition.Variants[(int)_memory.LoadBits(address, tagSize)];
                foreach (var field in Droppable(variant, variant.Payload))
                {
                    Destroy(field.Type, address + (ulong)(unionOffset + field.Offset));
                }
                return;
            }
            case { Struct: { } structure }:
                if (structure.DropFunction is { } drop)
                {
                    Call(_functions[drop], [Value.Of(address, 8)]);
                }
                foreach (var field in Droppable(structure, structure.Fields))
                {
                    Destroy(field.Type, address + (ulong)field.Offset);
                }
                return;
            default:
                throw new InvalidOperationException($"no drop glue for '{type}'");
        }
    }

    /// <summary>The emitter's LeadsTo: does destroying <paramref name="type"/> end in destroying <paramref name="box"/>?</summary>
    private bool LeadsTo(NovaType type, NovaType box)
    {
        if (!_leadsTo.TryGetValue((type, box), out var result))
        {
            result = _leadsTo[(type, box)] = ComputeLeadsTo(type, box);
        }
        return result;
    }

    private readonly Dictionary<(NovaType, NovaType), bool> _leadsTo = [];
    private readonly Dictionary<object, List<StructField>> _droppable = new(ReferenceEqualityComparer.Instance);

    /// <summary>The fields (or payloads) that need drop, in order; cached per struct or variant.</summary>
    private List<StructField> Droppable(object owner, IEnumerable<StructField> fields)
    {
        if (!_droppable.TryGetValue(owner, out var list))
        {
            list = _droppable[owner] = [.. fields.Where(f => TypeTable.NeedsDrop(f.Type))];
        }
        return list;
    }

    private static bool ComputeLeadsTo(NovaType type, NovaType box)
    {
        if (type == box)
        {
            return true;
        }
        if (type.Struct is { } definition)
        {
            return definition.Fields.LastOrDefault(f => TypeTable.NeedsDrop(f.Type)) is { } last && ComputeLeadsTo(last.Type, box);
        }
        if (type.Enum is { } enumDefinition)
        {
            return enumDefinition.Variants.Any(v => v.Payload.LastOrDefault(f => TypeTable.NeedsDrop(f.Type)) is { } last && ComputeLeadsTo(last.Type, box));
        }
        return false;
    }

    /// <summary>The emitter's DestroyExceptTail: destroys the value but its tail box, whose pointer it returns (0 if none).</summary>
    private ulong DestroyExceptTail(NovaType type, ulong address, NovaType box)
    {
        if (type == box)
        {
            return Pointer(address);
        }
        ulong Component(ulong at, NovaType componentType, bool isTail)
        {
            if (isTail && LeadsTo(componentType, box))
            {
                return DestroyExceptTail(componentType, at, box);
            }
            Destroy(componentType, at);
            return 0;
        }
        if (type.Struct is { } definition)
        {
            if (definition.DropFunction is { } drop)
            {
                Call(_functions[drop], [Value.Of(address, 8)]);
            }
            var droppable = Droppable(definition, definition.Fields);
            ulong next = 0;
            foreach (var field in droppable)
            {
                var tail = Component(address + (ulong)field.Offset, field.Type, field == droppable[^1]);
                if (field == droppable[^1])
                {
                    next = tail;
                }
            }
            return next;
        }
        var (_, _, tagSize, unionOffset) = TypeTable.EnumLayout(type);
        var variant = type.Enum!.Variants[(int)_memory.LoadBits(address, tagSize)];
        var fields = Droppable(variant, variant.Payload);
        ulong result = 0;
        foreach (var field in fields)
        {
            var tail = Component(address + (ulong)(unionOffset + field.Offset), field.Type, field == fields[^1]);
            if (field == fields[^1])
            {
                result = tail;
            }
        }
        return result;
    }

    /// <summary>A slot map's slot: <c>{ uint32_t gen; uint32_t next; T value; }</c> by C's rules.</summary>
    private static (ulong Size, ulong ValueOffset) SlotLayout(NovaType value)
    {
        var (size, align) = TypeTable.LayoutOf(value);
        var offset = AlignUp(8, (ulong)align);
        return (AlignUp(offset + (ulong)size, (ulong)Math.Max(4, align)), offset);
    }

    // ---- Built-ins ----

    private Value? Builtin(Frame frame, NirBuiltin builtin)
    {
        var arguments = builtin.Arguments;
        Value Argument(int i) => Operand(frame, arguments[i]);
        switch (builtin.Function)
        {
            case BuiltinFunction.ConsoleWrite or BuiltinFunction.ConsoleWriteLine:
                foreach (var argument in arguments)
                {
                    if (argument is NirString s)
                    {
                        WriteOutput(Encoding.UTF8.GetBytes(s.Value));
                        continue;
                    }
                    var value = Operand(frame, argument);
                    if (argument.Type.Kind == TypeKind.Bool)
                    {
                        WriteOutput(value.Bits(0, 1) != 0 ? "true"u8 : "false"u8);
                    }
                    else if (argument.Type.IsStringView)
                    {
                        WriteOutput(Bytes(value));
                    }
                    else
                    {
                        WriteOutput(Encoding.ASCII.GetBytes(Integer(value, argument.Type).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
                if (builtin.Function == BuiltinFunction.ConsoleWriteLine)
                {
                    WriteOutput("\n"u8);
                }
                return null;
            case BuiltinFunction.ConsoleWriteBytes:
                WriteOutput(Bytes(Argument(0)));
                return null;
            case BuiltinFunction.ConsoleWriteError or BuiltinFunction.ConsoleWriteErrorLine:
                foreach (var argument in arguments)
                {
                    WriteError(Bytes(Operand(frame, argument)));
                }
                if (builtin.Function == BuiltinFunction.ConsoleWriteErrorLine)
                {
                    WriteError("\n"u8);
                }
                return null;
            case BuiltinFunction.FailAfter:
                _failingArmed = true;
                _failingLeft = (ulong)Integer(Argument(0), arguments[0].Type);
                return null;
            case BuiltinFunction.FailReset:
                _failingArmed = false;
                return null;
            case BuiltinFunction.BoxCreate:
            {
                var value = Operand(frame, arguments[^1]);
                var box = builtin.Type.Enum!.ResultTypes!.Value.Ok;
                var pointer = Alloc((ulong)SizeOf(box.Pointee!), AllocatorId(box.Allocator!));
                if (pointer == 0)
                {
                    DestroyValue(box.Pointee!, value);
                    return OutOfMemory(builtin.Type);
                }
                _memory.Store(pointer, value);
                return Ok(builtin.Type, Value.Of(pointer, 8));
            }
            case BuiltinFunction.ArenaCreate:
            {
                var arena = Value.Uninitialised(40);
                arena.Put(0, Value.Of(0, 24)); // chunks, cur, end
                arena.Put(24, Value.Of((ulong)Integer(Argument(1), arguments[1].Type), 8));
                arena.Put(32, Value.Of(AllocatorId(arguments[0].Type), 8));
                return arena;
            }
            case BuiltinFunction.ArenaAlloc:
            {
                var region = Argument(0).Bits(0, 8);
                var value = Argument(1);
                var contents = arguments[1].Type;
                var pointer = RegionAlloc(region, arguments[0].Type, (ulong)SizeOf(contents), (ulong)AlignOf(contents));
                if (pointer == 0)
                {
                    return OutOfMemory(builtin.Type);
                }
                _memory.Store(pointer, value);
                return Ok(builtin.Type, Value.Of(pointer, 8));
            }
            case BuiltinFunction.ArenaAllocArray:
            {
                var region = Argument(0).Bits(0, 8);
                var count = (ulong)Integer(Argument(1), arguments[1].Type);
                var fill = Argument(2);
                var element = arguments[2].Type;
                var size = (ulong)SizeOf(element);
                if (size > 1 && count > ulong.MaxValue / size)
                {
                    return OutOfMemory(builtin.Type);
                }
                var pointer = RegionAlloc(region, arguments[0].Type, count == 0 ? 1 : count * size, (ulong)AlignOf(element));
                if (pointer == 0)
                {
                    return OutOfMemory(builtin.Type);
                }
                for (ulong i = 0; i < count; i++)
                {
                    _memory.Store(pointer + i * size, fill);
                }
                return Ok(builtin.Type, View(pointer, count));
            }
            case BuiltinFunction.ArenaReset:
                ArenaReset(Argument(0).Bits(0, 8));
                return null;
            case BuiltinFunction.FixedBufferOver:
            {
                var storage = Argument(0);
                var fba = Value.Uninitialised(24);
                fba.Put(0, storage); // base, capacity
                fba.Put(16, Value.Of(0, 8));
                return fba;
            }
            case BuiltinFunction.ScratchNest:
            {
                var parent = Argument(0).Bits(0, 8);
                _scratchDepth = _memory.LoadBits(parent + 24, 8) + 1;
                return ScratchFrame(_scratchDepth);
            }
            case BuiltinFunction.PoolCreate:
            {
                var pool = Value.Of(0, 48);
                pool.Put(16, Value.Of((ulong)Integer(Argument(1), arguments[1].Type), 8));
                pool.Put(40, Value.Of(AllocatorId(arguments[0].Type), 8));
                return pool;
            }
            case BuiltinFunction.PoolTake:
            {
                var pool = Argument(0).Bits(0, 8);
                var value = Argument(1);
                var contents = arguments[1].Type;
                var slot = PoolTake(pool, (ulong)SizeOf(contents));
                if (slot == 0)
                {
                    DestroyValue(contents, value);
                    return OutOfMemory(builtin.Type);
                }
                _memory.Store(slot, value);
                var box = Value.Uninitialised(16);
                box.Put(0, Value.Of(slot, 8));
                box.Put(8, Value.Of(pool, 8));
                return Ok(builtin.Type, box);
            }
            case BuiltinFunction.VectorCreate when builtin.Type.IsStableVector:
                return Value.Of(0, 264);
            case BuiltinFunction.VectorCreate when builtin.Type.Kind == TypeKind.SlotMap:
            {
                var map = Value.Of(0, 40);
                map.Put(0, Value.Of(_emptyVector, 8));
                map.Put(32, Value.Of(uint.MaxValue, 4));
                return map;
            }
            case BuiltinFunction.VectorCreate:
                return Vector(_emptyVector, 0, 0);
            case BuiltinFunction.VectorWithCapacity:
            {
                var vectorType = builtin.Type.Enum!.ResultTypes!.Value.Ok;
                var temporary = _memory.Allocate(AllocationKind.Local, 24, initialised: true, "a vector being created");
                _memory.Store(temporary.Base, Vector(_emptyVector, 0, 0));
                var pointer = VectorGrow(_emptyVector, 0, temporary.Base + 16, (ulong)Integer(Argument(0), arguments[0].Type),
                    (ulong)SizeOf(vectorType.Pointee!.Element!), AllocatorId(vectorType.Allocator!));
                var capacity = _memory.LoadBits(temporary.Base + 16, 8);
                temporary.End();
                return pointer == 0 ? OutOfMemory(builtin.Type) : Ok(builtin.Type, Vector(pointer, 0, capacity));
            }
            case BuiltinFunction.VectorPush:
            {
                var vector = Argument(0).Bits(0, 8);
                var value = Argument(1);
                var vectorType = arguments[0].Type.Pointee!;
                var size = (ulong)SizeOf(vectorType.Pointee!.Element!);
                var length = _memory.LoadBits(vector + 8, 8);
                var pointer = VectorGrow(Pointer(vector), length, vector + 16, 1, size, AllocatorId(vectorType.Allocator!));
                if (pointer == 0)
                {
                    DestroyValue(vectorType.Pointee!.Element!, value);
                    return OutOfMemory(builtin.Type);
                }
                _memory.StoreBits(vector, pointer, 8);
                _memory.Store(pointer + length * size, value);
                _memory.StoreBits(vector + 8, length + 1, 8);
                return Ok(builtin.Type, null);
            }
            case BuiltinFunction.StringAppend:
            {
                var text = Bytes(Argument(1));
                var target = Argument(0).Bits(0, 8);
                var stringType = arguments[0].Type.Pointee!;
                var length = _memory.LoadBits(target + 8, 8);
                var pointer = VectorGrow(Pointer(target), length, target + 16, (ulong)text.Length, 1, AllocatorId(stringType.Allocator!));
                if (pointer == 0)
                {
                    return OutOfMemory(builtin.Type);
                }
                _memory.StoreBits(target, pointer, 8);
                if (text.Length > 0)
                {
                    _memory.Store(pointer + length, Value.Initialised(text));
                }
                _memory.StoreBits(target + 8, length + (ulong)text.Length, 8);
                return Ok(builtin.Type, null);
            }
            case BuiltinFunction.VectorPop:
            {
                var vector = Argument(0).Bits(0, 8);
                var element = arguments[0].Type.Pointee!.Pointee!.Element!;
                var length = _memory.LoadBits(vector + 8, 8);
                if (length == 0)
                {
                    return None(builtin.Type);
                }
                _memory.StoreBits(vector + 8, length - 1, 8);
                var size = SizeOf(element);
                return Some(builtin.Type, _memory.Load(Pointer(vector) + (length - 1) * (ulong)size, size));
            }
            case BuiltinFunction.VectorClear:
            {
                var vector = Argument(0).Bits(0, 8);
                var element = arguments[0].Type.Pointee!.Pointee!.Element!;
                var length = _memory.LoadBits(vector + 8, 8);
                _memory.StoreBits(vector + 8, 0, 8);
                if (TypeTable.NeedsDrop(element))
                {
                    for (ulong i = 0; i < length; i++)
                    {
                        Destroy(element, Pointer(vector) + i * (ulong)SizeOf(element));
                    }
                }
                return null;
            }
            case BuiltinFunction.VectorCapacity:
                return _memory.Load(Argument(0).Bits(0, 8) + 16, 8);
            case BuiltinFunction.StableVectorPush:
            {
                var vector = Argument(0).Bits(0, 8);
                var value = Argument(1);
                var vectorType = arguments[0].Type.Pointee!;
                var element = vectorType.Pointee!.Element!;
                var length = _memory.LoadBits(vector + 256, 8);
                var slot = StableVectorSlot(vector, length, (ulong)SizeOf(element), AllocatorId(vectorType.Allocator!));
                if (slot == 0)
                {
                    DestroyValue(element, value);
                    return OutOfMemory(builtin.Type);
                }
                _memory.Store(slot, value);
                _memory.StoreBits(vector + 256, length + 1, 8);
                return Ok(builtin.Type, Value.Of(length, 8));
            }
            case BuiltinFunction.SlotMapInsert:
                return SlotMapInsert(arguments[0].Type.Pointee!, Argument(0).Bits(0, 8), Argument(1), builtin.Type);
            case BuiltinFunction.SlotMapGet:
            {
                var mapType = arguments[0].Type.Pointee!;
                var map = Argument(0).Bits(0, 8);
                var handle = Argument(1);
                if (!SlotMapValid(mapType, map, handle))
                {
                    return None(builtin.Type);
                }
                var (slotSize, valueOffset) = SlotLayout(mapType.Element!);
                return Some(builtin.Type, Value.Of(Pointer(map) + handle.Bits(0, 4) * slotSize + valueOffset, 8));
            }
            case BuiltinFunction.SlotMapRemove:
            {
                var mapType = arguments[0].Type.Pointee!;
                var map = Argument(0).Bits(0, 8);
                var handle = Argument(1);
                if (!SlotMapValid(mapType, map, handle))
                {
                    return None(builtin.Type);
                }
                var (slotSize, valueOffset) = SlotLayout(mapType.Element!);
                var index = handle.Bits(0, 4);
                var slot = Pointer(map) + index * slotSize;
                var generation = (uint)_memory.LoadBits(slot, 4) + 1;
                _memory.StoreBits(slot, generation, 4);
                _memory.StoreBits(map + 24, _memory.LoadBits(map + 24, 8) - 1, 8);
                if (generation != uint.MaxValue - 1)
                {
                    _memory.StoreBits(slot + 4, _memory.LoadBits(map + 32, 4), 4);
                    _memory.StoreBits(map + 32, index, 4);
                }
                return Some(builtin.Type, _memory.Load(slot + valueOffset, SizeOf(mapType.Element!)));
            }
            case BuiltinFunction.SlotMapContains:
                return Value.Of(SlotMapValid(arguments[0].Type.Pointee!, Argument(0).Bits(0, 8), Argument(1)) ? 1u : 0u, 1);
            case BuiltinFunction.SlotMapLength:
                return _memory.Load(Argument(0).Bits(0, 8) + 24, 8);
            case BuiltinFunction.PtrAdd or BuiltinFunction.PtrSub or BuiltinFunction.PtrOffset:
            {
                // Address arithmetic modulo 2^64 (type-system.md §27.3); only a later access is checked.
                var step = unchecked((ulong)Integer(Argument(1), arguments[1].Type) * (ulong)SizeOf(arguments[0].Type.Pointee!));
                var address = Argument(0).Bits(0, 8);
                return Value.Of(builtin.Function == BuiltinFunction.PtrSub ? unchecked(address - step) : unchecked(address + step), 8);
            }
            case BuiltinFunction.PtrIsNull:
                return Value.Of(Argument(0).Bits(0, 8) == 0 ? 1u : 0u, 1);
            case BuiltinFunction.SlicePtr:
                return Value.Of(Argument(0).Bits(0, 8), 8);
            case BuiltinFunction.SliceFromRaw:
                return View(Argument(0).Bits(0, 8), (ulong)Integer(Argument(1), arguments[1].Type));
            case BuiltinFunction.PtrReadVolatile:
            {
                // The interpreter performs every access once, in order: volatile adds nothing to check (§29.1).
                var pointee = arguments[0].Type.Pointee!;
                return _memory.Load(RawAddress(Argument(0), pointee), SizeOf(pointee));
            }
            case BuiltinFunction.PtrWriteVolatile:
                _memory.Store(RawAddress(Argument(0), arguments[0].Type.Pointee!), Argument(1));
                return null;
            case BuiltinFunction.SpinHint:
                return null;
            case BuiltinFunction.CallPointer:
                // The C types of the pointer's signature have the same bits as Nova's (§28.3): nothing converts.
                return CallThrough(Argument(0).Bits(0, 8), [.. arguments.Skip(1).Select(a => Operand(frame, a))]);
            case BuiltinFunction.TargetIsX8664:
                return Value.Of(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.X64 ? 1u : 0u, 1);
            case BuiltinFunction.TargetIsAArch64:
                return Value.Of(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                    == System.Runtime.InteropServices.Architecture.Arm64 ? 1u : 0u, 1);
            default:
                return IoBuiltin(frame, builtin);
        }
    }

    private static Value Vector(ulong pointer, ulong length, ulong capacity)
    {
        var vector = Value.Uninitialised(24);
        vector.Put(0, Value.Of(pointer, 8));
        vector.Put(8, Value.Of(length, 8));
        vector.Put(16, Value.Of(capacity, 8));
        return vector;
    }

    /// <summary>nvKV_: the handle's index is in range and its generation matches.</summary>
    private bool SlotMapValid(NovaType mapType, ulong map, Value handle)
    {
        var index = handle.Bits(0, 4);
        var (slotSize, _) = SlotLayout(mapType.Element!);
        return index < _memory.LoadBits(map + 8, 8) && _memory.LoadBits(Pointer(map) + index * slotSize, 4) == handle.Bits(4, 4);
    }

    /// <summary>nvKI_: a free slot or a new one (at most UINT32_MAX - 1); its generation becomes odd.</summary>
    private Value SlotMapInsert(NovaType mapType, ulong map, Value value, NovaType resultType)
    {
        var (slotSize, valueOffset) = SlotLayout(mapType.Element!);
        ulong index;
        var free = _memory.LoadBits(map + 32, 4);
        if (free != uint.MaxValue)
        {
            index = free;
            _memory.StoreBits(map + 32, _memory.LoadBits(Pointer(map) + index * slotSize + 4, 4), 4);
        }
        else
        {
            var length = _memory.LoadBits(map + 8, 8);
            var pointer = length < uint.MaxValue - 1
                ? VectorGrow(Pointer(map), length, map + 16, 1, slotSize, AllocatorId(mapType.Allocator!))
                : 0;
            if (pointer == 0)
            {
                DestroyValue(mapType.Element!, value);
                return OutOfMemory(resultType);
            }
            _memory.StoreBits(map, pointer, 8);
            index = length;
            _memory.StoreBits(pointer + index * slotSize, 0, 4);
            _memory.StoreBits(map + 8, length + 1, 8);
        }
        var slot = Pointer(map) + index * slotSize;
        var generation = _memory.LoadBits(slot, 4) + 1;
        _memory.StoreBits(slot, generation, 4);
        _memory.Store(slot + valueOffset, value);
        _memory.StoreBits(map + 24, _memory.LoadBits(map + 24, 8) + 1, 8);
        var handle = Value.Uninitialised(8);
        handle.Put(0, Value.Of(index, 4));
        handle.Put(4, Value.Of(generation, 4));
        return Ok(resultType, handle);
    }
}
