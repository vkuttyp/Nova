using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Analysis;

/// <summary>
/// C state is made in place and never moved (type-system.md §33.2). A value of a C-laid-out type may be used as a value
/// only where its struct literal flows into its box: from the literal's temporary into <c>Box.create</c>/<c>createIn</c>,
/// or into a field of an enclosing such literal. Every other use of one as a value (a move into a local, a call, a
/// result, an enum, an array, an assignment) is N0294, and so is a literal that does not reach a box.
/// </summary>
public static class InPlaceChecker
{
    public static void Check(NirFunction function, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var source = function.Source.Source;
        var statements = function.Blocks.SelectMany(b => b.Statements).ToList();
        var reported = new HashSet<int>(); // span starts, one error per statement
        void Report(TextSpan span, string message)
        {
            if (reported.Add(span.Start))
            {
                diagnostics.Error(DiagnosticCodes.ImportedTypeByValue, source, span, message);
            }
        }
        string NotPlaced(NovaType type) =>
            $"a '{type}' holds C state, so it is made only in place, as the value of 'Box.create' or 'Box.createIn' (type-system.md §33.2)";

        // The temporaries that hold a C-laid-out literal, and where each is made. A literal made straight into a
        // variable or a field is never in its box.
        var literals = new Dictionary<int, NirStatement>();
        foreach (var statement in statements)
        {
            if (statement is NirAssign { Value: NirAggregate aggregate } assign && TypeTable.IsCLaidOut(aggregate.Type))
            {
                if (assign.Target is { Projections.IsEmpty: true, Local.Name: null })
                {
                    literals[assign.Target.Local.Id] = statement;
                }
                else
                {
                    Report(statement.Span, NotPlaced(aggregate.Type));
                }
            }
        }
        var used = new HashSet<int>();
        var placed = new HashSet<int>();

        void Use(NirOperand operand, bool intoBox, TextSpan span)
        {
            if (operand is not NirPlaceOperand { Place: var place } || !TypeTable.IsCLaidOut(place.Type))
            {
                return;
            }
            if (place.Projections.IsEmpty && literals.ContainsKey(place.Local.Id))
            {
                used.Add(place.Local.Id);
                if (intoBox)
                {
                    placed.Add(place.Local.Id);
                    return;
                }
            }
            Report(span, $"a '{place.Type}' holds C state, which never moves once made: borrow it, or make it in place as the value of 'Box.createIn' (type-system.md §33.2)");
        }

        void Rvalue(NirRvalue value, TextSpan statement)
        {
            switch (value)
            {
                case NirBuiltin { Function: BuiltinFunction.BoxCreate } create:
                    for (var i = 0; i < create.Arguments.Length; i++)
                    {
                        Use(create.Arguments[i], intoBox: i == create.Arguments.Length - 1, statement);
                    }
                    break;
                case NirAggregate aggregate:
                    // A field of a C-laid-out literal is placed with it.
                    var nested = TypeTable.IsCLaidOut(aggregate.Type);
                    foreach (var field in aggregate.Fields)
                    {
                        Use(field, nested, statement);
                    }
                    break;
                case NirUse use:
                    Use(use.Operand, intoBox: false, statement);
                    break;
                case NirCall call:
                    call.Arguments.ToList().ForEach(a => Use(a, intoBox: false, statement));
                    break;
                case NirBuiltin builtin:
                    builtin.Arguments.ToList().ForEach(a => Use(a, intoBox: false, statement));
                    break;
                case NirEnumAggregate aggregate:
                    aggregate.Payload.ToList().ForEach(p => Use(p, intoBox: false, statement));
                    break;
                case NirArray array:
                    array.Elements.ToList().ForEach(e => Use(e, intoBox: false, statement));
                    break;
                case NirArrayRepeat repeat:
                    Use(repeat.Value, intoBox: false, statement);
                    break;
                case NirCast cast:
                    Use(cast.Operand, intoBox: false, statement);
                    break;
            }
        }

        foreach (var block in function.Blocks)
        {
            foreach (var statement in block.Statements)
            {
                switch (statement)
                {
                    case NirAssign assign:
                        Rvalue(assign.Value, statement.Span);
                        break;
                    case NirEval eval:
                        Rvalue(eval.Value, statement.Span);
                        break;
                }
            }
            if (block.Terminator is NirReturn { Value: { } result } ret)
            {
                Use(result, intoBox: false, ret.Span);
            }
        }
        foreach (var (temp, made) in literals.Where(l => !used.Contains(l.Key)))
        {
            Report(made.Span, NotPlaced(function.Locals[temp].Type));
        }
    }
}
