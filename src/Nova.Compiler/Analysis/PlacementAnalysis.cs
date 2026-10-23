using Nova.Compiler.Backend.C;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Analysis;

/// <summary>One line of <c>--explain-allocations</c>: an allocation site and where its memory comes from.</summary>
public sealed record PlacementDecision(SourceText Source, TextSpan Span, string What, bool OnStack, string Reason)
{
    public override string ToString()
    {
        var position = Source.GetLinePosition(Span.Start);
        return OnStack
            ? $"{Source.Path}:{position.Line}:{position.Column}: {What} placed on stack ({Reason})"
            : $"{Source.Path}:{position.Line}:{position.Column}: {What} stays on heap: {Reason}";
    }
}

/// <summary>The stack-placed boxes of one function (memory-model.md §5.1).</summary>
public sealed class FunctionPlacement
{
    /// <summary>Each stack-placed <c>Box.create</c> statement and its slot number.</summary>
    internal Dictionary<NirStatement, int> Sites { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The slots' value types, by slot number.</summary>
    public List<NovaType> Slots { get; } = [];

    /// <summary>The box locals of stack-placed sites: their drops destroy contents only, and their BoxFree does nothing.</summary>
    public HashSet<NirLocal> BoxLocals { get; } = [];

    public bool IsSite(NirStatement statement, out int slot) => Sites.TryGetValue(statement, out slot);
}

/// <summary>
/// Escape and placement analysis (M5; memory-model.md §5): decides, on checked NIR after drop elaboration,
/// which <c>Box.create</c> sites may use the function's stack. It never affects whether a program is accepted.
/// </summary>
public sealed class PlacementAnalysis
{
    private const int MaxBoxSize = 1024;
    private const int MaxFunctionSlots = 4096;

    private readonly Dictionary<NirFunction, FunctionPlacement> _functions = [];

    private PlacementAnalysis()
    {
    }

    /// <summary>Every allocation site's decision, in source order.</summary>
    public IReadOnlyList<PlacementDecision> Decisions { get; private set; } = [];

    public FunctionPlacement? For(NirFunction function) => _functions.GetValueOrDefault(function);

    /// <summary>The <c>--explain-allocations</c> report.</summary>
    public string Explain() => string.Concat(Decisions.Select(d => d + "\n"));

    public static PlacementAnalysis Analyze(NirProgram program, SymbolTable symbols)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        var analysis = new PlacementAnalysis();
        var decisions = new List<PlacementDecision>();
        foreach (var function in program.Functions.Where(f => !f.Source.IsGeneric))
        {
            var placement = new FunctionPlacement();
            analysis._functions[function] = placement;
            new FunctionAnalysis(function, symbols, placement, decisions).Run();
        }
        analysis.Decisions = [.. decisions
            .OrderBy(d => d.Source.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Start)
            .ThenBy(d => d.What, StringComparer.Ordinal)];
        return analysis;
    }

    private sealed class FunctionAnalysis(NirFunction function, SymbolTable symbols, FunctionPlacement placement, List<PlacementDecision> decisions)
    {
        private readonly ControlFlow _flow = new(function);
        private int _slotBytes;

        private SourceText Source => function.Source.Source;

        public void Run()
        {
            var definitions = new Dictionary<NirLocal, int>();
            foreach (var statement in function.Blocks.SelectMany(b => b.Statements))
            {
                foreach (var local in Definitions(statement))
                {
                    definitions[local] = definitions.GetValueOrDefault(local) + 1;
                }
            }
            foreach (var block in function.Blocks)
            {
                foreach (var statement in block.Statements)
                {
                    switch (statement)
                    {
                        case NirAssign { Target.IsLocal: true, Value: NirBuiltin { Function: BuiltinFunction.BoxCreate } create } site:
                            _ = create;
                            Decide(block, site, definitions);
                            break;
                        case NirAssign { Value: NirBuiltin builtin } assign when Unanalysed(builtin) is { } what:
                            decisions.Add(new PlacementDecision(Source, assign.Span, what.What, false, what.Reason));
                            break;
                        case NirEval { Value: NirBuiltin builtin } eval when Unanalysed(builtin) is { } what:
                            decisions.Add(new PlacementDecision(Source, eval.Span, what.What, false, what.Reason));
                            break;
                    }
                }
            }
        }

        /// <summary>Allocating built-ins that M5 does not place: their memory stays where the runtime puts it.</summary>
        private static (string What, string Reason)? Unanalysed(NirBuiltin builtin) => builtin.Function switch
        {
            BuiltinFunction.VectorPush or BuiltinFunction.StringAppend or BuiltinFunction.VectorWithCapacity =>
                ("vector storage", "growable storage is not analysed (M5)"),
            BuiltinFunction.StableVectorPush => ("stable-vector chunk", "growable storage is not analysed (M5)"),
            BuiltinFunction.SlotMapInsert => ("slot-map storage", "growable storage is not analysed (M5)"),
            BuiltinFunction.ArenaAlloc or BuiltinFunction.ArenaAllocArray => ("region allocation", "a region's memory is the region's"),
            BuiltinFunction.PoolTake => ("pool slot", "a pool's memory is the pool's"),
            _ => null,
        };

        private static IEnumerable<NirLocal> Definitions(NirStatement statement) => statement switch
        {
            NirAssign { Target.IsLocal: true } assign => [assign.Target.Local],
            NirUnpack unpack => unpack.Targets.OfType<NirLocal>(),
            _ => [],
        };

        private void Decide(NirBlock block, NirAssign site, Dictionary<NirLocal, int> definitions)
        {
            var result = site.Target.Local.Type;
            var box = result.Enum!.ResultTypes!.Value.Ok;
            var what = box.ToString();
            void Heap(string reason) => decisions.Add(new PlacementDecision(Source, site.Span, what, false, reason));

            if (box.Allocator!.Name != "SystemHeap")
            {
                Heap($"{box.Allocator.Name} is not placement-transparent");
                return;
            }
            var size = TypeTable.LayoutOf(box.Pointee!).Size;
            if (size > MaxBoxSize)
            {
                Heap($"{size} bytes is over the {MaxBoxSize}-byte stack limit");
                return;
            }
            if (_flow.InLoop(block))
            {
                Heap("created in a loop");
                return;
            }

            var flow = FlowSet(site.Target.Local, box, result);
            if (flow.FirstOrDefault(l => definitions.GetValueOrDefault(l) > 1) is { } redefined)
            {
                Heap($"{Name(redefined)} is assigned more than once");
                return;
            }
            if (Escape(flow, box, result) is { } reason)
            {
                Heap(reason);
                return;
            }
            if (_slotBytes + size > MaxFunctionSlots)
            {
                Heap($"the function's stack-placed boxes would exceed {MaxFunctionSlots} bytes");
                return;
            }
            _slotBytes += size;
            placement.Sites[site] = placement.Slots.Count;
            placement.Slots.Add(box.Pointee!);
            placement.BoxLocals.UnionWith(flow.Where(l => l.Type == box));
            decisions.Add(new PlacementDecision(Source, site.Span, what, true, "does not escape; SystemHeap is placement-transparent"));
        }

        /// <summary>The locals that may hold the site's box: the result, its copies, and the unpacked payloads.</summary>
        private HashSet<NirLocal> FlowSet(NirLocal result, NovaType box, NovaType resultType)
        {
            var flow = new HashSet<NirLocal> { result };
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var statement in function.Blocks.SelectMany(b => b.Statements))
                {
                    switch (statement)
                    {
                        case NirAssign { Target.IsLocal: true, Value: NirUse { Operand: NirPlaceOperand operand } } assign
                            when flow.Contains(operand.Place.Local) && (operand.Type == box || operand.Type == resultType):
                            changed |= flow.Add(assign.Target.Local);
                            break;
                        case NirUnpack unpack when flow.Contains(unpack.Source.Local):
                            foreach (var target in unpack.Targets.OfType<NirLocal>().Where(t => t.Type == box))
                            {
                                changed |= flow.Add(target);
                            }
                            break;
                    }
                }
            }
            return flow;
        }

        /// <summary>The first way the box escapes its flow set (memory-model.md §5.1), or null.</summary>
        private string? Escape(HashSet<NirLocal> flow, NovaType box, NovaType resultType)
        {
            bool Carries(NirOperand operand) =>
                operand is NirPlaceOperand p && flow.Contains(p.Place.Local) && (p.Type == box || p.Type == resultType);

            foreach (var block in function.Blocks)
            {
                foreach (var statement in block.Statements)
                {
                    var here = Where(statement.Span);
                    switch (statement)
                    {
                        case NirAssign { Value: NirUse { Operand: var operand } } assign when Carries(operand):
                            if (!(assign.Target.IsLocal && flow.Contains(assign.Target.Local)))
                            {
                                return $"stored outside the function's own locals at {here}";
                            }
                            break;
                        case NirAssign { Value: NirRef { IsMutable: true, Place: var place } } when flow.Contains(place.Local) && place.Type == box:
                            return $"borrowed mutably as a box at {here}";
                        case NirAssign { Value: var value } when MoveData.Operands(value).FirstOrDefault(Carries) is not null:
                            return Use(value, here);
                        case NirEval { Value: var value } when MoveData.Operands(value).FirstOrDefault(Carries) is not null:
                            return Use(value, here);
                        // The whole box, or a part of its contents (left after a move out), may be destroyed in place;
                        // so may the result on its error path, where it holds no box (the `?` and `!` lowering).
                        case NirDrop { Place: var place } when flow.Contains(place.Local) && !(place.IsLocal && place.Type == box)
                            && !(place.Local.Type == box && place.Projections[0].IsDeref)
                            && !(place.IsLocal && place.Type == resultType && OnlyOnErrorPath(block, place.Local)):
                            return $"may be destroyed inside {place.Local.Type} at {here}";
                        case NirAssign { Target: var target } when flow.Contains(target.Local) && !target.IsLocal && !target.Projections[0].IsDeref:
                            return $"assigned in part at {here}";
                    }
                }
                if (block.Terminator is NirReturn { Value: { } returned } && Carries(returned))
                {
                    return $"returned at {Where(block.Terminator.Span)}";
                }
            }
            return null;
        }

        /// <summary>
        /// Whether <paramref name="block"/> is reached only through a non-<c>Ok</c> branch of a switch on
        /// <paramref name="result"/>'s discriminant: a target other than case 0, entered only from the switch, dominates it.
        /// </summary>
        private bool OnlyOnErrorPath(NirBlock block, NirLocal result)
        {
            foreach (var switchBlock in function.Blocks)
            {
                if (switchBlock.Terminator is not NirSwitch { Value: NirPlaceOperand { Place.IsLocal: true } tag } choice
                    || !switchBlock.Statements.Any(s => s is NirAssign { Target: var t, Value: NirDiscriminant { Place: { IsLocal: true } of } }
                        && t.IsLocal && t.Local == tag.Place.Local && of.Local == result))
                {
                    continue;
                }
                var okTargets = choice.Cases.Where(c => c.Value == 0).Select(c => c.Target).ToHashSet();
                foreach (var target in choice.Successors.Distinct().Where(t => !okTargets.Contains(t)))
                {
                    if (_flow.PredecessorsOf(target).All(p => p == switchBlock) && _flow.Dominates(target, block))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private string Use(NirRvalue value, string here) => value switch
        {
            NirCall call => $"moved into call to '{symbols.GetName(call.Callee.Name)}' at {here}",
            NirBuiltin builtin => $"passed to {builtin.Function} at {here}",
            NirAggregate or NirEnumAggregate or NirArray or NirArrayRepeat => $"stored in an aggregate at {here}",
            _ => $"used as a value at {here}",
        };

        private string Where(TextSpan span) => Source.GetLinePosition(span.Start).ToString();

        private string Name(NirLocal local) => local.Name is { } name ? $"'{symbols.GetName(name)}'" : $"temporary _{local.Id}";
    }
}
