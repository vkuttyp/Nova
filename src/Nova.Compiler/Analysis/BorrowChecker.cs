using System.Collections.Immutable;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
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

    /// <summary>
    /// A local that may hold loans: of a scoped type (type-system.md §19.1, §25.3), or in generic code of a type
    /// that mentions a type parameter (§25.7 rule 5).
    /// </summary>
    private static bool IsReference(NirLocal local) => TypeChecker.MayHoldLoans(local.Type);

    /// <summary>
    /// A write of a value that may hold loans through the reference (or reference-holding value) <see cref="Holder"/>
    /// (type-system.md §25.7 rules 2–4): the loans also reach what the holder's mutable loans point to.
    /// </summary>
    private sealed record ThroughWrite(NirLocal Holder, ImmutableArray<NirLocal> Sources, bool Shared, TextSpan Span);

    private readonly List<ThroughWrite> _throughWrites = [];

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
                CollectWrites(block.Statements[i], edges);
                if (block.Statements[i] is NirUnpack unpack && IsReference(unpack.Source.Local))
                {
                    // The payloads of a scoped result carry its loans (lifetimes.md §3, M4a).
                    foreach (var payload in unpack.Targets.OfType<NirLocal>().Where(IsReference))
                    {
                        edges.Add((unpack.Source.Local, payload));
                    }
                    continue;
                }
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
                        // A reborrow through q, or a borrow of a scoped value of q, keeps q's loans alive
                        // while the new reference lives.
                        if (IsReference(place.Local) && (DerefsReference(place) || TypeTable.IsScoped(place.Type)))
                        {
                            edges.Add((place.Local, target));
                        }
                        break;
                    }
                    case NirCall call:
                        foreach (var index in call.Callee.ReturnSources)
                        {
                            if (index < call.Arguments.Length && call.Arguments[index] is NirPlaceOperand argument
                                && IsReference(argument.Place.Local))
                            {
                                edges.Add((argument.Place.Local, target));
                            }
                        }
                        break;
                    default:
                        // Copies, moves, payload reads, aggregates and built-in results take the loans
                        // of every scoped value they read.
                        // Only a value that is itself scoped (or read through a reference) carries loans:
                        // the `Err(AllocError)` payload of a scoped result does not.
                        // A region allocation borrows only from its region (argument 0), not from a frame's
                        // allocation right, which it needs for the call only (type-system.md §19.5).
                        // The allocated value's loans are the allocation's too (§25.4).
                        var sources = assign.Value is NirBuiltin { Function: BuiltinFunction.ArenaAlloc } alloc
                            ? [alloc.Arguments[0], alloc.Arguments[1]]
                            : assign.Value is NirBuiltin { Function: BuiltinFunction.ArenaAllocArray } array
                            ? [array.Arguments[0]]
                            : MoveData.Operands(assign.Value);
                        foreach (var operand in sources.OfType<NirPlaceOperand>()
                                     .Where(o => IsReference(o.Place.Local) && (TypeTable.IsScoped(o.Type) || DerefsReference(o.Place))))
                        {
                            edges.Add((operand.Place.Local, target));
                        }
                        break;
                }
            }
        }

        // Writes through references reach what the references' loans point to, which depends on the loans
        // computed so far: iterate until nothing changes (§25.7 rule 2).
        var known = new HashSet<(NirLocal, NirLocal)>(edges);
        while (true)
        {
            Propagate(edges);
            var added = false;
            foreach (var write in _throughWrites)
            {
                foreach (var loan in LoansOf(write.Holder).Select(id => _loans[id]).Where(l => l.Place is not null && (l.IsMutable || write.Shared)))
                {
                    foreach (var source in write.Sources)
                    {
                        if (known.Add((source, loan.Place!.Local)))
                        {
                            edges.Add((source, loan.Place.Local));
                            added = true;
                        }
                    }
                }
            }
            if (!added)
            {
                break;
            }
        }
        CheckParameterWrites();
    }

    private void Propagate(List<(NirLocal From, NirLocal To)> edges)
    {
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

    /// <summary>The loan-carrying locals an operand list reads.</summary>
    private static ImmutableArray<NirLocal> Carriers(IEnumerable<NirOperand> operands) =>
        [.. operands.OfType<NirPlaceOperand>().Select(o => o.Place.Local).Where(IsReference).Distinct()];

    /// <summary>
    /// Writes that store loans somewhere other than a fresh local (type-system.md §25.7 rules 1–4): into a place of
    /// a local, through a reference, into a collection, or into a <c>borrow mut</c> argument of a call.
    /// </summary>
    private void CollectWrites(NirStatement statement, List<(NirLocal From, NirLocal To)> edges)
    {
        var (value, span) = statement switch
        {
            NirAssign a => (a.Value, a.Span),
            NirEval e => (e.Value, e.Span),
            _ => (null, default),
        };
        if (value is null)
        {
            return;
        }
        void Write(NirLocal holder, ImmutableArray<NirLocal> sources, bool throughReference, bool shared)
        {
            if (sources.IsEmpty)
            {
                return;
            }
            foreach (var source in sources)
            {
                edges.Add((source, holder));
            }
            if (throughReference)
            {
                _throughWrites.Add(new ThroughWrite(holder, sources, shared, span));
            }
        }
        // Rule 1/2: a value stored into a place that is not a whole local.
        if (statement is NirAssign { Target.IsLocal: false } store && TypeChecker.MayHoldLoans(store.Target.Type))
        {
            Write(store.Target.Local, Carriers(MoveData.Operands(store.Value)), DerefsReference(store.Target), shared: false);
        }
        switch (value)
        {
            // Rule 3: collection writes store their argument through the receiver.
            case NirBuiltin { Function: BuiltinFunction.VectorPush or BuiltinFunction.SlotMapInsert or BuiltinFunction.StableVectorPush } push
                when push.Arguments[0] is NirPlaceOperand receiver:
                Write(receiver.Place.Local, Carriers([push.Arguments[1]]), throughReference: true,
                    shared: push.Function == BuiltinFunction.StableVectorPush);
                break;
            // Rule 4: a call may store an argument's loans through a `borrow mut` argument (§25.6 flows).
            case NirCall call:
                foreach (var (target, source) in call.Callee.MutableFlows)
                {
                    if (target < call.Arguments.Length && source < call.Arguments.Length && call.Arguments[target] is NirPlaceOperand receiver)
                    {
                        Write(receiver.Place.Local, Carriers([call.Arguments[source]]), throughReference: true, shared: false);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// N0272 (§25.7 rule 2): a write that reaches memory a <c>borrow mut</c> parameter refers to may only store
    /// loans of parameters that the signature lets flow into that parameter.
    /// </summary>
    private void CheckParameterWrites()
    {
        var flows = _function.Source.MutableFlows;
        foreach (var write in _throughWrites)
        {
            var targets = LoansOf(write.Holder).Select(id => _loans[id])
                .Where(l => l.IsMutable || write.Shared)
                .Select(l => l.Place is null ? l.ParameterIndex : DerefOfParameter(l.Place))
                .Where(p => p >= 0 && _function.Locals[p].Type.IsMutableReference)
                .Distinct().ToList();
            if (targets.Count == 0)
            {
                continue;
            }
            var stored = write.Sources.SelectMany(LoansOf).Distinct().Select(id => _loans[id]).ToList();
            foreach (var target in targets)
            {
                var foreign = stored.FirstOrDefault(l =>
                {
                    var from = l.Place is null ? l.ParameterIndex : DerefOfParameter(l.Place);
                    return from < 0 || !flows.Contains((target, from));
                });
                if (foreign is not null)
                {
                    var name = _function.Locals[target].Name is { } n ? _symbols.GetName(n) : "?";
                    var origin = foreign.Place is null
                        ? $"parameter '{(_function.Locals[foreign.ParameterIndex].Name is { } p ? _symbols.GetName(p) : "?")}'"
                        : Quoted(foreign.Place);
                    _diagnostics.Error(DiagnosticCodes.ForeignLoanStored, _source, write.Span,
                        $"this stores a value borrowed from {origin} into what '{name}' refers to, which outlives this call; the signature must let it flow there, e.g. with a shared region: 'fn f<region r>({name}: borrow mut X<r>, v: Y<r>)' (type-system.md §25.6)",
                        foreign.Place is null ? [] : [new DiagnosticNote(foreign.Span, "borrowed here")]);
                    break;
                }
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
        NirUnpack u => u.Targets.OfType<NirLocal>().Select(t => (AccessKind.Write, new NirPlace(t))).Prepend((AccessKind.Move, u.Source)),
        NirBoxFree f => [(AccessKind.Dead, f.Box)],
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
        accesses.Where(a => IsReference(a.Place.Local)
                            && (a.Kind != AccessKind.Dead || TypeTable.DropReadsLoans(a.Place.Local.Type)) // §25.7 rule 6
                            && !(a.Kind == AccessKind.Write && a.Place.IsLocal))
                .Select(a => a.Place.Local);

    /// <summary>
    /// The reference locals a statement defines: an assignment's target, or the payload bindings of an
    /// `Unpack` (a consuming match binding a reference payload).
    /// </summary>
    private static IEnumerable<NirLocal> Definitions(NirStatement statement) => statement switch
    {
        NirAssign { Target.IsLocal: true } a when IsReference(a.Target.Local) => [a.Target.Local],
        NirUnpack u => u.Targets.OfType<NirLocal>().Where(IsReference),
        _ => [],
    };

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
                    live.ExceptWith(Definitions(block.Statements[i]));
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
                var liveLoans = LiveLoans(live[i], created, block, i);
                // An assignment writes its target after the value is computed: loans only used by the
                // value (`a[0] = sum(a[..])`) have ended by then, so the write is judged after the statement.
                var write = i < block.Statements.Count && block.Statements[i] is NirAssign ? accesses[^1] : default;
                var liveAfterLoans = write.Place is null ? liveLoans : LiveLoans(live[i + 1], created, block, i);
                foreach (var access in accesses)
                {
                    var (kind, place) = access;
                    foreach (var loan in access == write ? liveAfterLoans : liveLoans)
                    {
                        // A reference going out of scope does not end what it refers to.
                        var throughReference = kind == AccessKind.Dead
                            && (DerefsReference(loan.Place!) || returned.Contains(loan.Id));
                        if (!throughReference && Overlap(loan.Place!, place) && Conflicts(loan, kind) && _reported.Add((block, i, loan.Id)))
                        {
                            Report(loan, kind, place, span, block, i);
                        }
                    }
                }
            }
        }
    }

    private List<Loan> LiveLoans(HashSet<NirLocal> live, Loan? created, NirBlock block, int index) =>
        [.. live.SelectMany(LoansOf).Distinct().Select(id => _loans[id])
            .Where(l => l.Place is not null && l != created && Reached(l, block, index))];

    private readonly Dictionary<int, HashSet<NirBlock>> _reachedBlocks = [];

    /// <summary>
    /// Is position (<paramref name="block"/>, <paramref name="index"/>) reachable from where <paramref name="loan"/> is
    /// created? A loan does not exist before that, even in a holder that is live there (type-system.md §25.7: holders
    /// such as a vector receive loans after they are defined).
    /// </summary>
    private bool Reached(Loan loan, NirBlock block, int index)
    {
        if (loan.Block is null)
        {
            return true;
        }
        if (block == loan.Block && index > loan.Index)
        {
            return true;
        }
        if (!_reachedBlocks.TryGetValue(loan.Id, out var reached))
        {
            reached = [];
            var work = new Queue<NirBlock>(loan.Block.Terminator!.Successors);
            while (work.TryDequeue(out var next))
            {
                if (reached.Add(next))
                {
                    foreach (var successor in next.Terminator!.Successors)
                    {
                        work.Enqueue(successor);
                    }
                }
            }
            _reachedBlocks.Add(loan.Id, reached);
        }
        return reached.Contains(block);
    }

    /// <summary>Does the place go through a reference? (A box deref does not: the box owns what it points to.)</summary>
    private static bool DerefsReference(NirPlace place)
    {
        var type = place.Local.Type;
        foreach (var projection in place.Projections)
        {
            if (projection.IsDeref && type.IsReference)
            {
                return true;
            }
            type = projection.Apply(type);
        }
        return false;
    }

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
        if (!TypeChecker.MayHoldLoans(_function.ReturnType))
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
            // A reborrow of a local reference is reported through that reference's own loans, and
            // one local loan per return is enough (an allocation and its region are both local).
            var reportedLocal = false;
            foreach (var loan in LoansOf(value.Place.Local).Order().Select(id => _loans[id])
                         .Where(l => l.Place is null || !(DerefsReference(l.Place) && l.Place.Local.Id >= _function.ParameterCount)))
            {
                var parameter = loan.Place is null ? loan.ParameterIndex : DerefOfParameter(loan.Place);
                if (parameter < 0)
                {
                    if (reportedLocal)
                    {
                        continue;
                    }
                    reportedLocal = true;
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
