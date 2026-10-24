using System.Collections;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Analysis;

/// <summary>
/// Context effects (type-system.md §32): infers the <c>sleep</c> and <c>alloc</c> effects of every compiled function
/// over the package, then reports each operation whose effect is forbidden where it runs: in an <c>@context</c>
/// function, while an <c>@context(atomic)</c> value is live, or beyond a function's <c>@effects</c> bound. Runs on
/// NIR after drop elaboration, so drops are explicit operations.
/// </summary>
public sealed class EffectChecker
{
    private const int MaxChain = 6;

    /// <summary>Why an operation has an effect: what it is, and the function to follow for the rest of the chain.</summary>
    private sealed record Witness(string Description, HirFunction? Next);

    private sealed class Inferred
    {
        public Effects Effects;
        public Witness? Sleep;
        public Witness? Alloc;
    }

    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<NirFunction> _functions;
    private readonly Dictionary<HirFunction, Inferred> _inferred = [];
    private readonly Dictionary<NovaType, (Effects Effects, HirFunction? Via)> _dropEffects = [];
    private readonly Dictionary<NovaType, bool> _holdsAtomic = [];
    private readonly HashSet<(string Path, int Start, string Message)> _reported = [];

    private EffectChecker(NirProgram program, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
        _functions = [.. program.Functions.Where(f => !f.Source.IsGeneric)];
    }

    public static void Check(NirProgram program, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var checker = new EffectChecker(program, symbols, diagnostics);
        checker.Infer();
        foreach (var function in checker._functions)
        {
            checker.CheckFunction(function);
        }
    }

    // ---- Inference (§32.1) ----

    private void Infer()
    {
        foreach (var function in _functions)
        {
            _inferred[function.Source] = new Inferred();
        }
        // Effects only grow, so iterating to a fixed point terminates (two bits per function).
        for (var changed = true; changed;)
        {
            changed = false;
            _dropEffects.Clear();
            foreach (var function in _functions)
            {
                var inferred = _inferred[function.Source];
                foreach (var statement in function.Blocks.SelectMany(b => b.Statements))
                {
                    foreach (var (effects, witness) in Operations(statement))
                    {
                        if ((effects & ~inferred.Effects) == Effects.None)
                        {
                            continue;
                        }
                        changed = true;
                        if ((effects & Effects.Sleep) != 0 && inferred.Sleep is null)
                        {
                            inferred.Sleep = witness;
                        }
                        if ((effects & Effects.Alloc) != 0 && inferred.Alloc is null)
                        {
                            inferred.Alloc = witness;
                        }
                        inferred.Effects |= effects;
                    }
                }
            }
        }
    }

    /// <summary>The effects of a function as a callee: an extern's declaration, a Nova function's inference.</summary>
    private Effects EffectsOf(HirFunction function) =>
        function.IsExtern ? function.DeclaredEffects ?? (Effects.Sleep | Effects.Alloc)
        : _inferred.TryGetValue(function, out var inferred) ? inferred.Effects
        : Effects.None; // generic code is never compiled; its instantiations are

    private IEnumerable<(Effects Effects, Witness Witness)> Operations(NirStatement statement)
    {
        switch (statement)
        {
            case NirAssign { Value: var value }:
                return Rvalue(value);
            case NirEval { Value: var value }:
                return Rvalue(value);
            case NirDrop drop:
            {
                var (effects, via) = DropEffects(drop.Place.Type);
                return effects == Effects.None ? [] : [(effects, new Witness($"drops a '{drop.Place.Type}'", via))];
            }
            case NirBoxFree free when free.Box.Type.Allocator?.Struct?.DeallocateMethod is { } deallocate:
                return [(EffectsOf(deallocate), new Witness($"frees a '{free.Box.Type}'", deallocate))];
            default:
                return [];
        }
    }

    private IEnumerable<(Effects, Witness)> Rvalue(NirRvalue value)
    {
        switch (value)
        {
            case NirCall call:
            {
                var effects = EffectsOf(call.Callee);
                return effects == Effects.None ? [] : [(effects, new Witness($"calls {Describe(call.Callee)}", call.Callee))];
            }
            case NirBuiltin builtin:
            {
                var (effects, description, via) = BuiltinEffects(builtin);
                return effects == Effects.None ? [] : [(effects, new Witness(description, via))];
            }
            default:
                return [];
        }
    }

    /// <summary>The effects of built-in operations (the table of §32.1).</summary>
    private (Effects, string, HirFunction?) BuiltinEffects(NirBuiltin builtin)
    {
        switch (builtin.Function)
        {
            case BuiltinFunction.BoxCreate:
            {
                var box = builtin.Type.Enum?.ResultTypes?.Ok;
                return box?.Allocator?.Struct?.AllocateMethod is { } allocate
                    ? (EffectsOf(allocate), $"allocates a '{box}'", allocate)
                    : (Effects.Alloc, $"allocates a '{box?.Name ?? "Box"}' from the heap", null);
            }
            case BuiltinFunction.ArenaAlloc or BuiltinFunction.ArenaAllocArray
                when builtin.Arguments[0].Type is { IsReference: true, Pointee.Region: RegionKind.FixedBuffer }:
                return (Effects.None, "", null); // a fixed buffer never asks a heap (§19.4)
            case BuiltinFunction.ArenaCreate or BuiltinFunction.ArenaAlloc or BuiltinFunction.ArenaAllocArray or BuiltinFunction.ScratchEnter
                or BuiltinFunction.PoolCreate or BuiltinFunction.VectorWithCapacity or BuiltinFunction.VectorPush or BuiltinFunction.StringAppend
                or BuiltinFunction.StableVectorPush or BuiltinFunction.SlotMapInsert:
                return (Effects.Alloc, $"may allocate ({Builtin(builtin.Function)})", null);
            case BuiltinFunction.ProcessRun:
                return (Effects.Sleep | Effects.Alloc, "runs a process", null);
            case BuiltinFunction.ConsoleWrite or BuiltinFunction.ConsoleWriteLine or BuiltinFunction.ConsoleRead or BuiltinFunction.ConsoleWriteBytes
                or BuiltinFunction.ConsoleWriteError or BuiltinFunction.ConsoleWriteErrorLine:
                return (Effects.Sleep, "does console I/O", null);
            case BuiltinFunction.FileOpen or BuiltinFunction.FileCreate or BuiltinFunction.FileRemove or BuiltinFunction.ResourceRead
                or BuiltinFunction.ResourceWrite or BuiltinFunction.TcpListen or BuiltinFunction.TcpAccept or BuiltinFunction.TcpConnect
                or BuiltinFunction.TcpShutdownWrite:
                return (Effects.Sleep, "does file or socket I/O", null);
            case BuiltinFunction.CallPointer:
                return (Effects.Sleep | Effects.Alloc, "calls through a C function pointer, which may do anything (type-system.md §32.1)", null);
            default:
                return (Effects.None, "", null);
        }
    }

    private static string Builtin(BuiltinFunction function) => function switch
    {
        BuiltinFunction.ArenaCreate or BuiltinFunction.PoolCreate => "makes a region",
        BuiltinFunction.ArenaAlloc or BuiltinFunction.ArenaAllocArray => "allocates from a region",
        BuiltinFunction.ScratchEnter => "enters a scratch frame",
        _ => "grows a collection",
    };

    /// <summary>The effects of destroying a value of <paramref name="type"/>, and the function that explains them.</summary>
    private (Effects Effects, HirFunction? Via) DropEffects(NovaType type)
    {
        if (_dropEffects.TryGetValue(type, out var known))
        {
            return known;
        }
        _dropEffects[type] = (Effects.None, null); // a recursive type: its own drop adds nothing new
        var effects = Effects.None;
        HirFunction? via = null;
        void Add((Effects Effects, HirFunction? Via) part)
        {
            if ((part.Effects & ~effects) != Effects.None && via is null)
            {
                via = part.Via;
            }
            effects |= part.Effects;
        }
        if (type.Struct is { } definition)
        {
            if (definition.DropFunction is { } drop)
            {
                Add((EffectsOf(drop), drop));
            }
            foreach (var field in definition.Fields)
            {
                Add(DropEffects(field.Type));
            }
        }
        else if (type.Enum is { } enumDefinition)
        {
            foreach (var field in enumDefinition.Variants.SelectMany(v => v.Payload))
            {
                Add(DropEffects(field.Type));
            }
        }
        else if (type.IsBox)
        {
            Add(DropEffects(type.Pointee!));
            if (type.Allocator?.Struct?.DeallocateMethod is { } deallocate)
            {
                Add((EffectsOf(deallocate), deallocate));
            }
        }
        else if (type.Kind is TypeKind.Array or TypeKind.StableVector or TypeKind.SlotMap or TypeKind.Vector
                 && (type.Element ?? type.Pointee?.Element) is { } element)
        {
            Add(DropEffects(element));
        }
        return _dropEffects[type] = (effects, via);
    }

    // ---- Checking (§32.2–§32.3) ----

    private void CheckFunction(NirFunction function)
    {
        var source = function.Source;
        var contextForbids = source.Context switch
        {
            EffectContext.Atomic => Effects.Sleep,
            EffectContext.Irq => Effects.Sleep | Effects.Alloc,
            _ => Effects.None,
        };
        var boundForbids = source.IsExtern || source.DeclaredEffects is not { } declared ? Effects.None : (Effects.Sleep | Effects.Alloc) & ~declared;
        var holders = function.Locals.Where(l => HoldsAtomic(l.Type)).Select(l => l.Id).ToHashSet();
        var liveIn = holders.Count == 0 ? null : LiveHolders(function, holders);
        foreach (var block in function.Blocks)
        {
            var live = liveIn is null ? null : new BitArray(liveIn[block.Id]);
            foreach (var statement in block.Statements)
            {
                var atStatement = live is null ? null : Without(live, Killed(statement, holders));
                var guarded = atStatement is not null && Any(atStatement) ? Effects.Sleep : Effects.None;
                foreach (var (effects, witness) in Operations(statement))
                {
                    Report(function, statement, effects & contextForbids, witness,
                        () => $"{Describe(source)} is @context({(source.Context == EffectContext.Irq ? "irq" : "atomic")})");
                    Report(function, statement, effects & guarded & ~contextForbids, witness,
                        () => $"a '{function.Locals[FirstLive(atStatement!)].Type}' is live here, which makes this an atomic context (type-system.md §32.3)");
                    Report(function, statement, effects & boundForbids, witness,
                        () => $"{Describe(source)} is declared @effects({Names(source.DeclaredEffects ?? Effects.None)})");
                }
                if (live is not null)
                {
                    Transfer(live, statement, holders);
                }
            }
        }
    }

    private void Report(NirFunction function, NirStatement statement, Effects forbidden, Witness witness, Func<string> context)
    {
        if (forbidden == Effects.None)
        {
            return;
        }
        var effect = (forbidden & Effects.Sleep) != 0 ? Effects.Sleep : Effects.Alloc;
        var message = $"{context()}, but this {(effect == Effects.Sleep ? "may sleep" : "may allocate")}: it {Explain(witness, effect)}";
        var sourceText = function.Source.Source;
        if (_reported.Add((sourceText.Path, statement.Span.Start, message)))
        {
            _diagnostics.Error(DiagnosticCodes.EffectViolation, sourceText, statement.Span, message);
        }
    }

    /// <summary>The chain of calls from an operation to where the effect comes from.</summary>
    private string Explain(Witness witness, Effects effect)
    {
        var text = witness.Description;
        var seen = new HashSet<HirFunction>();
        for (var (next, depth) = (witness.Next, 0); next is not null && seen.Add(next); depth++)
        {
            if (next.IsExtern)
            {
                text += next.DeclaredEffects is { } declared ? $", declared @effects({Names(declared)})" : ", a C function without @effects, which may do anything";
                break;
            }
            if (!_inferred.TryGetValue(next, out var inferred) || (effect == Effects.Sleep ? inferred.Sleep : inferred.Alloc) is not { } step)
            {
                break;
            }
            if (depth == MaxChain)
            {
                text += ", …";
                break;
            }
            text += $", which {step.Description}";
            next = step.Next;
        }
        return text;
    }

    private string Describe(HirFunction function) =>
        function.Owner is { } owner ? $"'{owner}.{_symbols.GetName(function.Name)}'" : $"'{_symbols.GetName(function.Name)}'";

    private static string Names(Effects effects) => string.Join(", ",
        new[] { (Effects.Sleep, "sleep"), (Effects.Alloc, "alloc") }.Where(e => (effects & e.Item1) != 0).Select(e => e.Item2));

    // ---- Where an atomic-context value is live (§32.3) ----

    /// <summary>Does a value of this type hold an <c>@context(atomic)</c> struct by value (or in a box it owns)?</summary>
    private bool HoldsAtomic(NovaType type)
    {
        if (_holdsAtomic.TryGetValue(type, out var known))
        {
            return known;
        }
        _holdsAtomic[type] = false;
        var holds = type.Struct is { } definition ? definition.IsAtomicContext || definition.Fields.Any(f => HoldsAtomic(f.Type))
            : type.Enum is { } enumDefinition ? enumDefinition.Variants.SelectMany(v => v.Payload).Any(f => HoldsAtomic(f.Type))
            : type.IsBox ? HoldsAtomic(type.Pointee!)
            : type.Kind == TypeKind.Array && HoldsAtomic(type.Element!);
        return _holdsAtomic[type] = holds;
    }

    /// <summary>For each block, the holder locals that may be live on entry (a forward, may analysis).</summary>
    private static Dictionary<int, BitArray> LiveHolders(NirFunction function, HashSet<int> holders)
    {
        var count = function.Locals.Count;
        var liveIn = function.Blocks.ToDictionary(b => b.Id, _ => new BitArray(count));
        for (var p = 0; p < function.ParameterCount; p++)
        {
            if (holders.Contains(p))
            {
                liveIn[function.Blocks[0].Id][p] = true;
            }
        }
        var work = new Queue<NirBlock>(function.Blocks);
        var queued = function.Blocks.Select(b => b.Id).ToHashSet();
        while (work.TryDequeue(out var block))
        {
            queued.Remove(block.Id);
            var live = new BitArray(liveIn[block.Id]);
            foreach (var statement in block.Statements)
            {
                Transfer(live, statement, holders);
            }
            foreach (var successor in Successors(block.Terminator))
            {
                var target = liveIn[successor.Id];
                var before = new BitArray(target);
                target.Or(live);
                if (!Same(before, target) && queued.Add(successor.Id))
                {
                    work.Enqueue(successor);
                }
            }
        }
        return liveIn;
    }

    private static void Transfer(BitArray live, NirStatement statement, HashSet<int> holders)
    {
        foreach (var id in Killed(statement, holders))
        {
            live[id] = false;
        }
        if (statement is NirAssign { Target.Projections.IsEmpty: true, Target.Local.Id: var target } && holders.Contains(target))
        {
            live[target] = true;
        }
    }

    /// <summary>Holder locals a statement ends: moved away, dropped, or whose storage ends.</summary>
    private static IEnumerable<int> Killed(NirStatement statement, HashSet<int> holders)
    {
        IEnumerable<int> ids = statement switch
        {
            NirDrop { Place.Projections.IsEmpty: true } drop => [drop.Place.Local.Id],
            NirStorageDead dead => [dead.Local.Id],
            NirAssign assign => Moved(assign.Value),
            NirEval eval => Moved(eval.Value),
            _ => [],
        };
        return ids.Where(holders.Contains);
    }

    private static IEnumerable<int> Moved(NirRvalue value)
    {
        IEnumerable<NirOperand> operands = value switch
        {
            NirUse use => [use.Operand],
            NirCall call => call.Arguments,
            NirBuiltin builtin => builtin.Arguments,
            NirAggregate aggregate => aggregate.Fields,
            NirEnumAggregate aggregate => aggregate.Payload,
            NirArray array => array.Elements,
            _ => [],
        };
        return operands.OfType<NirPlaceOperand>().Where(o => o.IsMove && o.Place.Projections.IsEmpty).Select(o => o.Place.Local.Id);
    }

    private static IEnumerable<NirBlock> Successors(NirTerminator? terminator) => terminator switch
    {
        NirGoto g => [g.Target],
        NirBranch b => [b.Then, b.Else],
        NirSwitch s => [.. s.Cases.Select(c => c.Target), s.Otherwise],
        _ => [],
    };

    private static BitArray Without(BitArray live, IEnumerable<int> ids)
    {
        var result = new BitArray(live);
        foreach (var id in ids)
        {
            result[id] = false;
        }
        return result;
    }

    private static bool Any(BitArray bits)
    {
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                return true;
            }
        }
        return false;
    }

    private static int FirstLive(BitArray bits)
    {
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                return i;
            }
        }
        return -1;
    }

    private static bool Same(BitArray a, BitArray b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }
}
