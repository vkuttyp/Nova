using System.Collections.Immutable;
using System.Globalization;
using Nova.Compiler.Backend.C;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Tests;

/// <summary>The structured form (c-backend.md §3). Its behaviour is checked by the run corpus in both forms; these test the tree itself.</summary>
public class StructuredCTests
{
    private static NirProgram Nir(string text)
    {
        var compilation = Compilation.Create([new SourceText("t.nova", text)], TargetInfo.Lp64);
        Assert.False(compilation.Diagnostics.HasErrors);
        return compilation.Nir!;
    }

    /// <summary>Structures with marker lines: <c>s B.I</c> per statement, <c>x B</c> per exit terminator.</summary>
    private static List<CNode>? Structure(NirFunction function) => CStructurer.Structure(function,
        block => [.. block.Statements.Select((_, i) => (CNode)new CCode([string.Create(CultureInfo.InvariantCulture, $"s {block.Id}.{i}")], true))],
        (block, _) => new CCode([string.Create(CultureInfo.InvariantCulture, $"x {block.Id}")], false),
        _ => "c",
        s => ("v", [.. s.Cases.Select(c => (c.Value.ToString(CultureInfo.InvariantCulture), c.Target)), ("default", s.Otherwise)]));

    private static IEnumerable<CNode> Walk(List<CNode> list) => list.SelectMany(node => node switch
    {
        CIf branch => [node, .. Walk(branch.Then), .. Walk(branch.Else)],
        CLoop loop => [node, .. Walk(loop.Body)],
        CSwitch choice => [node, .. choice.Cases.SelectMany(c => Walk(c.Body))],
        _ => (IEnumerable<CNode>)[node],
    });

    /// <summary>Every block's code is emitted exactly once: each statement and exit appears once in the tree (§3 step 2).</summary>
    [Theory]
    [MemberData(nameof(BackendTests.RunPassFiles), MemberType = typeof(BackendTests))]
    public void Every_statement_is_emitted_exactly_once(string relativePath)
    {
        var nir = Nir(File.ReadAllText(Path.Combine(TestHelpers.RepoRoot(), "tests", "corpus", relativePath)));
        foreach (var function in nir.Functions.Where(f => !f.Source.IsGeneric))
        {
            var tree = Structure(function);
            Assert.NotNull(tree); // Nova source produces reducible graphs
            var expected = function.Blocks.SelectMany(b => b.Statements.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"s {b.Id}.{i}"))
                .Concat(b.Terminator is NirGoto or NirBranch or NirSwitch ? [] : [string.Create(CultureInfo.InvariantCulture, $"x {b.Id}")]));
            var actual = Walk(tree).OfType<CCode>().SelectMany(c => c.Lines);
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

            // Every remaining goto names a label that is in the tree.
            var labels = Walk(tree).OfType<CLabel>().Select(l => l.Block).ToHashSet();
            Assert.All(Walk(tree).OfType<CGoto>(), g => Assert.Contains(g.Block, labels));
        }
    }

    [Fact]
    public void Loops_and_branches_need_no_goto()
    {
        var nir = Nir("""
            fn f(n: i32) -> i32 {
                var total = 0;
                var i = 0;
                while (i < n) {
                    var j = 0;
                    while (j < i) {
                        if (j % 3 == 0) { j += 1; continue; }
                        if (total > 100) { break; }
                        total += j;
                        j += 1;
                    }
                    i += 1;
                }
                if (total > 5) { total = 5; } else { total = 0; }
                return total;
            }
            fn main() -> i32 => f(4);
            """);
        var tree = Structure(nir.Functions.Single(f => f.Source.Syntax.Name.Span.Start < 10))!;
        Assert.Empty(Walk(tree).OfType<CGoto>());
        Assert.Equal(2, Walk(tree).OfType<CLoop>().Count());
        // Both loops' exits and the inner `break` are breaks; the `continue` falls off the loop body.
        Assert.Equal(3, Walk(tree).OfType<CBreak>().Count());
    }

    /// <summary>A retreating edge that is not a back edge makes the graph irreducible: the function is emitted flat.</summary>
    [Fact]
    public void Irreducible_graph_falls_back_to_flat()
    {
        var source = Nir("fn main() -> i32 { var b = true; if (b) { return 1; } return 0; }").Functions.Single();
        var boolean = source.Locals.First(l => l.Type.Kind == TypeKind.Bool).Type;
        NirBlock entry = new(0), a = new(1), b = new(2);
        entry.Terminator = new NirBranch(new NirConst(1, boolean), a, b);
        a.Terminator = new NirGoto(b);
        b.Terminator = new NirGoto(a); // a and b each enter the cycle a <-> b
        var function = new NirFunction(source.Source, source.Locals, source.ParameterCount, [entry, a, b]);
        Assert.False(new ControlFlow(function).IsReducible);
        Assert.Null(Structure(function));
    }

    [Fact]
    public void Diamond_becomes_if_else()
    {
        var source = Nir("fn main() -> i32 { var b = true; if (b) { return 1; } return 0; }").Functions.Single();
        var boolean = source.Locals.First(l => l.Type.Kind == TypeKind.Bool).Type;
        NirBlock entry = new(0), a = new(1), b = new(2), join = new(3);
        entry.Terminator = new NirBranch(new NirConst(1, boolean), a, b);
        a.Statements.Add(new NirStorageDead(source.Locals[0]));
        b.Statements.Add(new NirStorageDead(source.Locals[0]));
        a.Terminator = new NirGoto(join);
        b.Terminator = new NirGoto(join);
        join.Terminator = new NirReturn(null);
        var tree = Structure(new NirFunction(source.Source, source.Locals, source.ParameterCount, [entry, a, b, join]))!;
        Assert.Empty(Walk(tree).OfType<CGoto>());
        Assert.Empty(Walk(tree).OfType<CLabel>());
        var branch = Assert.Single(Walk(tree).OfType<CIf>());
        Assert.NotEmpty(branch.Then);
        Assert.NotEmpty(branch.Else);
        Assert.IsType<CCode>(tree[^1]); // the join's return follows the if
    }
}
