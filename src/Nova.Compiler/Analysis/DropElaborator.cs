using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;

namespace Nova.Compiler.Analysis;

/// <summary>
/// Drop elaboration (type-system.md §14.5; ownership.md O4). Runs after move and borrow checking
/// succeeded. It inserts <see cref="NirDrop"/> where values that need drop end:
/// <list type="bullet">
/// <item>before a <c>StorageDead</c> of such a local (scope end, break/continue, return, temporaries);</item>
/// <item>before an assignment that overwrites such a place. The assignment is split as
/// <c>t = rvalue; Drop(place); place = t</c>, so the new value is computed before the old one is destroyed.</item>
/// </list>
/// What is dropped follows the init state at that point:
/// <list type="bullet">
/// <item>definitely initialised: dropped unconditionally;</item>
/// <item>definitely uninitialised: not dropped;</item>
/// <item>otherwise: guarded by a runtime drop flag, or dropped field by field for a partially moved struct.</item>
/// </list>
/// </summary>
internal sealed class DropElaborator
{
    private readonly NirFunction _function;
    private readonly TypeTable _types;
    private readonly MoveData _data;
    private readonly Dictionary<MovePath, NirLocal> _flags = [];

    private DropElaborator(NirFunction function, TypeTable types)
    {
        _function = function;
        _types = types;
        _data = new MoveData(function);
    }

    public static void Elaborate(NirFunction function, TypeTable types) => new DropElaborator(function, types).Run();

    private void Run()
    {
        // Pass 1: decide what each drop point destroys (this may allocate drop flags).
        var before = new Dictionary<(NirBlock, int), List<NirStatement>>();
        var replace = new Dictionary<(NirBlock, int), List<NirStatement>>();
        foreach (var block in _function.Blocks)
        {
            for (var i = 0; i < block.Statements.Count; i++)
            {
                var state = _data.Before[block][i];
                var statement = block.Statements[i];
                switch (statement)
                {
                    case NirStorageDead dead when TypeTable.NeedsDrop(dead.Local.Type):
                        before[(block, i)] = Sequence(_data.Root(dead.Local), state, statement);
                        break;
                    case NirAssign assign when TypeTable.NeedsDrop(assign.Target.Type):
                    {
                        var afterValue = state.Clone();
                        _data.ApplyMoves(assign.Value, afterValue);
                        var drops = assign.Target.Projections.Any(p => p.IsDeref)
                            ? [new NirDrop(assign.Target, null) { Span = statement.Span }] // a referent is always initialised
                            : Sequence(_data.PathFor(assign.Target), afterValue, statement);
                        if (drops.Count > 0)
                        {
                            var temp = _function.AddLocal(assign.Target.Type);
                            replace[(block, i)] =
                            [
                                new NirAssign(new NirPlace(temp), assign.Value) { Span = statement.Span },
                                .. drops,
                                new NirAssign(assign.Target, new NirUse(new NirPlaceOperand(new NirPlace(temp), IsMove: true))) { Span = statement.Span },
                            ];
                        }
                        break;
                    }
                }
            }
        }

        // Pass 2: rebuild the blocks, keeping every drop flag equal to its leaf's runtime init state.
        foreach (var block in _function.Blocks)
        {
            var rebuilt = new List<NirStatement>();
            if (block == _function.Blocks[0])
            {
                foreach (var (leaf, flag) in _flags.Where(f => f.Key.Local.Id < _function.ParameterCount))
                {
                    rebuilt.Add(SetFlag(flag, true, default));
                }
            }
            for (var i = 0; i < block.Statements.Count; i++)
            {
                var statement = block.Statements[i];
                if (before.TryGetValue((block, i), out var drops))
                {
                    rebuilt.AddRange(drops);
                }
                rebuilt.AddRange(replace.TryGetValue((block, i), out var replacement) ? replacement : [statement]);
                rebuilt.AddRange(FlagUpdates(statement));
            }
            block.Statements.Clear();
            block.Statements.AddRange(rebuilt);
        }
    }

    /// <summary>The drops that destroy what is still initialised under <paramref name="path"/>.</summary>
    private List<NirStatement> Sequence(MovePath path, InitState state, NirStatement at)
    {
        if (!TypeTable.NeedsDrop(path.Type) || state.DefinitelyUninit(path))
        {
            return [];
        }
        if (state.DefinitelyInit(path))
        {
            return [new NirDrop(path.Place(), null) { Span = at.Span }];
        }
        // A struct with a drop block is never partially moved (N0252): its leaves move together.
        if (path.IsLeaf || path.Type.Struct?.DropFunction is not null)
        {
            return [new NirDrop(path.Place(), FlagFor(path.Leaves().First())) { Span = at.Span }];
        }
        return [.. path.Children.SelectMany(child => Sequence(child, state, at))];
    }

    private NirLocal FlagFor(MovePath leaf)
    {
        if (!_flags.TryGetValue(leaf, out var flag))
        {
            // One flag per leaf; every leaf under a drop-block struct gets one, so they stay in step.
            foreach (var sibling in (leaf.Ancestors().LastOrDefault(a => a.Type.Struct?.DropFunction is not null) ?? leaf).Leaves())
            {
                _flags.TryAdd(sibling, _function.AddLocal(_types.Bool));
            }
            flag = _flags[leaf];
        }
        return flag;
    }

    private static NirAssign SetFlag(NirLocal flag, bool value, Syntax.TextSpan span) =>
        new(new NirPlace(flag), new NirUse(new NirConst(value ? 1 : 0, flag.Type))) { Span = span };

    private IEnumerable<NirStatement> FlagUpdates(NirStatement statement)
    {
        if (_flags.Count == 0)
        {
            yield break;
        }
        var updates = new Dictionary<NirLocal, bool>();
        var rvalue = statement switch
        {
            NirAssign a => a.Value,
            NirEval e => e.Value,
            _ => null,
        };
        if (rvalue is not null)
        {
            foreach (var moved in MoveData.MovedPlaces(rvalue).Where(p => !p.Projections.Any(x => x.IsDeref)))
            {
                foreach (var leaf in _data.PathFor(moved).Leaves().Where(_flags.ContainsKey))
                {
                    updates[_flags[leaf]] = false;
                }
            }
        }
        if (statement is NirAssign assign && !assign.Target.Projections.Any(p => p.IsDeref) && _data.Tracks(assign.Target.Local))
        {
            foreach (var leaf in _data.PathFor(assign.Target).Leaves().Where(_flags.ContainsKey))
            {
                updates[_flags[leaf]] = true;
            }
        }
        if (statement is NirStorageDead dead)
        {
            foreach (var leaf in _data.Root(dead.Local).Leaves().Where(_flags.ContainsKey))
            {
                updates[_flags[leaf]] = false;
            }
        }
        foreach (var (flag, value) in updates)
        {
            yield return SetFlag(flag, value, statement.Span);
        }
    }
}
