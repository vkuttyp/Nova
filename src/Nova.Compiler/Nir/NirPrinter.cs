using System.Globalization;
using System.Text;
using Nova.Compiler.Hir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Nir;

/// <summary>Prints NIR in a MIR-like text form (`novac build --dump-nir`). For debugging and review.</summary>
public static class NirPrinter
{
    public static string Print(NirProgram program, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        var sb = new StringBuilder();
        foreach (var function in program.Functions)
        {
            var parameters = string.Join(", ", function.Locals.Take(function.ParameterCount).Select(l => $"{Local(l, symbols)}: {l.Type}"));
            sb.Append(CultureInfo.InvariantCulture, $"fn {symbols.GetName(function.Name)}({parameters}) -> {function.ReturnType} {{\n");
            foreach (var local in function.Locals.Skip(function.ParameterCount))
            {
                sb.Append(CultureInfo.InvariantCulture, $"    let {Local(local, symbols)}: {local.Type};\n");
            }
            foreach (var block in function.Blocks)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  bb{block.Id}:\n");
                foreach (var statement in block.Statements)
                {
                    sb.Append("    ").Append(Statement(statement, symbols)).Append(";\n");
                }
                sb.Append("    ").Append(Terminator(block.Terminator!, symbols)).Append(";\n");
            }
            sb.Append("}\n");
        }
        return sb.ToString();
    }

    private static string Local(NirLocal local, SymbolTable symbols) =>
        local.Name is { } name ? $"_{local.Id}({symbols.GetName(name)})" : $"_{local.Id}";

    private static string Place(NirPlace place, SymbolTable symbols)
    {
        var text = Local(place.Local, symbols);
        foreach (var projection in place.Projections)
        {
            text = projection.Kind switch
            {
                ProjectionKind.Deref => $"(*{text})",
                ProjectionKind.Field => $"{text}.{symbols.GetName(projection.Field!.Name)}",
                ProjectionKind.Downcast => $"({text} as {symbols.GetName(projection.Variant!.Name)})",
                _ => $"{text}[{(projection.Index is NirOperand index ? Operand(index, symbols) : "?")}]",
            };
        }
        return text;
    }

    private static string Operand(NirOperand operand, SymbolTable symbols) => operand switch
    {
        NirPlaceOperand p => Place(p.Place, symbols),
        NirConst c => $"const {c.Value.ToString(CultureInfo.InvariantCulture)}_{c.Type}",
        NirString s => $"const \"{s.Value}\"",
        _ => operand.ToString(),
    };

    private static string Rvalue(NirRvalue rvalue, SymbolTable symbols) => rvalue switch
    {
        NirUse u => Operand(u.Operand, symbols),
        NirBinary b => $"{b.Op}({Operand(b.Left, symbols)}, {Operand(b.Right, symbols)})",
        NirUnary u => $"{u.Op}({Operand(u.Operand, symbols)})",
        NirCast c => $"{Operand(c.Operand, symbols)} as {c.Type}",
        NirTruncate t => $"truncate<{t.Type}>({Operand(t.Operand, symbols)})",
        NirCall c => $"{symbols.GetName(c.Callee.Name)}({string.Join(", ", c.Arguments.Select(a => Operand(a, symbols)))})",
        NirRef r => $"&{(r.IsMutable ? "mut " : "")}{Place(r.Place, symbols)}",
        NirDiscriminant d => $"discriminant({Place(d.Place, symbols)})",
        NirEnumAggregate e => $"{e.Type}.{symbols.GetName(e.Variant.Name)}({string.Join(", ", e.Payload.Select(f => Operand(f, symbols)))})",
        NirAggregate a => $"{a.Type} {{ {string.Join(", ", a.Fields.Select(f => Operand(f, symbols)))} }}",
        NirArray a => $"[{string.Join(", ", a.Elements.Select(e => Operand(e, symbols)))}]",
        NirArrayRepeat a => $"[{Operand(a.Value, symbols)}; {a.Type.ArrayLength}]",
        NirSlice s => $"&{(s.IsMutable ? "mut " : "")}{Place(s.Base, symbols)}[{Operand(s.Low, symbols)}..{Operand(s.High, symbols)}]",
        NirLength l => $"length({Place(l.Slice, symbols)})",
        NirBuiltin b => $"{b.Function}({string.Join(", ", b.Arguments.Select(a => Operand(a, symbols)))})",
        _ => rvalue.ToString(),
    };

    private static string Statement(NirStatement statement, SymbolTable symbols) => statement switch
    {
        NirAssign a => $"{Place(a.Target, symbols)} = {Rvalue(a.Value, symbols)}",
        NirEval e => Rvalue(e.Value, symbols),
        NirBoundsCheck c => $"BoundsCheck({Operand(c.Index, symbols)} < {Operand(c.Length, symbols)})",
        NirRangeCheck c => $"RangeCheck({Operand(c.Low, symbols)} <= {Operand(c.High, symbols)} <= {Operand(c.Length, symbols)})",
        NirStorageDead d => $"StorageDead({Local(d.Local, symbols)})",
        NirDrop { Flag: null } d => $"Drop({Place(d.Place, symbols)})",
        NirDrop d => $"Drop({Place(d.Place, symbols)}) if {Local(d.Flag!, symbols)}",
        _ => statement.ToString()!,
    };

    private static string Terminator(NirTerminator terminator, SymbolTable symbols) => terminator switch
    {
        NirGoto g => $"goto bb{g.Target.Id}",
        NirBranch b => $"branch {Operand(b.Condition, symbols)} ? bb{b.Then.Id} : bb{b.Else.Id}",
        NirSwitch s => $"switch {Operand(s.Value, symbols)} [{string.Join(", ", s.Cases.Select(c => $"{c.Value} => bb{c.Target.Id}"))}, _ => bb{s.Otherwise.Id}]",
        NirReturn { Value: null } => "return",
        NirReturn r => $"return {Operand(r.Value!, symbols)}",
        NirExit e => $"exit {Operand(e.Code, symbols)}",
        NirUnreachable => "unreachable",
        _ => terminator.ToString()!,
    };
}
