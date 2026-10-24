using Nova.Compiler.Hir;
using Nova.Compiler.Semantics;

namespace Nova.Compiler.Interpreter;

/// <summary>
/// The interpreter's model of a few C library functions (intermediate-representation.md §5.4, type-system.md §28.6).
/// The interpreter runs no C code: any other call to an <c>extern "C"</c> function is a fault.
/// </summary>
public sealed partial class NirInterpreter
{
    private enum Shape
    {
        Pointer,
        Int32,
        Size,
    }

    private static readonly Dictionary<string, (Shape[] Parameters, Shape? Result)> s_cLibrary = new(StringComparer.Ordinal)
    {
        ["strlen"] = ([Shape.Pointer], Shape.Size),
        ["puts"] = ([Shape.Pointer], Shape.Int32),
        ["abs"] = ([Shape.Int32], Shape.Int32),
        ["memcmp"] = ([Shape.Pointer, Shape.Pointer, Shape.Size], Shape.Int32),
        ["memcpy"] = ([Shape.Pointer, Shape.Pointer, Shape.Size], Shape.Pointer),
        ["memset"] = ([Shape.Pointer, Shape.Int32, Shape.Size], Shape.Pointer),
    };

    private static bool Fits(NovaType type, Shape shape) => shape switch
    {
        Shape.Pointer => type.IsRawPointer,
        Shape.Int32 => type.IsInteger && type.Bits == 32 && type.IsSigned,
        _ => type.IsInteger && type.Bits == 64 && !type.IsSigned,
    };

    private Value? CallC(HirFunction function, Value[] arguments)
    {
        var name = _symbols.GetName(function.Name);
        if (!s_cLibrary.TryGetValue(name, out var model)
            || model.Parameters.Length != function.Parameters.Length
            || !model.Parameters.Zip(function.Parameters).All(p => Fits(p.Second.Type, p.First))
            || (model.Result is { } result ? !Fits(function.ReturnType, result) : function.ReturnType.Kind != TypeKind.Void))
        {
            throw new InterpreterFault($"unsupported: call to C function '{name}' (the interpreter runs no C code)");
        }
        ulong Address(int i) => arguments[i].Bits(0, 8);
        ulong Count(int i) => arguments[i].Bits(0, 8);
        switch (name)
        {
            case "strlen":
            {
                ulong length = 0;
                while (_memory.LoadBits(Address(0) + length, 1) != 0)
                {
                    length++;
                }
                return Value.Of(length, 8);
            }
            case "puts":
            {
                var bytes = new List<byte>();
                for (var at = Address(0); _memory.LoadBits(at, 1) is var b and not 0; at++)
                {
                    bytes.Add((byte)b);
                }
                bytes.Add((byte)'\n');
                WriteOutput(bytes.ToArray());
                return Value.Of(0, 4);
            }
            case "abs":
            {
                // abs(INT_MIN) is undefined in C.
                var value = (int)(uint)arguments[0].Bits(0, 4);
                return value == int.MinValue
                    ? throw new InterpreterFault("abs of the minimum int (undefined in C)")
                    : Value.Of((uint)Math.Abs(value), 4);
            }
            case "memcmp":
            {
                var n = Count(2);
                for (ulong i = 0; i < n; i++)
                {
                    var (a, b) = (_memory.LoadBits(Address(0) + i, 1), _memory.LoadBits(Address(1) + i, 1));
                    if (a != b)
                    {
                        return Value.Of(a < b ? unchecked((uint)-1) : 1u, 4);
                    }
                }
                return Value.Of(0, 4);
            }
            case "memcpy":
            {
                var (to, from, n) = (Address(0), Address(1), Count(2));
                if (n > 0 && to < from + n && from < to + n)
                {
                    throw new InterpreterFault("memcpy with overlapping ranges (undefined in C)");
                }
                if (n > 0)
                {
                    _memory.Store(to, _memory.Load(from, checked((int)n)));
                }
                return Value.Of(to, 8);
            }
            default: // memset
            {
                var (to, n) = (Address(0), Count(2));
                var fill = (ulong)(byte)arguments[1].Bits(0, 4);
                for (ulong i = 0; i < n; i++)
                {
                    _memory.Store(to + i, Value.Of(fill, 1));
                }
                return Value.Of(to, 8);
            }
        }
    }
}
