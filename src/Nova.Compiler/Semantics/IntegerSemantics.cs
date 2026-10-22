using System.Numerics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Semantics;

/// <summary>
/// Exact integer semantics of Nova operators (docs/type-system.md §6–7), used for constant
/// evaluation. The C runtime helpers (increment 1c) must agree with these definitions; the
/// differential tests check that.
/// </summary>
public static class IntegerSemantics
{
    /// <summary>Reduces <paramref name="value"/> modulo 2^bits and reinterprets it in <paramref name="type"/>.</summary>
    public static BigInteger Wrap(BigInteger value, NovaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var modulus = BigInteger.One << type.Bits;
        var reduced = BigInteger.Remainder(value, modulus);
        if (reduced.Sign < 0)
        {
            reduced += modulus;
        }
        return type.IsSigned && reduced > type.Max ? reduced - modulus : reduced;
    }

    /// <summary>
    /// Evaluates an arithmetic, bitwise or shift operator. <paramref name="type"/> is the operand
    /// (and result) type; for shifts it is the left operand's type and <paramref name="b"/> the amount.
    /// Returns false with a reason where the runtime would panic.
    /// </summary>
    public static bool TryEvaluate(BinaryOp op, BigInteger a, BigInteger b, NovaType type, out BigInteger result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);
        error = null;
        switch (op)
        {
            case BinaryOp.Add: result = a + b; break;
            case BinaryOp.Subtract: result = a - b; break;
            case BinaryOp.Multiply: result = a * b; break;
            case BinaryOp.Divide:
            case BinaryOp.Remainder:
                if (b.IsZero)
                {
                    result = default;
                    error = "division by zero";
                    return false;
                }
                // BigInteger division truncates toward zero; the remainder has the dividend's sign.
                result = op == BinaryOp.Divide ? BigInteger.Divide(a, b) : BigInteger.Remainder(a, b);
                if (op == BinaryOp.Remainder)
                {
                    return true; // |a % b| < |b|, always in range (MIN % -1 == 0)
                }
                break;
            case BinaryOp.BitAnd: result = a & b; break;
            case BinaryOp.BitOr: result = a | b; break;
            case BinaryOp.BitXor: result = a ^ b; break;
            case BinaryOp.ShiftLeft:
            case BinaryOp.ShiftRight:
                if (b.Sign < 0 || b >= type.Bits)
                {
                    result = default;
                    error = $"shift amount {b} is out of range for '{type}' (0..{type.Bits - 1})";
                    return false;
                }
                // Bits shifted out are discarded; '>>' is logical (operands are unsigned in M1).
                result = op == BinaryOp.ShiftLeft ? Wrap(a << (int)b, type) : a >> (int)b;
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "not an arithmetic operator");
        }

        if (!type.Fits(result))
        {
            error = $"arithmetic overflow: the result {result} does not fit in '{type}'";
            return false;
        }
        return true;
    }

    public static bool Compare(BinaryOp op, BigInteger a, BigInteger b) => op switch
    {
        BinaryOp.Equal => a == b,
        BinaryOp.NotEqual => a != b,
        BinaryOp.Less => a < b,
        BinaryOp.LessEqual => a <= b,
        BinaryOp.Greater => a > b,
        BinaryOp.GreaterEqual => a >= b,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "not a comparison"),
    };
}
