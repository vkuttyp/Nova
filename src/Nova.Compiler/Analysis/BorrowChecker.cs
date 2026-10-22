using System.Collections.Immutable;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Analysis;

/// <summary>
/// The NLL borrow checker of docs/lifetimes.md, on one NIR function:
/// loans and points-to sets (§3), accesses and conflicts (§4), liveness (§5),
/// temporaries (§6), and returned references (§7).
/// </summary>
internal sealed class BorrowChecker
{
    private enum AccessKind
    {
        Read,
        Write,
        BorrowShared,
        BorrowMut,
        Dead,
        /// <summary>A move out of a place: invalidates it like a write (type-system.md §14.3).</summary>
        Move,
    }

    /// <summary>A loan. <see cref="Place"/> is null for the placeholder of a reference parameter.</summary>
    private sealed record Loan(int Id, bool IsMutable, NirPlace? Place, NirBlock? Block, int Index, TextSpan Span, int ParameterIndex);

    private readonly NirFunction _function;
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly SourceText _source;

    private readonly List<Loan> _loans = [];
    private readonly Dictionary<NirLocal, HashSet<int>> _pointsTo = [];
    private readonly Dictionary<(NirBlock, int), Loan> _loanAt = [];
    private readonly HashSet<(NirBlock, int, int)> _reported = [];

    private BorrowChecker(NirFunction function, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _function = function;
        _symbols = symbols;
        _diagnostics = diagnostics;
        _source = function.Source.Source;
    }

    public static void Check(NirFunction function, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(function);
        var checker = new BorrowChecker(function, symbols, diagnostics);
        checker.ComputeLoans();
        checker.CheckConflicts();
        checker.CheckReturns();
    }

    private static bool IsReference(NirLocal local) => local.Type.IsReference;

    private HashSet<int> LoansOf(NirLocal local)
    {
        if (!_pointsTo.TryGetValue(local, out var set))
        {
            set = [];
            _pointsTo.Add(local, set);
        }
        return set;
    }

    // ---- §3 Loans and points-to sets ----

    private void ComputeLoans()
    {
        // Placeholder loans for reference parameters.
        for (var i = 0; i < _function.ParameterCount; i++)
        {
            var parameter = _function.Locals[i];
            if (IsReference(parameter))
            {
                var loan = new Loan(_loans.Count, parameter.Type.IsMutableReference, null, null, -1, default, i);
                _loans.Add(loan);
                LoansOf(parameter).Add(loan.Id);
            }
        }

        var edges = new List<(NirLocal From, NirLocal To)>();
        foreach (var block in _function.Blocks)
        {
            for (var i = 0; i < block.Statements.Count; i++)
            {
                if (block.Statements[i] is not NirAssign { Target.IsLocal: true } assign || !IsReference(assign.Target.Local))
                {
                    continue;
                }
                var target = assign.Target.Local;
                switch (assign.Value)
                {
                    case NirRef or NirSlice:
                    {
                        // A slice is a loan of the whole array or slice it views (type-system.md §16.3).
                        var (isMutable, place) = assign.Value is NirRef r ? (r.IsMutable, r.Place) : (((NirSlice)assign.Value).IsMutable, ((NirSlice)assign.Value).Base);
                        var loan = new Loan(_loans.Count, isMutable, place, block, i, assign.Span, -1);
                        _loans.Add(loan);
                        _loanAt[(block, i)] = loan;
                        LoansOf(target).Add(loan.Id);
                        // A reborrow through q keeps q's loans alive while the new reference lives.
                        if (place.Projections.Any(p => p.IsDeref))
                        {
                            edges.Add((place.Local, target));
                        }
                        break;
                    }
                    case NirUse { Operand: NirPlaceOperand { Place.IsLocal: true } copy } when IsReference(copy.Place.Local):
                        edges.Add((copy.Place.Local, target));
                        break;
                    case NirCall call:
                        foreach (var index in call.Callee.ReturnSources)
                        {
                            if (index < call.Arguments.Length && call.Arguments[index] is NirPlaceOperand { Place.IsLocal: true } argument
                                && IsReference(argument.Place.Local))
                            {
                                edges.Add((argument.Place.Local, target));
                            }
                        }
                        break;
                }
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (from, to) in edges)
            {
                var before = LoansOf(to).Count;
                LoansOf(to).UnionWith(LoansOf(from));
                changed |= LoansOf(to).Count != before;
            }
        }
    }

    // ---- §4 Accesses ----

    private static IEnumerable<(AccessKind Kind, NirPlace Place)> OperandAccesses(NirOperand? operand) =>
        operand is NirPlaceOperand place ? [(place.IsMove ? AccessKind.Move : AccessKind.Read, place.Place)] : [];

    private static IEnumerable<(AccessKind Kind, NirPlace Place)> RvalueAccesses(NirRvalue rvalue) => rvalue switch
    {
        NirUse u => OperandAccesses(u.Operand),
        NirBinary b => OperandAccesses(b.Left).Concat(OperandAccesses(b.Right)),
        NirUnary u => OperandAccesses(u.Operand),
        NirCast c => OperandAccesses(c.Operand),
        NirTruncate t => OperandAccesses(t.Operand),
        NirAggregate a => a.Fields.SelectMany(OperandAccesses),
        NirCall c => c.Arguments.SelectMany(OperandAccesses),
        NirBuiltin b => b.Arguments.SelectMany(OperandAccesses),
        NirRef r => [(r.IsMutable ? AccessKind.BorrowMut : AccessKind.BorrowShared, r.Place)],
        NirSlice s => OperandAccesses(s.Low).Concat(OperandAccesses(s.High))
            .Append((s.IsMutable ? AccessKind.BorrowMut : AccessKind.BorrowShared, s.Base)),
        NirLength l => [(AccessKind.Read, l.Slice)],
        NirArray a => a.Elements.SelectMany(OperandAccesses),
        NirArrayRepeat a => OperandAccesses(a.Value),
        NirDiscriminant d => [(AccessKind.Read, d.Place)],
        NirEnumAggregate e => e.Payload.SelectMany(OperandAccesses),
        _ => [],
    };

    private static IEnumerable<(AccessKind Kind, NirPlace Place)> StatementAccesses(NirStatement statement) => statement switch
    {
        NirAssign a => RvalueAccesses(a.Value).Append((AccessKind.Write, a.Target)),
        NirEval e => RvalueAccesses(e.Value),
        NirBoundsCheck c => OperandAccesses(c.Index).Concat(OperandAccesses(c.Length)),
        NirRangeCheck c => OperandAccesses(c.Low).Concat(OperandAccesses(c.High)).Concat(OperandAccesses(c.Length)),
        NirStorageDead d => [(AccessKind.Dead, new NirPlace(d.Local))],
        NirDrop d => [(AccessKind.Dead, d.Place)],
        _ => [],
    };

    private static IEnumerable<(AccessKind Kind, NirPlace Place)> TerminatorAccesses(NirTerminator terminator) => terminator switch
    {
        NirBranch b => OperandAccesses(b.Condition),
        NirSwitch s => OperandAccesses(s.Value),
        NirReturn r => OperandAccesses(r.Value),
        NirExit e => OperandAccesses(e.Code),
        _ => [],
    };

    /// <summary>Same root, and one projection list is a prefix of the other.</summary>
    private static bool Overlap(NirPlace a, NirPlace b)
    {
        if (a.Local != b.Local)
        {
            return false;
        }
        var common = Math.Min(a.Projections.Length, b.Projections.Length);
        for (var i = 0; i < common; i++)
        {
            var (x, y) = (a.Projections[i], b.Projections[i]);
            var same = x.Kind == y.Kind && x.Kind switch
            {
                ProjectionKind.Field => x.Field == y.Field,
                ProjectionKind.Downcast => x.Variant == y.Variant,
                _ => true, // every element of an array overlaps every other (no index reasoning in v0)
            };
            if (!same)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Conflicts(Loan loan, AccessKind access) =>
        loan.IsMutable || access is AccessKind.Write or AccessKind.BorrowMut or AccessKind.Dead or AccessKind.Move;

    // ---- §5 Liveness ----

    /// <summary>Reference locals read by a place (its root, if it is a reference).</summary>
    private static IEnumerable<NirLocal> Uses(IEnumerable<(AccessKind Kind, NirPlace Place)> accesses) =>
        accesses.Where(a => a.Kind != AccessKind.Dead && IsReference(a.Place.Local)
                            && !(a.Kind == AccessKind.Write && a.Place.IsLocal))
                .Select(a => a.Place.Local);

    private static NirLocal? Definition(NirStatement statement) =>
        statement is NirAssign { Target.IsLocal: true } a && IsReference(a.Target.Local) ? a.Target.Local : null;

    /// <summary>Live-in sets per statement position (index Count is the terminator).</summary>
    private Dictionary<NirBlock, List<HashSet<NirLocal>>> ComputeLiveness()
    {
        var successors = _function.Blocks.ToDictionary(b => b, b => b.Terminator!.Successors.ToArray());
        var liveIn = _function.Blocks.ToDictionary(b => b, _ => new HashSet<NirLocal>());
        var positions = new Dictionary<NirBlock, List<HashSet<NirLocal>>>();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in _function.Blocks.Reverse())
            {
                var live = new HashSet<NirLocal>();
                foreach (var successor in successors[block])
                {
                    live.UnionWith(liveIn[successor]);
                }
                var perPosition = new List<HashSet<NirLocal>>(new HashSet<NirLocal>[block.Statements.Count + 1]);
                live.UnionWith(Uses(TerminatorAccesses(block.Terminator!)));
                perPosition[block.Statements.Count] = [.. live];
                for (var i = block.Statements.Count - 1; i >= 0; i--)
                {
                    if (Definition(block.Statements[i]) is { } defined)
                    {
                        live.Remove(defined);
                    }
                    live.UnionWith(Uses(StatementAccesses(block.Statements[i])));
                    perPosition[i] = [.. live];
                }
                positions[block] = perPosition;
                if (!live.SetEquals(liveIn[block]))
                {
                    liveIn[block] = live;
                    changed = true;
                }
            }
        }
        return positions;
    }

    private void CheckConflicts()
    {
        if (_loans.All(l => l.Place is null))
        {
            return;
        }
        var liveness = ComputeLiveness();
        // Loans carried by a returned reference are judged by the return check (E1/E2, §7), not as
        // "dropped while borrowed" when the function's locals end at the return.
        var returned = _function.Blocks
            .Select(b => b.Terminator)
            .OfType<NirReturn>()
            .Select(r => r.Value)
            .OfType<NirPlaceOperand>()
            .SelectMany(o => LoansOf(o.Place.Local))
            .ToHashSet();
        foreach (var block in _function.Blocks)
        {
            var live = liveness[block];
            for (var i = 0; i <= block.Statements.Count; i++)
            {
                var accesses = i < block.Statements.Count
                    ? StatementAccesses(block.Statements[i]).ToList()
                    : TerminatorAccesses(block.Terminator!).ToList();
                var span = i < block.Statements.Count ? block.Statements[i].Span : block.Terminator!.Span;
                var created = _loanAt.GetValueOrDefault((block, i));
                var liveLoans = LiveLoans(live[i], created);
                // An assignment writes its target after the value is computed: loans only used by the
                // value (`a[0] = sum(a[..])`) have ended by then, so the write is judged after the statement.
                var write = i < block.Statements.Count && block.Statements[i] is NirAssign ? accesses[^1] : default;
                var liveAfterLoans = write.Place is null ? liveLoans : LiveLoans(live[i + 1], created);
                foreach (var access in accesses)
                {
                    var (kind, place) = access;
                    foreach (var loan in access == write ? liveAfterLoans : liveLoans)
                    {
                        // A reference going out of scope does not end what it refers to.
                        var throughReference = kind == AccessKind.Dead
                            && (loan.Place!.Projections.Any(p => p.IsDeref) || returned.Contains(loan.Id));
                        if (!throughReference && Overlap(loan.Place!, place) && Conflicts(loan, kind) && _reported.Add((block, i, loan.Id)))
                        {
                            Report(loan, kind, place, span, block, i);
                        }
                    }
                }
            }
        }
    }

    private List<Loan> LiveLoans(HashSet<NirLocal> live, Loan? created) =>
        [.. live.SelectMany(LoansOf).Distinct().Select(id => _loans[id]).Where(l => l.Place is not null && l != created)];

    private string Describe(NirPlace place)
    {
        var text = place.Local.Name is { } name ? _symbols.GetName(name) : "a temporary";
        foreach (var projection in place.Projections)
        {
            text = projection.Kind switch
            {
                ProjectionKind.Field => $"{text}.{_symbols.GetName(projection.Field!.Name)}",
                ProjectionKind.Index => $"{text}[_]",
                _ => text,
            };
        }
        return text;
    }

    /// <summary>The place in quotes, or "a temporary value" for an unnamed temporary.</summary>
    private string Quoted(NirPlace place) => place.Local.Name is null ? "a temporary value" : $"'{Describe(place)}'";

    private void Report(Loan loan, AccessKind kind, NirPlace place, TextSpan span, NirBlock block, int index)
    {
        var what = Describe(loan.Place!);
        var borrowKind = loan.IsMutable ? "mutably borrowed" : "borrowed";
        var (code, message) = kind switch
        {
            AccessKind.Move => (DiagnosticCodes.WriteWhileBorrowed, $"cannot move out of {Quoted(place)} while {Quoted(loan.Place!)} is {borrowKind}"),
            AccessKind.Write => (DiagnosticCodes.WriteWhileBorrowed, $"cannot assign to {Quoted(place)} while {Quoted(loan.Place!)} is {borrowKind}"),
            AccessKind.BorrowShared or AccessKind.BorrowMut => (DiagnosticCodes.ConflictingBorrow,
                $"cannot borrow {Quoted(place)}{(kind == AccessKind.BorrowMut ? " as mutable" : "")} while {Quoted(loan.Place!)} is {borrowKind}"),
            AccessKind.Read => (DiagnosticCodes.ReadWhileMutablyBorrowed, $"cannot use {Quoted(place)} while {Quoted(loan.Place!)} is mutably borrowed"),
            _ => (DiagnosticCodes.DeadWhileBorrowed, place.Local.Name is null
                ? "a temporary value is dropped at the end of this statement while still borrowed; bind it to a local first"
                : $"'{Describe(place)}' goes out of scope while still borrowed"),
        };
        var notes = ImmutableArray.CreateBuilder<DiagnosticNote>();
        notes.Add(new DiagnosticNote(loan.Span, $"{Quoted(loan.Place!)} is {borrowKind} here"));
        if (FindLaterUse(loan, block, index) is { } use)
        {
            notes.Add(new DiagnosticNote(use, "the borrow is used later here"));
        }
        _diagnostics.Error(code, _source, span, message, notes.ToImmutable());
    }

    /// <summary>The first use, strictly after the given position, of a reference that may hold the loan.</summary>
    private TextSpan? FindLaterUse(Loan loan, NirBlock block, int index)
    {
        var holders = _pointsTo.Where(kv => kv.Value.Contains(loan.Id)).Select(kv => kv.Key).ToHashSet();
        var visited = new HashSet<NirBlock>();
        var work = new Queue<(NirBlock Block, int Start)>();
        work.Enqueue((block, index + 1));
        while (work.TryDequeue(out var item))
        {
            var (b, start) = item;
            for (var i = start; i <= b.Statements.Count; i++)
            {
                var accesses = i < b.Statements.Count ? StatementAccesses(b.Statements[i]) : TerminatorAccesses(b.Terminator!);
                if (Uses(accesses).Any(holders.Contains))
                {
                    return i < b.Statements.Count ? b.Statements[i].Span : b.Terminator!.Span;
                }
            }
            var next = b.Terminator!.Successors;
            foreach (var successor in next.Where(visited.Add))
            {
                work.Enqueue((successor, 0));
            }
        }
        return null;
    }

    // ---- §7 Returned references ----

    private void CheckReturns()
    {
        if (!_function.ReturnType.IsReference)
        {
            return;
        }
        var allowed = _function.Source.ReturnSources;
        foreach (var block in _function.Blocks)
        {
            if (block.Terminator is not NirReturn { Value: NirPlaceOperand { Place.IsLocal: true } value } ret)
            {
                continue;
            }
            foreach (var loan in LoansOf(value.Place.Local).Select(id => _loans[id]))
            {
                var parameter = loan.Place is null ? loan.ParameterIndex : DerefOfParameter(loan.Place);
                if (parameter < 0)
                {
                    _diagnostics.Error(DiagnosticCodes.ReturnsLocalReference, _source, ret.Span,
                        $"cannot return a reference to {Quoted(loan.Place!)}, which is local to '{_symbols.GetName(_function.Name)}'",
                        [new DiagnosticNote(loan.Span, "borrowed here")]);
                }
                else if (!allowed.Contains(parameter))
                {
                    var name = _function.Locals[parameter].Name is { } n ? _symbols.GetName(n) : "?";
                    _diagnostics.Error(DiagnosticCodes.ReturnSourceMismatch, _source, ret.Span,
                        $"the returned reference may be derived from parameter '{name}', which the return type does not list");
                }
            }
        }
    }

    /// <summary>The parameter index if the place starts with a deref of a reference parameter; -1 otherwise.</summary>
    private int DerefOfParameter(NirPlace place) =>
        place.Local.Id < _function.ParameterCount && IsReference(place.Local)
        && !place.Projections.IsEmpty && place.Projections[0].IsDeref
            ? place.Local.Id
            : -1;
}
