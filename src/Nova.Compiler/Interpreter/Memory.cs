namespace Nova.Compiler.Interpreter;

// The interpreter's memory (intermediate-representation.md §5.2): byte-addressed allocations with the
// target layout, each byte carrying an initialised bit. Allocation i has base address (i + 1) << 32,
// which is 16-aligned like malloc's memory, so address arithmetic behaves as in the generated C.

internal enum AllocationKind { Local, Heap, Static }

internal sealed class Allocation(int id, AllocationKind kind, byte[] data, bool[] init, string what)
{
    public int Id { get; } = id;

    public AllocationKind Kind { get; } = kind;

    public byte[] Data { get; private set; } = data;

    public bool[] Init { get; private set; } = init;

    public string What { get; } = what;

    public bool Live { get; private set; } = true;

    public ulong Base => (ulong)(Id + 1) << 32;

    public void End()
    {
        Live = false;
        Data = [];
        Init = [];
    }
}

/// <summary>A value in transit: its bytes and which of them are initialised.</summary>
internal sealed class Value(byte[] data, bool[] init)
{
    public byte[] Data { get; } = data;

    public bool[] Init { get; } = init;

    public int Size => Data.Length;

    public static Value Initialised(byte[] data) => new(data, Filled(data.Length, true));

    public static Value Uninitialised(int size) => new(new byte[size], new bool[size]);

    public static bool[] Filled(int size, bool value)
    {
        var bits = new bool[size];
        if (value)
        {
            Array.Fill(bits, true);
        }
        return bits;
    }

    public static Value Of(ulong bits, int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = (byte)(bits >> (8 * i)); // little-endian targets (x86-64, AArch64)
        }
        return Initialised(data);
    }

    /// <summary>The bytes [offset, offset + size) as an unsigned number; a use, so they must be initialised.</summary>
    public ulong Bits(int offset, int size)
    {
        ulong bits = 0;
        for (var i = 0; i < size; i++)
        {
            if (!Init[offset + i])
            {
                throw new InterpreterFault("use of uninitialised memory");
            }
            bits |= (ulong)Data[offset + i] << (8 * i);
        }
        return bits;
    }

    public void Put(int offset, Value part)
    {
        Array.Copy(part.Data, 0, Data, offset, part.Size);
        Array.Copy(part.Init, 0, Init, offset, part.Size);
    }

    public Value Slice(int offset, int size)
    {
        var value = Uninitialised(size);
        Array.Copy(Data, offset, value.Data, 0, size);
        Array.Copy(Init, offset, value.Init, 0, size);
        return value;
    }
}

/// <summary>A run-time check of the memory model failed (§5.2): the program did something no checked program can.</summary>
internal sealed class InterpreterFault(string message) : Exception(message)
{
    /// <summary>Where the program was when the fault happened (set by the innermost frame).</summary>
    public string? Location { get; set; }
}

internal sealed class Memory
{
    private readonly List<Allocation> _allocations = [];

    public Allocation Allocate(AllocationKind kind, int size, bool initialised, string what)
    {
        var allocation = new Allocation(_allocations.Count, kind, new byte[size], Value.Filled(size, initialised), what);
        _allocations.Add(allocation);
        return allocation;
    }

    public Allocation Static(byte[] bytes, string what)
    {
        var allocation = Allocate(AllocationKind.Static, bytes.Length, initialised: true, what);
        bytes.CopyTo(allocation.Data, 0);
        return allocation;
    }

    /// <summary>The live allocation containing [address, address + size).</summary>
    private (Allocation Allocation, int Offset) Resolve(ulong address, int size, bool write)
    {
        if (address == 0)
        {
            throw new InterpreterFault("dereference of null");
        }
        var id = (long)(address >> 32) - 1;
        if (id < 0 || id >= _allocations.Count)
        {
            throw new InterpreterFault($"access to an invalid address 0x{address:x}");
        }
        var allocation = _allocations[(int)id];
        if (!allocation.Live)
        {
            throw new InterpreterFault($"access to ended memory ({allocation.What})");
        }
        var offset = address & 0xFFFF_FFFF;
        if (offset + (ulong)size > (ulong)allocation.Data.Length)
        {
            throw new InterpreterFault($"access out of bounds of {allocation.What} ({size} bytes at offset {offset} of {allocation.Data.Length})");
        }
        if (write && allocation.Kind == AllocationKind.Static)
        {
            throw new InterpreterFault($"write to static memory ({allocation.What})");
        }
        return (allocation, (int)offset);
    }

    public Value Load(ulong address, int size)
    {
        if (size == 0)
        {
            return Value.Uninitialised(0);
        }
        var (allocation, offset) = Resolve(address, size, write: false);
        var value = Value.Uninitialised(size);
        Array.Copy(allocation.Data, offset, value.Data, 0, size);
        Array.Copy(allocation.Init, offset, value.Init, 0, size);
        return value;
    }

    public void Store(ulong address, Value value)
    {
        if (value.Size == 0)
        {
            return;
        }
        var (allocation, offset) = Resolve(address, value.Size, write: true);
        Array.Copy(value.Data, 0, allocation.Data, offset, value.Size);
        Array.Copy(value.Init, 0, allocation.Init, offset, value.Size);
    }

    /// <summary>Marks bytes uninitialised: the value was moved out (type-system.md §14.3).</summary>
    public void Uninitialise(ulong address, int size)
    {
        if (size == 0)
        {
            return;
        }
        var (allocation, offset) = Resolve(address, size, write: false);
        Array.Fill(allocation.Init, false, offset, size);
    }

    /// <summary>A scalar of <paramref name="size"/> bytes (a use: they must be initialised).</summary>
    public ulong LoadBits(ulong address, int size)
    {
        var (allocation, offset) = Resolve(address, size, write: false);
        var data = allocation.Data;
        var init = allocation.Init;
        ulong bits = 0;
        for (var i = 0; i < size; i++)
        {
            if (!init[offset + i])
            {
                throw new InterpreterFault("use of uninitialised memory");
            }
            bits |= (ulong)data[offset + i] << (8 * i);
        }
        return bits;
    }

    public void StoreBits(ulong address, ulong bits, int size)
    {
        var (allocation, offset) = Resolve(address, size, write: true);
        for (var i = 0; i < size; i++)
        {
            allocation.Data[offset + i] = (byte)(bits >> (8 * i));
            allocation.Init[offset + i] = true;
        }
    }

    /// <summary>Copies bytes with their initialised bits (memmove semantics).</summary>
    public void Copy(ulong destination, ulong source, int size)
    {
        if (size == 0)
        {
            return;
        }
        var (from, fromOffset) = Resolve(source, size, write: false);
        var (to, toOffset) = Resolve(destination, size, write: true);
        Array.Copy(from.Data, fromOffset, to.Data, toOffset, size);
        Array.Copy(from.Init, fromOffset, to.Init, toOffset, size);
    }

    public Allocation AllocationAt(ulong address)
    {
        var id = (long)(address >> 32) - 1;
        return id >= 0 && id < _allocations.Count ? _allocations[(int)id] : throw new InterpreterFault($"invalid address 0x{address:x}");
    }

    /// <summary>Ends a heap allocation; only its base address may be freed.</summary>
    public void Free(ulong address)
    {
        var id = (long)(address >> 32) - 1;
        if (id < 0 || id >= _allocations.Count || (address & 0xFFFF_FFFF) != 0)
        {
            throw new InterpreterFault($"free of an address that is not an allocation (0x{address:x})");
        }
        var allocation = _allocations[(int)id];
        if (allocation.Kind != AllocationKind.Heap)
        {
            throw new InterpreterFault($"free of {allocation.What}");
        }
        if (!allocation.Live)
        {
            throw new InterpreterFault("double free");
        }
        allocation.End();
    }
}
