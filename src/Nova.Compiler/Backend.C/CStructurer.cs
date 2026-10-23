using Nova.Compiler.Nir;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Backend.C;

// Structured C (c-backend.md §3): a statement tree built from the NIR graph. Every block's code
// ends in an explicit transfer, so placement only affects readability; transfers are removed or
// turned into break/continue only where falling through provably reaches the same label.

internal abstract class CNode
{
    public TextSpan Span { get; init; }
}

/// <summary>Lines of one NIR statement or terminator. <see cref="Completes"/> is false for return, panic, exit.</summary>
internal sealed class CCode(IReadOnlyList<string> lines, bool completes) : CNode
{
    public IReadOnlyList<string> Lines { get; } = lines;

    public bool Completes { get; } = completes;
}

internal sealed class CLabel(int block) : CNode
{
    public int Block { get; } = block;
}

internal sealed class CGoto(int block) : CNode
{
    public int Block { get; } = block;
}

internal sealed class CBreak : CNode;

internal sealed class CContinue : CNode;

internal sealed class CIf(string condition, List<CNode> then, List<CNode> otherwise) : CNode
{
    public string Condition { get; set; } = condition;

    public List<CNode> Then { get; set; } = then;

    public List<CNode> Else { get; set; } = otherwise;
}

internal sealed class CLoop(int header, List<CNode> body) : CNode
{
    public int Header { get; } = header;

    public List<CNode> Body { get; } = body;
}

internal sealed class CSwitch(string value, List<(List<string> Labels, List<CNode> Body)> cases) : CNode
{
    public string Value { get; } = value;

    public List<(List<string> Labels, List<CNode> Body)> Cases { get; } = cases;
}

/// <summary>Dominators, loops and the placement of each block (c-backend.md §3, steps 1–2).</summary>
internal sealed class ControlFlow
{
    private const int InlineExitBlocks = 2;
    private const int InlineExitStatements = 8;

    private readonly Dictionary<NirBlock, int> _order = [];
    private readonly Dictionary<NirBlock, NirBlock> _idom = [];
    private readonly Dictionary<NirBlock, List<NirBlock>> _predecessors = [];
    private readonly Dictionary<NirBlock, HashSet<NirBlock>> _loops = [];
    private readonly Dictionary<NirBlock, int> _forwardPredecessors = [];
    private readonly Dictionary<NirBlock, List<NirBlock>> _children = [];

    public ControlFlow(NirFunction function)
    {
        Entry = function.Blocks[0];
        ReversePostorder = ComputeReversePostorder(Entry);
        for (var i = 0; i < ReversePostorder.Count; i++)
        {
            _order[ReversePostorder[i]] = i;
            _predecessors[ReversePostorder[i]] = [];
        }
        foreach (var block in ReversePostorder)
        {
            foreach (var successor in block.Terminator!.Successors)
            {
                _predecessors[successor].Add(block);
            }
        }
        ComputeDominators();

        IsReducible = true;
        foreach (var block in ReversePostorder)
        {
            _forwardPredecessors[block] = 0;
            _children[block] = [];
        }
        foreach (var block in ReversePostorder)
        {
            if (block != Entry)
            {
                _children[_idom[block]].Add(block);
            }
            foreach (var successor in block.Terminator!.Successors)
            {
                if (Dominates(successor, block))
                {
                    AddToLoop(successor, block);
                }
                else if (_order[successor] <= _order[block])
                {
                    IsReducible = false; // a retreating edge that is not a back edge
                }
                else
                {
                    _forwardPredecessors[successor]++;
                }
            }
        }
    }

    public NirBlock Entry { get; }

    public IReadOnlyList<NirBlock> ReversePostorder { get; }

    public bool IsReducible { get; }

    public bool IsLoopHeader(NirBlock block) => _loops.ContainsKey(block);

    public IReadOnlyList<NirBlock> PredecessorsOf(NirBlock block) => _predecessors[block];

    /// <summary>Whether the block belongs to a loop (an irreducible graph counts every block as in one).</summary>
    public bool InLoop(NirBlock block) => !IsReducible || _loops.Values.Any(members => members.Contains(block));

    public bool IsBackEdge(NirBlock from, NirBlock to) => Dominates(to, from);

    public bool Dominates(NirBlock a, NirBlock b)
    {
        for (var x = b; ; x = _idom[x])
        {
            if (x == a)
            {
                return true;
            }
            if (x == Entry)
            {
                return false;
            }
        }
    }

    public enum Placement { Inline, Follow, ExitFollow }

    /// <summary>Where a block (not the entry) is emitted, and next to which block (§3 step 2).</summary>
    public (Placement Kind, NirBlock Owner) PlacementOf(NirBlock block)
    {
        var d = _idom[block];
        NirBlock? exited = null;
        foreach (var (header, members) in _loops)
        {
            // The outermost such loop is the largest one: loops are nested or disjoint.
            if (members.Contains(d) && !members.Contains(block) && (exited is null || _loops[exited].Count < members.Count))
            {
                exited = header;
            }
        }
        var single = _forwardPredecessors[block] == 1;
        if (exited is not null)
        {
            return single && d != exited && IsSmallClosedSubtree(block) ? (Placement.Inline, d) : (Placement.ExitFollow, exited);
        }
        return single ? (Placement.Inline, d) : (Placement.Follow, d);
    }

    private bool IsSmallClosedSubtree(NirBlock root)
    {
        var subtree = new HashSet<NirBlock>();
        var stack = new Stack<NirBlock>([root]);
        while (stack.Count > 0)
        {
            var block = stack.Pop();
            subtree.Add(block);
            foreach (var child in _children[block])
            {
                stack.Push(child);
            }
        }
        return subtree.Count <= InlineExitBlocks
            && subtree.Sum(b => b.Statements.Count) <= InlineExitStatements
            && subtree.All(b => b.Terminator!.Successors.All(subtree.Contains));
    }

    private static List<NirBlock> ComputeReversePostorder(NirBlock entry)
    {
        var postorder = new List<NirBlock>();
        var visited = new HashSet<NirBlock> { entry };
        var stack = new Stack<(NirBlock Block, IEnumerator<NirBlock> Successors)>();
        stack.Push((entry, entry.Terminator!.Successors.GetEnumerator()));
        while (stack.Count > 0)
        {
            var (block, successors) = stack.Peek();
            if (successors.MoveNext())
            {
                if (visited.Add(successors.Current))
                {
                    stack.Push((successors.Current, successors.Current.Terminator!.Successors.GetEnumerator()));
                }
            }
            else
            {
                stack.Pop();
                postorder.Add(block);
            }
        }
        postorder.Reverse();
        return postorder;
    }

    /// <summary>Cooper, Harvey and Kennedy, "A Simple, Fast Dominance Algorithm".</summary>
    private void ComputeDominators()
    {
        _idom[Entry] = Entry;
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var block in ReversePostorder.Skip(1))
            {
                NirBlock? candidate = null;
                foreach (var predecessor in _predecessors[block])
                {
                    if (!_idom.ContainsKey(predecessor))
                    {
                        continue;
                    }
                    candidate = candidate is null ? predecessor : Intersect(predecessor, candidate);
                }
                if (candidate is not null && (!_idom.TryGetValue(block, out var old) || old != candidate))
                {
                    _idom[block] = candidate;
                    changed = true;
                }
            }
        }
    }

    private NirBlock Intersect(NirBlock a, NirBlock b)
    {
        while (a != b)
        {
            while (_order[a] > _order[b])
            {
                a = _idom[a];
            }
            while (_order[b] > _order[a])
            {
                b = _idom[b];
            }
        }
        return a;
    }

    private void AddToLoop(NirBlock header, NirBlock source)
    {
        if (!_loops.TryGetValue(header, out var members))
        {
            _loops[header] = members = [header];
        }
        var stack = new Stack<NirBlock>();
        if (members.Add(source))
        {
            stack.Push(source);
        }
        while (stack.Count > 0)
        {
            foreach (var predecessor in _predecessors[stack.Pop()])
            {
                if (members.Add(predecessor))
                {
                    stack.Push(predecessor);
                }
            }
        }
    }
}

/// <summary>Builds and simplifies the statement tree of one function (c-backend.md §3, steps 2–6).</summary>
internal sealed class CStructurer
{
    private readonly ControlFlow _flow;
    private readonly Func<NirBlock, List<CNode>> _statements;
    private readonly Func<NirBlock, NirTerminator, CNode> _exit;
    private readonly Func<NirOperand, string> _operand;
    private readonly Func<NirSwitch, (string Value, IEnumerable<(string Label, NirBlock Target)> Cases)> _cases;
    private readonly Dictionary<NirBlock, List<NirBlock>> _follows = [];
    private readonly Dictionary<NirBlock, List<NirBlock>> _exitFollows = [];

    /// <param name="statements">The code of a block's statements.</param>
    /// <param name="exit">The code of a return, exit, panic or unreachable terminator.</param>
    /// <param name="operand">A C operand.</param>
    /// <param name="cases">A switch's C value, and its case labels and targets in order (the default last, labelled <c>default</c>).</param>
    private CStructurer(ControlFlow flow, Func<NirBlock, List<CNode>> statements, Func<NirBlock, NirTerminator, CNode> exit,
        Func<NirOperand, string> operand, Func<NirSwitch, (string Value, IEnumerable<(string Label, NirBlock Target)> Cases)> cases)
    {
        _flow = flow;
        _statements = statements;
        _exit = exit;
        _operand = operand;
        _cases = cases;
    }

    /// <summary>The function body as a statement tree, or null if its graph is irreducible (emit it flat).</summary>
    public static List<CNode>? Structure(NirFunction function, Func<NirBlock, List<CNode>> statements, Func<NirBlock, NirTerminator, CNode> exit,
        Func<NirOperand, string> operand, Func<NirSwitch, (string Value, IEnumerable<(string Label, NirBlock Target)> Cases)> cases)
    {
        var flow = new ControlFlow(function);
        if (!flow.IsReducible)
        {
            return null;
        }
        var structurer = new CStructurer(flow, statements, exit, operand, cases);
        foreach (var block in flow.ReversePostorder)
        {
            structurer._follows[block] = [];
            structurer._exitFollows[block] = [];
        }
        foreach (var block in flow.ReversePostorder.Skip(1))
        {
            switch (flow.PlacementOf(block))
            {
                case (ControlFlow.Placement.Follow, var owner):
                    structurer._follows[owner].Add(block);
                    break;
                case (ControlFlow.Placement.ExitFollow, var owner):
                    structurer._exitFollows[owner].Add(block);
                    break;
            }
        }

        var tree = structurer.Tree(flow.Entry);
        // To a fixpoint: removing a goto can extend what falls through before it (an empty exit block).
        while (Eliminate(tree, Fall.None, Context.Root))
        {
        }
        var targets = new HashSet<int>();
        CollectGotos(tree, targets);
        RemoveLabels(tree, targets); // before hoisting: an arm holding only a dead label is empty
        Hoist(tree);
        return tree;
    }

    // ---- Building (steps 2 and 3) ----

    private List<CNode> Tree(NirBlock block)
    {
        var list = new List<CNode> { new CLabel(block.Id) };
        if (_flow.IsLoopHeader(block))
        {
            list.Add(new CLoop(block.Id, Body(block)));
            foreach (var exit in _exitFollows[block])
            {
                list.AddRange(Tree(exit));
            }
        }
        else
        {
            list.AddRange(Body(block));
        }
        return list;
    }

    private List<CNode> Body(NirBlock block)
    {
        var list = _statements(block);
        var terminator = block.Terminator!;
        switch (terminator)
        {
            case NirGoto g:
                list.AddRange(Branch(block, g.Target, terminator.Span));
                break;
            case NirBranch b:
                list.Add(new CIf(_operand(b.Condition), Branch(block, b.Then, terminator.Span), Branch(block, b.Else, terminator.Span))
                {
                    Span = terminator.Span,
                });
                break;
            case NirSwitch s:
            {
                var cases = new List<(List<string> Labels, List<CNode> Body)>();
                var (value, targets) = _cases(s);
                foreach (var group in targets.GroupBy(c => c.Target))
                {
                    // Cases with one target share a body; the default stays last.
                    cases.Add(([.. group.Select(c => c.Label)], Branch(block, group.Key, terminator.Span)));
                }
                var defaultIndex = cases.FindIndex(c => c.Labels.Contains("default"));
                var withDefault = cases[defaultIndex];
                cases.RemoveAt(defaultIndex);
                withDefault.Labels.Sort((a, b) => (a == "default").CompareTo(b == "default"));
                cases.Add(withDefault);
                list.Add(new CSwitch(value, cases) { Span = terminator.Span });
                break;
            }
            default:
                list.Add(_exit(block, terminator));
                break;
        }
        foreach (var follow in _follows[block])
        {
            list.AddRange(Tree(follow));
        }
        return list;
    }

    private List<CNode> Branch(NirBlock from, NirBlock to, TextSpan span) =>
        !_flow.IsBackEdge(from, to) && to != _flow.Entry && _flow.PlacementOf(to) is (ControlFlow.Placement.Inline, var owner) && owner == from
            ? Tree(to)
            : [new CGoto(to.Id) { Span = span }];

    // ---- Transfer elimination (step 4) ----

    /// <summary>What falling off the end of a statement list reaches: labels passed over without executing code.</summary>
    private sealed record Fall(IReadOnlySet<int> Labels)
    {
        public static readonly Fall None = new(new HashSet<int>());
    }

    /// <param name="Header">The innermost loop's header, or -1.</param>
    /// <param name="AfterLoop">What falls through after the innermost loop.</param>
    /// <param name="InSwitch">A switch lies between here and the innermost loop.</param>
    /// <param name="AfterSwitch">What falls through after the innermost switch.</param>
    private sealed record Context(int Header, Fall AfterLoop, bool InSwitch, Fall AfterSwitch)
    {
        public static readonly Context Root = new(-1, Fall.None, false, Fall.None);
    }

    private static Fall FallAfter(List<CNode> list, int index, Fall end)
    {
        var labels = new HashSet<int>();
        for (var i = index + 1; i < list.Count; i++)
        {
            if (list[i] is not CLabel label)
            {
                return new Fall(labels);
            }
            labels.Add(label.Block);
        }
        labels.UnionWith(end.Labels);
        return new Fall(labels);
    }

    /// <returns>Whether anything changed.</returns>
    private static bool Eliminate(List<CNode> list, Fall end, Context context)
    {
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var next = FallAfter(list, i, end);
            switch (list[i])
            {
                case CGoto g when next.Labels.Contains(g.Block):
                    list.RemoveAt(i--);
                    changed = true;
                    break;
                case CGoto g when g.Block == context.Header:
                    list[i] = new CContinue { Span = g.Span };
                    changed = true;
                    break;
                case CGoto g when !context.InSwitch && context.Header >= 0 && context.AfterLoop.Labels.Contains(g.Block):
                    list[i] = new CBreak { Span = g.Span };
                    changed = true;
                    break;
                case CGoto g when context.InSwitch && context.AfterSwitch.Labels.Contains(g.Block):
                    list[i] = new CBreak { Span = g.Span };
                    changed = true;
                    break;
                case CIf branch:
                    changed |= Eliminate(branch.Then, next, context);
                    changed |= Eliminate(branch.Else, next, context);
                    break;
                case CLoop loop:
                    // Falling off a loop body goes back to its header.
                    changed |= Eliminate(loop.Body, new Fall(new HashSet<int> { loop.Header }), new Context(loop.Header, next, false, Fall.None));
                    break;
                case CSwitch choice:
                    foreach (var (_, body) in choice.Cases)
                    {
                        // A case body never falls into the next case (-Wimplicit-fallthrough).
                        changed |= Eliminate(body, Fall.None, context with { InSwitch = true, AfterSwitch = next });
                    }
                    break;
            }
        }
        return changed;
    }

    // ---- Guard clauses (step 5) ----

    private static void Hoist(List<CNode> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case CIf branch:
                {
                    Hoist(branch.Then);
                    Hoist(branch.Else);
                    if (branch.Then.Count == 0 && branch.Else.Count == 0)
                    {
                        list.RemoveAt(i--); // conditions are operands: no side effects
                        break;
                    }
                    if (branch.Then.Count == 0)
                    {
                        (branch.Then, branch.Else) = (branch.Else, branch.Then);
                        branch.Condition = Negate(branch.Condition);
                    }
                    if (branch.Else.Count == 0)
                    {
                        break;
                    }
                    var thenEnds = !Completes(branch.Then);
                    var elseEnds = !Completes(branch.Else);
                    if (elseEnds && (!thenEnds || Size(branch.Else) < Size(branch.Then)))
                    {
                        (branch.Then, branch.Else) = (branch.Else, branch.Then);
                        branch.Condition = Negate(branch.Condition);
                        thenEnds = true;
                    }
                    if (thenEnds)
                    {
                        list.InsertRange(i + 1, branch.Else);
                        branch.Else = [];
                    }
                    break;
                }
                case CLoop loop:
                    Hoist(loop.Body);
                    break;
                case CSwitch choice:
                    foreach (var (_, body) in choice.Cases)
                    {
                        Hoist(body);
                    }
                    break;
            }
        }
    }

    private static string Negate(string condition) =>
        condition.StartsWith('!') && IsIdentifier(condition[1..]) ? condition[1..]
        : IsIdentifier(condition) ? "!" + condition
        : $"!({condition})";

    private static bool IsIdentifier(string text) =>
        text.Length > 0 && !char.IsAsciiDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static int Size(List<CNode> list) => list.Sum(node => node switch
    {
        CIf branch => 1 + Size(branch.Then) + Size(branch.Else),
        CLoop loop => 1 + Size(loop.Body),
        CSwitch choice => 1 + choice.Cases.Sum(c => Size(c.Body)),
        CCode code => code.Lines.Count,
        CLabel => 0,
        _ => 1,
    });

    /// <summary>Whether control can reach the end of the list.</summary>
    private static bool Completes(List<CNode> list) => list.Count == 0 || list[^1] switch
    {
        CGoto or CBreak or CContinue => false,
        CCode code => code.Completes,
        CIf branch => Completes(branch.Then) || Completes(branch.Else),
        CLoop loop => Breaks(loop.Body),
        _ => true, // labels, switches (conservative)
    };

    /// <summary>Whether the list contains a break of the loop it is the body of.</summary>
    private static bool Breaks(List<CNode> list) => list.Any(node => node switch
    {
        CBreak => true,
        CIf branch => Breaks(branch.Then) || Breaks(branch.Else),
        _ => false, // a nested loop's or switch's break is its own
    });

    // ---- Labels (step 6) ----

    private static void CollectGotos(List<CNode> list, HashSet<int> targets)
    {
        foreach (var node in list)
        {
            switch (node)
            {
                case CGoto g:
                    targets.Add(g.Block);
                    break;
                case CIf branch:
                    CollectGotos(branch.Then, targets);
                    CollectGotos(branch.Else, targets);
                    break;
                case CLoop loop:
                    CollectGotos(loop.Body, targets);
                    break;
                case CSwitch choice:
                    foreach (var (_, body) in choice.Cases)
                    {
                        CollectGotos(body, targets);
                    }
                    break;
            }
        }
    }

    private static void RemoveLabels(List<CNode> list, HashSet<int> targets)
    {
        list.RemoveAll(node => node is CLabel label && !targets.Contains(label.Block));
        foreach (var node in list)
        {
            switch (node)
            {
                case CIf branch:
                    RemoveLabels(branch.Then, targets);
                    RemoveLabels(branch.Else, targets);
                    break;
                case CLoop loop:
                    RemoveLabels(loop.Body, targets);
                    break;
                case CSwitch choice:
                    foreach (var (_, body) in choice.Cases)
                    {
                        RemoveLabels(body, targets);
                    }
                    break;
            }
        }
    }
}
