using System.Collections;
using System.Collections.Immutable;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;

namespace Nova.Compiler.Analysis;

/// <summary>
/// A node of a local's move-path tree: the local itself, or a field path without derefs
/// (intermediate-representation.md, M2c). When one field of a struct is tracked, all of its
/// siblings are too, so a node's children partition it. Init state is kept per leaf.
/// </summary>
internal sealed class MovePath
{
    public MovePath(NirLocal local, MovePath? parent, StructField? field, NovaType type)
    {
        Local = local;
        Parent = parent;
        Field = field;
        Type = type;
    }

    public NirLocal Local { get; }

    public MovePath? Parent { get; }

    /// <summary>The field this node selects from its parent; null for a local's root.</summary>
    public StructField? Field { get; }

    public NovaType Type { get; }

    public List<MovePath> Children { get; } = [];

    /// <summary>Bit index of a leaf; -1 for inner nodes.</summary>
    public int LeafIndex { get; set; } = -1;

    public bool IsLeaf => Children.Count == 0;

    public NirPlace Place()
    {
        var fields = new List<StructField>();
        for (var node = this; node.Field is not null; node = node.Parent!)
        {
            fields.Insert(0, node.Field);
        }
        return new NirPlace(Local, [.. fields.Select(f => new Projection(f))]);
    }

    public IEnumerable<MovePath> Leaves() => IsLeaf ? [this] : Children.SelectMany(c => c.Leaves());

    public IEnumerable<MovePath> Ancestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
        {
            yield return node;
        }
    }
}

/// <summary>Init state at a program point: three sets of leaves (all "maybe", joined by union).</summary>
internal sealed class InitState
{
    public InitState(int leaves)
    {
        MaybeInit = new BitArray(leaves);
        MaybeUninit = new BitArray(leaves);
        MaybeMoved = new BitArray(leaves);
    }

    private InitState(InitState other)
    {
        MaybeInit = new BitArray(other.MaybeInit);
        MaybeUninit = new BitArray(other.MaybeUninit);
        MaybeMoved = new BitArray(other.MaybeMoved);
    }

    public BitArray MaybeInit { get; }

    public BitArray MaybeUninit { get; }

    /// <summary>Leaves that may be uninitialised because of a move (N0250 rather than N0212).</summary>
    public BitArray MaybeMoved { get; }

    public InitState Clone() => new(this);

    public bool JoinFrom(InitState other)
    {
        var changed = false;
        changed |= Or(MaybeInit, other.MaybeInit);
        changed |= Or(MaybeUninit, other.MaybeUninit);
        changed |= Or(MaybeMoved, other.MaybeMoved);
        return changed;
    }

    private static bool Or(BitArray target, BitArray source)
    {
        var changed = false;
        for (var i = 0; i < target.Length; i++)
        {
            if (source[i] && !target[i])
            {
                target[i] = true;
                changed = true;
            }
        }
        return changed;
    }

    public bool DefinitelyInit(MovePath path) => path.Leaves().All(l => !MaybeUninit[l.LeafIndex]);

    public bool DefinitelyUninit(MovePath path) => path.Leaves().All(l => !MaybeInit[l.LeafIndex]);

    public bool AnyMaybeMoved(MovePath path) => path.Leaves().Any(l => MaybeUninit[l.LeafIndex] && MaybeMoved[l.LeafIndex]);

    public void Initialize(MovePath path)
    {
        foreach (var leaf in path.Leaves())
        {
            MaybeInit[leaf.LeafIndex] = true;
            MaybeUninit[leaf.LeafIndex] = false;
            MaybeMoved[leaf.LeafIndex] = false;
        }
    }

    public void Uninitialize(MovePath path, bool moved)
    {
        foreach (var leaf in path.Leaves())
        {
            MaybeInit[leaf.LeafIndex] = false;
            MaybeUninit[leaf.LeafIndex] = true;
            MaybeMoved[leaf.LeafIndex] = moved;
        }
    }
}

/// <summary>
/// Move paths and the forward init/move dataflow of one NIR function, shared by
/// <see cref="MoveChecker"/> and <see cref="DropElaborator"/>.
/// </summary>
internal sealed class MoveData
{
    private readonly Dictionary<NirLocal, MovePath> _roots = [];
    private readonly List<MovePath> _leaves = [];

    public MoveData(NirFunction function)
    {
        Function = function;
        foreach (var local in function.Locals)
        {
            _roots[local] = new MovePath(local, null, null, local.Type);
        }
        foreach (var block in function.Blocks)
        {
            foreach (var statement in block.Statements)
            {
                foreach (var place in TrackedPlaces(statement))
                {
                    Expand(place);
                }
            }
        }
        foreach (var root in _roots.Values)
        {
            foreach (var leaf in root.Leaves())
            {
                leaf.LeafIndex = _leaves.Count;
                _leaves.Add(leaf);
            }
        }
        Compute();
    }

    public NirFunction Function { get; }

    public int LeafCount => _leaves.Count;

    /// <summary>State before each statement of each block; index Count is the state before the terminator.</summary>
    public Dictionary<NirBlock, List<InitState>> Before { get; } = [];

    public MovePath Root(NirLocal local) => _roots[local];

    /// <summary>False for locals added after the analysis (temporaries and flags of drop elaboration).</summary>
    public bool Tracks(NirLocal local) => _roots.ContainsKey(local);

    /// <summary>The deepest tracked node on the place's field prefix (stopping at the first Deref).</summary>
    public MovePath PathFor(NirPlace place)
    {
        var node = _roots[place.Local];
        foreach (var projection in place.Projections)
        {
            if (!projection.IsField || node.IsLeaf)
            {
                break;
            }
            node = node.Children.First(c => c.Field == projection.Field);
        }
        return node;
    }

    /// <summary>Places whose partial state matters: assignment targets and moved operands, without derefs.</summary>
    private static IEnumerable<NirPlace> TrackedPlaces(NirStatement statement) => statement switch
    {
        NirAssign a => MovedPlaces(a.Value).Append(a.Target),
        NirEval e => MovedPlaces(e.Value),
        _ => [],
    };

    public static IEnumerable<NirPlace> MovedPlaces(NirRvalue rvalue) => Operands(rvalue)
        .OfType<NirPlaceOperand>().Where(o => o.IsMove).Select(o => o.Place);

    public static IEnumerable<NirOperand> Operands(NirRvalue rvalue) => rvalue switch
    {
        NirUse u => [u.Operand],
        NirBinary b => [b.Left, b.Right],
        NirUnary u => [u.Operand],
        NirCast c => [c.Operand],
        NirTruncate t => [t.Operand],
        NirAggregate a => a.Fields,
        NirEnumAggregate e => e.Payload,
        NirArray a => a.Elements,
        NirArrayRepeat a => [a.Value],
        NirSlice s => [s.Low, s.High],
        NirCall c => c.Arguments,
        NirBuiltin b => b.Arguments,
        _ => [],
    };

    private void Expand(NirPlace place)
    {
        var node = _roots[place.Local];
        foreach (var projection in place.Projections)
        {
            if (!projection.IsField)
            {
                return;
            }
            if (node.IsLeaf)
            {
                foreach (var field in node.Type.Struct!.Fields)
                {
                    node.Children.Add(new MovePath(node.Local, node, field, field.Type));
                }
            }
            node = node.Children.First(c => c.Field == projection.Field);
        }
    }

    // ---- Transfer functions ----

    /// <summary>Applies the moves of an rvalue's operands.</summary>
    public void ApplyMoves(NirRvalue rvalue, InitState state)
    {
        foreach (var place in MovedPlaces(rvalue).Where(p => p.IsFieldPath))
        {
            state.Uninitialize(PathFor(place), moved: true);
        }
    }

    public void Apply(NirStatement statement, InitState state)
    {
        switch (statement)
        {
            case NirAssign assign:
                ApplyMoves(assign.Value, state);
                if (assign.Target.IsFieldPath)
                {
                    state.Initialize(PathFor(assign.Target));
                }
                break;
            case NirEval eval:
                ApplyMoves(eval.Value, state);
                break;
            case NirStorageDead dead:
                state.Uninitialize(_roots[dead.Local], moved: false);
                break;
            case NirDrop drop:
                state.Uninitialize(PathFor(drop.Place), moved: false);
                break;
        }
    }

    private void Compute()
    {
        var entry = new InitState(LeafCount);
        foreach (var root in _roots.Values)
        {
            if (root.Local.Id < Function.ParameterCount)
            {
                entry.Initialize(root);
            }
            else
            {
                entry.Uninitialize(root, moved: false);
            }
        }

        var blockIn = new Dictionary<NirBlock, InitState> { [Function.Blocks[0]] = entry };
        var work = new Queue<NirBlock>([Function.Blocks[0]]);
        var queued = new HashSet<NirBlock> { Function.Blocks[0] };
        while (work.TryDequeue(out var block))
        {
            queued.Remove(block);
            var state = blockIn[block].Clone();
            var positions = new List<InitState>(block.Statements.Count + 1);
            foreach (var statement in block.Statements)
            {
                positions.Add(state.Clone());
                Apply(statement, state);
            }
            positions.Add(state.Clone());
            Before[block] = positions;

            var successors = block.Terminator!.Successors;
            foreach (var successor in successors)
            {
                if (!blockIn.TryGetValue(successor, out var existing))
                {
                    blockIn[successor] = state.Clone();
                }
                else if (!existing.JoinFrom(state))
                {
                    continue;
                }
                if (queued.Add(successor))
                {
                    work.Enqueue(successor);
                }
            }
        }
    }

    public ImmutableArray<MovePath> Leaves => [.. _leaves];
}
