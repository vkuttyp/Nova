using Nova.Compiler.Diagnostics;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Analysis;

/// <summary>
/// Initialisation and move checking on NIR (type-system.md §14.3–14.4; ownership.md O2, O3):
/// every read, borrow or move needs a definitely initialised place (N0212, N0250). Moves out of
/// references (N0251), out of fields of types with a drop block (N0252) and out of `using`
/// bindings (N0253) are rejected. Each variable is reported at most once.
/// </summary>
internal sealed class MoveChecker
{
    private readonly MoveData _data;
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly SourceText _source;
    private readonly HashSet<NirLocal> _reported = [];

    private MoveChecker(MoveData data, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _data = data;
        _symbols = symbols;
        _diagnostics = diagnostics;
        _source = data.Function.Source.Source;
    }

    public static void Check(NirFunction function, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        var checker = new MoveChecker(new MoveData(function), symbols, diagnostics);
        foreach (var block in function.Blocks)
        {
            checker.CheckBlock(block);
        }
    }

    private void CheckBlock(NirBlock block)
    {
        var states = _data.Before[block];
        for (var i = 0; i < block.Statements.Count; i++)
        {
            var state = states[i].Clone();
            var statement = block.Statements[i];
            switch (statement)
            {
                case NirAssign assign:
                    CheckRvalue(assign.Value, state, statement.Span);
                    _data.ApplyMoves(assign.Value, state);
                    CheckWrite(assign.Target, state, statement.Span);
                    break;
                case NirEval eval:
                    CheckRvalue(eval.Value, state, statement.Span);
                    break;
                case NirUnpack unpack:
                    CheckUse(unpack.Source, isMove: true, state, statement.Span);
                    break;
            }
        }

        var terminatorState = states[block.Statements.Count];
        var operand = block.Terminator switch
        {
            NirBranch b => b.Condition,
            NirSwitch s => s.Value,
            NirReturn r => r.Value,
            NirExit e => e.Code,
            _ => null,
        };
        if (operand is NirPlaceOperand place)
        {
            CheckUse(place.Place, place.IsMove, terminatorState, block.Terminator!.Span);
        }
    }

    /// <summary>Operands are evaluated left to right; a place moved by an earlier operand is uninitialised for later ones.</summary>
    private void CheckRvalue(NirRvalue rvalue, InitState state, TextSpan span)
    {
        if (rvalue is NirRef reference)
        {
            CheckUse(reference.Place, isMove: false, state, span);
            return;
        }
        if (rvalue is NirDiscriminant discriminant)
        {
            CheckUse(discriminant.Place, isMove: false, state, span);
            return;
        }
        if (rvalue is NirLength length)
        {
            CheckUse(length.Slice, isMove: false, state, span);
            return;
        }
        if (rvalue is NirSlice slice)
        {
            CheckUse(slice.Base, isMove: false, state, span);
        }
        var running = state.Clone();
        foreach (var operand in MoveData.Operands(rvalue).OfType<NirPlaceOperand>())
        {
            CheckUse(operand.Place, operand.IsMove, running, span);
            if (operand.IsMove && _data.MovedPath(operand.Place) is { } moved)
            {
                running.Uninitialize(moved, moved: true);
            }
        }
    }

    private string Name(NirLocal local) => local.Name is { } name ? _symbols.GetName(name) : "a temporary";

    private string Describe(NirPlace place)
    {
        var text = Name(place.Local);
        foreach (var projection in place.Projections.TakeWhile(p => p.IsField || p.IsDeref))
        {
            text += projection.IsField ? "." + _symbols.GetName(projection.Field!.Name) : "";
        }
        return text;
    }

    private void Error(NirLocal local, string code, TextSpan span, string message)
    {
        if (_reported.Add(local))
        {
            _diagnostics.Error(code, _source, span, message);
        }
    }

    private void CheckUse(NirPlace place, bool isMove, InitState state, TextSpan span)
    {
        var hasDeref = !place.IsMovePath; // through a reference, into an element, or into an enum variant
        // Reading through a reference needs the reference; reading a place needs the place.
        var path = hasDeref ? _data.Root(place.Local) : _data.PathFor(place);
        if (!state.DefinitelyInit(path))
        {
            var what = hasDeref ? Name(place.Local) : Describe(place);
            if (state.AnyMaybeMoved(path))
            {
                Error(place.Local, DiagnosticCodes.UseOfMoved, span, $"'{what}' is used here after it was moved (on at least one path)");
            }
            else
            {
                Error(place.Local, DiagnosticCodes.PossiblyUnassigned, span,
                    $"'{what}' is read here but may not have been assigned a value");
            }
            return;
        }
        if (!isMove)
        {
            return;
        }
        if (hasDeref && MoveData.IsPayloadOfLocal(place))
        {
            return; // `?` / `!` consuming a result (type-system.md §17.4)
        }
        if (hasDeref)
        {
            Error(place.Local, DiagnosticCodes.MoveOutOfReference, span, place.Local.Type.IsBox && place.Projections[0].IsDeref
                ? $"cannot move a '{place.Type}' out of the contents of the box '{Name(place.Local)}'; only Copy values can be read out of a box"
                : place.Projections.Any(p => p.IsDeref)
                ? $"cannot move a '{place.Type}' out of the value '{Name(place.Local)}' refers to; only Copy values can be read through a reference"
                : $"cannot move a '{place.Type}' out of an element or enum payload of '{Name(place.Local)}'; only Copy values can be read from there");
            return;
        }
        if (place.Local.IsUsing)
        {
            Error(place.Local, DiagnosticCodes.MoveOutOfUsing, span,
                $"cannot move out of '{Describe(place)}': it is a 'using' binding, which lives until the end of its block");
            return;
        }
        // Partial moves are not allowed out of a struct whose drop block needs the whole value.
        var node = _data.PathFor(place);
        if (node.Ancestors().FirstOrDefault(a => a.Type.Struct?.DropFunction is not null) is { } owner)
        {
            Error(place.Local, DiagnosticCodes.MoveOutOfDropType, span,
                $"cannot move '{Describe(place)}' out of '{Describe(owner.Place())}': '{owner.Type}' has a drop block, which needs the whole value");
        }
    }

    private void CheckWrite(NirPlace target, InitState state, TextSpan span)
    {
        if (!target.IsMovePath)
        {
            CheckUse(new NirPlace(target.Local), isMove: false, state, span);
            return;
        }
        if (target.IsLocal)
        {
            return;
        }
        // p.f = v needs the rest of p initialised: no piecewise construction (type-system.md §14.4).
        var written = _data.PathFor(target);
        // A struct with a drop block is never partially moved (N0252), so an uninitialised leaf means the
        // whole value is gone: its fields cannot be written, even when the written field is the only one.
        var rest = written.Ancestors().LastOrDefault(a => a.Type.Struct?.DropFunction is not null) is { } owner
            ? owner.Leaves().Concat(_data.Root(target.Local).Leaves().Except(owner.Leaves()))
            : _data.Root(target.Local).Leaves().Except(written.Leaves());
        if (rest.Any(leaf => state.MaybeUninit[leaf.LeafIndex]))
        {
            var moved = rest.Any(leaf => state.MaybeUninit[leaf.LeafIndex] && state.MaybeMoved[leaf.LeafIndex]);
            Error(target.Local, moved ? DiagnosticCodes.UseOfMoved : DiagnosticCodes.PossiblyUnassigned, span, moved
                ? $"cannot assign to '{Describe(target)}': the rest of '{Name(target.Local)}' was moved"
                : $"'{Name(target.Local)}' is read here but may not have been assigned a value");
        }
    }
}
