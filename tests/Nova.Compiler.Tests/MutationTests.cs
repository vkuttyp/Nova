using System.Collections.Immutable;
using System.Text;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;
using Xunit.Abstractions;

namespace Nova.Compiler.Tests;

/// <summary>
/// The M2 mutation suite (docs/milestone-2.md, criterion 3). Seed programs that compile are
/// mutated structurally: adjacent statements swapped, statements moved or removed, scopes added
/// or removed, moves duplicated. Each mutant's expected diagnostics, as (code, line) pairs, come
/// from <see cref="Oracle"/>, a separate model of the rules in ownership.md and lifetimes.md for
/// straight-line code. It shares no code with the compiler's checkers. The compiler must report
/// exactly those diagnostics.
/// </summary>
public class MutationTests(ITestOutputHelper output)
{
    private const int MutantsPerSeed = 120;

    private abstract record Stmt;

    /// <summary><c>var x = Res { v: 1 };</c></summary>
    private sealed record Declare(string Name) : Stmt;

    /// <summary><c>consume(x);</c>: a move.</summary>
    private sealed record Consume(string Name) : Stmt;

    /// <summary><c>peek(x);</c>: an implicit shared borrow for the call.</summary>
    private sealed record Peek(string Name) : Stmt;

    /// <summary><c>total += x.v;</c>: a read.</summary>
    private sealed record ReadField(string Name) : Stmt;

    /// <summary><c>x.v = 7;</c>: a write of a field (Res has a drop block).</summary>
    private sealed record WriteField(string Name) : Stmt;

    /// <summary><c>x = Res { v: 2 };</c>: a write that (re)initialises.</summary>
    private sealed record Assign(string Name) : Stmt;

    /// <summary><c>let r = borrow x;</c> or <c>let r = borrow mut x;</c></summary>
    private sealed record Borrow(string Reference, string Name, bool Mutable) : Stmt;

    /// <summary><c>look(r);</c> or <c>poke(r);</c>: a use of a reference.</summary>
    private sealed record Use(string Reference, bool Mutable) : Stmt;

    private sealed record Block(ImmutableArray<Stmt> Body) : Stmt;

    private static Declare D(string x) => new(x);

    private static Block B(params Stmt[] body) => new([.. body]);

    /// <summary>Seed programs; each must compile without diagnostics.</summary>
    private static readonly ImmutableArray<Stmt>[] s_seeds =
    [
        [
            D("a"), D("b"), new Borrow("ra", "a", false), new Use("ra", false), new Peek("b"), new Consume("a"),
            new Assign("a"), new ReadField("a"), new Borrow("mb", "b", true), new Use("mb", true), new WriteField("b"),
            new Consume("b"), B(D("c"), new Borrow("rc", "c", false), new Use("rc", false), new Consume("c")),
        ],
        [
            D("x"), new Borrow("r1", "x", false), new Borrow("r2", "x", false), new Use("r1", false), new ReadField("x"),
            new Use("r2", false), new Borrow("m", "x", true), new Use("m", true), new Use("m", true), new Assign("x"),
            new ReadField("x"), new Consume("x"),
        ],
        [
            D("p"), B(new Borrow("rp", "p", true), new Use("rp", true)), new Peek("p"),
            B(D("q"), new Consume("q"), new Assign("q"), new Borrow("rq", "q", false), new Use("rq", false)),
            new Consume("p"),
        ],
        [
            D("s"), D("t"), new Consume("s"), new Assign("s"), new WriteField("t"), new Borrow("rt", "t", false),
            new ReadField("t"), new Use("rt", false), new Consume("t"), new Consume("s"),
        ],
        [
            D("u"), new Borrow("mu", "u", true), new Use("mu", true), new WriteField("u"), new Peek("u"),
            new Borrow("su", "u", false), new ReadField("u"), new Use("su", false), new Assign("u"),
            B(new Borrow("mu2", "u", true), new Use("mu2", true)), new Consume("u"),
        ],
    ];

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < s_seeds.Length; i++)
        {
            data.Add(i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Seed_compiles_and_the_oracle_agrees(int seed)
    {
        var (source, lines) = Render(s_seeds[seed]);
        Assert.Empty(Oracle.Expect(s_seeds[seed], lines));
        Assert.Empty(Compile(source));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Mutants_get_the_oracles_verdict(int seed)
    {
        var random = new Random(1000 + seed); // fixed: the mutants are the same on every run
        var seen = new HashSet<string>();
        int resolved = 0, rejected = 0, attempts = 0;
        // Mutants with undefined names never reach the move and borrow checkers. They are checked
        // too, but only mutants whose names resolve count towards the target.
        while (resolved < MutantsPerSeed && attempts++ < MutantsPerSeed * 20)
        {
            var mutant = s_seeds[seed];
            var steps = 1 + random.Next(3);
            var applied = new List<string>();
            for (var step = 0; step < steps; step++)
            {
                (mutant, var description) = Mutate(mutant, random);
                applied.Add(description);
            }
            var (source, lines) = Render(mutant);
            if (!seen.Add(source))
            {
                continue;
            }
            var expected = Oracle.Expect(mutant, lines);
            var actual = Compile(source);
            if (!expected.SetEquals(actual))
            {
                Assert.Fail($"mutant {seen.Count} of seed {seed} ({string.Join("; ", applied)}):\n{source}\n"
                    + $"expected: {Show(expected)}\nactual:   {Show(actual)}");
            }
            if (expected.All(e => e.Code != "N0200"))
            {
                resolved++;
                rejected += expected.Count > 0 ? 1 : 0;
            }
        }
        output.WriteLine($"seed {seed}: {seen.Count} distinct mutants; {resolved} resolve their names, and {rejected} of those are rejected");
        // The suite is only meaningful if it exercises both verdicts of the move and borrow checkers.
        Assert.Equal(MutantsPerSeed, resolved);
        Assert.InRange(rejected, resolved / 10, resolved * 9 / 10);
    }

    private static string Show(IEnumerable<(string Code, int Line)> diagnostics) =>
        string.Join(", ", diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Code, StringComparer.Ordinal).Select(d => $"{d.Code}@{d.Line}"));

    private static HashSet<(string Code, int Line)> Compile(string source)
    {
        var text = new SourceText("mutant.nova", source);
        var compilation = Compilation.Create([text], TargetInfo.Lp64);
        return [.. compilation.Diagnostics.Items.Select(d => (d.Code, d.Source.GetLinePosition(d.Span.Start).Line))];
    }

    // ---- Rendering ----

    private const string Prelude = """
        struct Res { v: i32; drop { } }
        fn consume(r: Res) { }
        fn peek(r: borrow Res) { }
        fn look(r: borrow Res) { }
        fn poke(r: borrow mut Res) { r.v += 1; }
        fn main() {
            var total = 0;
        """;

    /// <summary>The program text, and the source line of every statement (by reference).</summary>
    private static (string Source, Dictionary<Stmt, int> Lines) Render(ImmutableArray<Stmt> program)
    {
        var sb = new StringBuilder(Prelude.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        var line = sb.ToString().Count(c => c == '\n') + 1;
        var lines = new Dictionary<Stmt, int>(ReferenceEqualityComparer.Instance);
        void Emit(ImmutableArray<Stmt> body, int depth)
        {
            foreach (var stmt in body)
            {
                var indent = new string(' ', 4 * depth);
                if (stmt is Block block)
                {
                    sb.Append(indent).Append("{\n");
                    line++;
                    Emit(block.Body, depth + 1);
                    sb.Append(indent).Append("}\n");
                    line++;
                    continue;
                }
                lines[stmt] = line++;
                sb.Append(indent).Append(stmt switch
                {
                    Declare d => $"var {d.Name} = Res {{ v: 1 }};",
                    Consume c => $"consume({c.Name});",
                    Peek p => $"peek({p.Name});",
                    ReadField r => $"total += {r.Name}.v;",
                    WriteField w => $"{w.Name}.v = 7;",
                    Assign a => $"{a.Name} = Res {{ v: 2 }};",
                    Borrow b => $"let {b.Reference} = borrow {(b.Mutable ? "mut " : "")}{b.Name};",
                    Use u => $"{(u.Mutable ? "poke" : "look")}({u.Reference});",
                    _ => throw new InvalidOperationException(),
                }).Append('\n');
            }
        }
        Emit(program, 1);
        sb.Append("    Console.writeLine(total);\n}\n");
        return (sb.ToString(), lines);
    }

    // ---- Mutations ----

    /// <summary>Every statement list in the program (the top level and each block body), with a way to replace it.</summary>
    private static List<(ImmutableArray<Stmt> List, Func<ImmutableArray<Stmt>, ImmutableArray<Stmt>> Replace)> Lists(ImmutableArray<Stmt> program)
    {
        var result = new List<(ImmutableArray<Stmt>, Func<ImmutableArray<Stmt>, ImmutableArray<Stmt>>)>();
        void Visit(ImmutableArray<Stmt> list, Func<ImmutableArray<Stmt>, ImmutableArray<Stmt>> replace)
        {
            result.Add((list, replace));
            for (var i = 0; i < list.Length; i++)
            {
                if (list[i] is Block block)
                {
                    var index = i;
                    Visit(block.Body, body => replace(list.SetItem(index, new Block(body))));
                }
            }
        }
        Visit(program, list => list);
        return result;
    }

    private static (ImmutableArray<Stmt> Program, string Description) Mutate(ImmutableArray<Stmt> program, Random random)
    {
        var lists = Lists(program);
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var (list, replace) = lists[random.Next(lists.Count)];
            switch (random.Next(6))
            {
                case 0 when list.Length >= 2:
                {
                    var i = random.Next(list.Length - 1);
                    return (replace(list.SetItem(i, list[i + 1]).SetItem(i + 1, list[i])), $"swap {i},{i + 1}");
                }
                case 1 when list.Length >= 2:
                {
                    var from = random.Next(list.Length);
                    var to = random.Next(list.Length);
                    return (replace(list.RemoveAt(from).Insert(to, list[from])), $"move {from}->{to}");
                }
                case 2 when list.Length >= 1:
                {
                    var i = random.Next(list.Length);
                    return (replace(list.RemoveAt(i)), $"remove {i}");
                }
                case 3 when list.Any(s => s is Block):
                {
                    var blocks = Enumerable.Range(0, list.Length).Where(i => list[i] is Block).ToList();
                    var i = blocks[random.Next(blocks.Count)];
                    return (replace(list.RemoveAt(i).InsertRange(i, ((Block)list[i]).Body)), $"unscope {i}");
                }
                case 4 when list.Length >= 1:
                {
                    var start = random.Next(list.Length);
                    var count = 1 + random.Next(list.Length - start);
                    var wrapped = new Block(list.Skip(start).Take(count).ToImmutableArray());
                    return (replace(list.RemoveRange(start, count).Insert(start, wrapped)), $"scope {start}+{count}");
                }
                case 5 when list.Any(s => s is Consume):
                {
                    var moves = Enumerable.Range(0, list.Length).Where(i => list[i] is Consume).ToList();
                    var i = moves[random.Next(moves.Count)];
                    var to = i + 1 + random.Next(list.Length - i);
                    // A fresh record: statements are identified by reference when rendering.
                    return (replace(list.Insert(to, new Consume(((Consume)list[i]).Name))), $"duplicate move {i}->{to}");
                }
            }
        }
        return (program, "none");
    }

    // ---- The oracle ----

    /// <summary>
    /// The expected diagnostics of a straight-line program, from the rules alone:
    /// <list type="bullet">
    /// <item>names must be declared in an enclosing scope (N0200); if not, no later analysis runs;</item>
    /// <item>a use of a moved variable is N0250, reported once per variable (type-system.md §14.4);
    /// writing a field of a moved <c>Res</c> counts, since Res has a drop block;</item>
    /// <item>a loan is live at a statement if its reference is used at a later statement (NLL, lifetimes.md §5);</item>
    /// <item>against a live loan of x: a move or write of x is N0240, a borrow that conflicts is N0241,
    /// a read while mutably borrowed is N0242 (lifetimes.md §4).</item>
    /// </list>
    /// Scope ends cannot conflict here: a reference is always declared in its referent's scope, so it
    /// is dead when that scope ends.
    /// </summary>
    private static class Oracle
    {
        public static HashSet<(string Code, int Line)> Expect(ImmutableArray<Stmt> program, Dictionary<Stmt, int> lines)
        {
            var nameErrors = new HashSet<(string, int)>();
            var scopes = new List<HashSet<string>> { new() };
            var flat = new List<Stmt>();
            void Resolve(ImmutableArray<Stmt> body)
            {
                foreach (var stmt in body)
                {
                    if (stmt is Block block)
                    {
                        scopes.Add([]);
                        Resolve(block.Body);
                        scopes.RemoveAt(scopes.Count - 1);
                        continue;
                    }
                    flat.Add(stmt);
                    var used = stmt switch
                    {
                        Declare => null,
                        Consume c => c.Name,
                        Peek p => p.Name,
                        ReadField r => r.Name,
                        WriteField w => w.Name,
                        Assign a => a.Name,
                        Borrow b => b.Name,
                        Use u => u.Reference,
                        _ => null,
                    };
                    if (used is not null && !scopes.Any(s => s.Contains(used)))
                    {
                        nameErrors.Add(("N0200", lines[stmt]));
                    }
                    switch (stmt)
                    {
                        case Declare d:
                            scopes[^1].Add(d.Name);
                            break;
                        case Borrow b:
                            scopes[^1].Add(b.Reference);
                            break;
                    }
                }
            }
            Resolve(program);
            if (nameErrors.Count > 0)
            {
                return nameErrors;
            }

            var errors = new HashSet<(string, int)>();
            var lastUse = new Dictionary<string, int>();
            for (var i = 0; i < flat.Count; i++)
            {
                if (flat[i] is Use u)
                {
                    lastUse[u.Reference] = i;
                }
            }
            var moved = new HashSet<string>();
            var reportedMove = new HashSet<string>();
            var loans = new List<(string Reference, string Name, bool Mutable)>();
            for (var i = 0; i < flat.Count; i++)
            {
                var line = lines[flat[i]];
                var index = i;
                IEnumerable<(string Reference, string Name, bool Mutable)> Live(string name) =>
                    loans.Where(l => l.Name == name && lastUse.TryGetValue(l.Reference, out var last) && last > index);
                void NeedInit(string name)
                {
                    if (moved.Contains(name) && reportedMove.Add(name))
                    {
                        errors.Add(("N0250", line));
                    }
                }
                void Conflict(string name, bool onlyMutableLoans, string code)
                {
                    if (Live(name).Any(l => l.Mutable || !onlyMutableLoans))
                    {
                        errors.Add((code, line));
                    }
                }

                switch (flat[i])
                {
                    case Consume c:
                        NeedInit(c.Name);
                        Conflict(c.Name, onlyMutableLoans: false, "N0240");
                        moved.Add(c.Name);
                        break;
                    case Peek p:
                        NeedInit(p.Name);
                        Conflict(p.Name, onlyMutableLoans: true, "N0241");
                        break;
                    case ReadField r:
                        NeedInit(r.Name);
                        Conflict(r.Name, onlyMutableLoans: true, "N0242");
                        break;
                    case WriteField w:
                        NeedInit(w.Name);
                        Conflict(w.Name, onlyMutableLoans: false, "N0240");
                        break;
                    case Assign a:
                        Conflict(a.Name, onlyMutableLoans: false, "N0240");
                        moved.Remove(a.Name);
                        break;
                    case Borrow b:
                        NeedInit(b.Name);
                        Conflict(b.Name, onlyMutableLoans: !b.Mutable, "N0241");
                        loans.Add((b.Reference, b.Name, b.Mutable));
                        break;
                }
            }
            return errors;
        }
    }
}
