using System.Globalization;
using System.Text;

namespace Nova.Compiler.Tests;

/// <summary>
/// Model-based property tests of the built-in collections (docs/testing.md §3, milestone-6.md
/// criterion 2). For each collection and seed, a generator writes a Nova program of random
/// operations while a C# model of the collection computes the output the program must print:
/// values, lengths, capacities (type-system.md §23.1 growth), stale handles, slices and the order of
/// every drop. The program then runs under every C configuration and the fault-injection sweep.
/// </summary>
public class CollectionPropertyTests
{
    private const int Operations = 80;

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var kind in new[] { "vector", "string", "slotmap", "stablevector", "views" })
        {
            for (var seed = 1; seed <= 6; seed++)
            {
                data.Add(kind, seed);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Collection_agrees_with_its_model(string kind, int seed)
    {
        var (program, stdout) = Generate(kind, seed);
        var name = $"property-{kind}-{seed}.nova";
        RunCorpusTests.RunEverywhere(name, program, new RunCorpusTests.Expectations(stdout, 0, null, 0));
        // The NIR interpreter agrees with the model too (intermediate-representation.md §5.5).
        Assert.Equal(new InterpreterTests.Outcome(stdout, "", 0), InterpreterTests.Interpret(name, program));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Collection_handles_every_allocation_failure(string kind, int seed) =>
        FaultInjectionTests.Sweep($"property-{kind}-{seed}.nova", Generate(kind, seed).Program);

    internal static (string Program, string Stdout) Generate(string kind, int seed)
    {
        var random = new Random(seed);
        var generator = new Generator();
        switch (kind)
        {
            case "vector":
                generator.Vector(random);
                break;
            case "string":
                generator.String(random);
                break;
            case "slotmap":
                generator.SlotMap(random);
                break;
            case "views":
                generator.Views(random);
                break;
            default:
                generator.StableVector(random);
                break;
        }
        return (generator.Program.ToString(), generator.Stdout.ToString());
    }

    /// <summary>The growth rule of <c>nova_vec_grow</c> (type-system.md §23.1): the new capacity, or the old one if it fits.</summary>
    private static long Grow(long length, long capacity, long extra)
    {
        var need = length + extra;
        return need <= capacity ? capacity : Math.Max(Math.Max(capacity * 2, 4), need);
    }

    private sealed class Generator
    {
        public StringBuilder Program { get; } = new();
        public StringBuilder Stdout { get; } = new();
        private int _names;

        private void Line(string code) => Program.Append("    ").Append(code).Append('\n');

        private void Expect(object value) =>
            Stdout.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');

        private string Fresh(string prefix) => prefix + (_names++).ToString(CultureInfo.InvariantCulture);

        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary><c>Vector&lt;Item&gt;</c>, where destroying an item prints <c>d&lt;id&gt;</c>.</summary>
        public void Vector(Random random)
        {
            Program.Append("""
                struct Item { id: i32; drop { Console.write("d"); Console.writeLine(this.id); } }

                fn sumIds(xs: Slice<Item>) -> i32 {
                    var total = 0;
                    for (i in 0..xs.length) { total += xs[i].id; }
                    return total;
                }

                fn main() {
                    var v = Vector<Item>.create();

                """);
            var model = new List<int>();
            long capacity = 0;
            var nextId = 1;
            for (var op = 0; op < Operations; op++)
            {
                var choice = random.Next(100);
                if (choice < 40 || model.Count == 0 && choice < 70)
                {
                    capacity = Grow(model.Count, capacity, 1);
                    model.Add(nextId);
                    Line($"v.push(Item {{ id: {nextId++} }})!;");
                }
                else if (choice < 52)
                {
                    if (model.Count == 0)
                    {
                        Line("Console.writeLine(v.pop() == null);");
                        Expect("true");
                    }
                    else
                    {
                        // The popped item lives in a temporary until the end of the statement.
                        var last = model[^1];
                        model.RemoveAt(model.Count - 1);
                        Line("Console.writeLine(v.pop()!.id);");
                        Expect(last);
                        Expect($"d{last}");
                    }
                }
                else if (choice < 64 && model.Count > 0)
                {
                    var i = random.Next(model.Count);
                    Expect($"d{model[i]}"); // the old element is destroyed by the assignment
                    model[i] = nextId;
                    Line($"v[{i}] = Item {{ id: {nextId++} }};");
                }
                else if (choice < 74 && model.Count > 0)
                {
                    var i = random.Next(model.Count);
                    var name = Fresh("r");
                    Line($"let {name} = borrow v[{i}];");
                    Line($"Console.writeLine({name}.id);");
                    Expect(model[i]);
                }
                else if (choice < 82)
                {
                    Line("Console.writeLine(v.length);");
                    Expect(model.Count);
                    Line("Console.writeLine(v.capacity);");
                    Expect(capacity);
                }
                else if (choice < 92)
                {
                    Line("Console.writeLine(sumIds(v));");
                    Expect(model.Sum());
                }
                else if (choice < 95)
                {
                    Line("v.clear();");
                    foreach (var id in model)
                    {
                        Expect($"d{id}");
                    }
                    model.Clear();
                }
                else
                {
                    Line("Console.writeLine(v.view().length);");
                    Expect(model.Count);
                }
            }
            foreach (var id in model)
            {
                Expect($"d{id}"); // `v` is destroyed at the end of `main`, elements in index order
            }
            Program.Append("}\n");
        }

        /// <summary><c>String</c>: appends of UTF-8 pieces, slices at character boundaries, bytes, comparison.</summary>
        public void String(Random random)
        {
            string[] pieces = ["a", "bc", "é", "日本", "", "xyz", "ñ", "Ω", "🙂"];
            Program.Append("""
                fn main() {
                    var s = String.create();

                """);
            var model = new StringBuilder();
            long capacity = 0;
            for (var op = 0; op < Operations; op++)
            {
                var bytes = Encoding.UTF8.GetBytes(model.ToString());
                var choice = random.Next(100);
                if (choice < 40)
                {
                    var piece = pieces[random.Next(pieces.Length)];
                    capacity = Grow(bytes.Length, capacity, Encoding.UTF8.GetByteCount(piece));
                    model.Append(piece);
                    Line($"s.append(\"{piece}\")!;");
                }
                else if (choice < 50)
                {
                    Line("Console.writeLine(s);");
                    Expect(model.ToString());
                }
                else if (choice < 60)
                {
                    Line("Console.writeLine(s.length);");
                    Expect(bytes.Length);
                    Line("Console.writeLine(s.capacity);");
                    Expect(capacity);
                }
                else if (choice < 75)
                {
                    var boundaries = Boundaries(model.ToString());
                    var lo = boundaries[random.Next(boundaries.Count)];
                    var hi = boundaries[random.Next(boundaries.Count)];
                    (lo, hi) = (Math.Min(lo, hi), Math.Max(lo, hi));
                    Line($"Console.writeLine(s[{lo}..{hi}]);");
                    Expect(Encoding.UTF8.GetString(bytes, lo, hi - lo));
                }
                else if (choice < 85 && bytes.Length > 0)
                {
                    var i = random.Next(bytes.Length);
                    Line($"Console.writeLine(s[{i}]);");
                    Expect(bytes[i]);
                }
                else if (choice < 95)
                {
                    var same = random.Next(2) == 0;
                    var text = same ? model.ToString() : model + "x";
                    Line($"Console.writeLine(s.view() == \"{text}\");");
                    Expect(Bool(same));
                }
                else
                {
                    Line("s.clear();");
                    model.Clear();
                }
            }
            Program.Append("}\n");
        }

        /// <summary>The byte offsets that start a character, and the end.</summary>
        private static List<int> Boundaries(string text)
        {
            var result = new List<int>();
            var offset = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                result.Add(offset);
                offset += rune.Utf8SequenceLength;
            }
            result.Add(offset);
            return result;
        }

        /// <summary>
        /// <c>Vector&lt;StringView&gt;</c> of slices into one <c>String</c> (scoped elements, type-system.md §25). After
        /// the vector's last use the string is changed, which the borrow checker must allow (its loans have ended).
        /// </summary>
        public void Views(Random random)
        {
            string[] pieces = ["ab", "é", "日本", "xyz", "ñ", "🙂", "q"];
            var text = string.Concat(Enumerable.Range(0, 12).Select(_ => pieces[random.Next(pieces.Length)]));
            Program.Append("fn main() {\n");
            Line("var text = String.create();");
            Line($"text.append(\"{text}\")!;");
            Line("let base = text.view();");
            Line("var v = Vector<StringView>.create();");
            var bytes = Encoding.UTF8.GetBytes(text);
            var boundaries = Boundaries(text);
            string Slice(out int lo, out int hi)
            {
                lo = boundaries[random.Next(boundaries.Count)];
                hi = boundaries[random.Next(boundaries.Count)];
                (lo, hi) = (Math.Min(lo, hi), Math.Max(lo, hi));
                return Encoding.UTF8.GetString(bytes, lo, hi - lo);
            }
            var model = new List<string>();
            for (var op = 0; op < Operations; op++)
            {
                var choice = random.Next(100);
                if (choice < 40 || model.Count == 0 && choice < 60)
                {
                    model.Add(Slice(out var lo, out var hi));
                    Line($"v.push(base[{lo}..{hi}])!;");
                }
                else if (choice < 55 && model.Count > 0)
                {
                    var i = random.Next(model.Count);
                    Line($"Console.writeLine(v[{i}]);");
                    Expect(model[i]);
                }
                else if (choice < 65 && model.Count > 0)
                {
                    var i = random.Next(model.Count);
                    model[i] = Slice(out var lo, out var hi);
                    Line($"v[{i}] = base[{lo}..{hi}];");
                }
                else if (choice < 75)
                {
                    if (model.Count == 0)
                    {
                        Line("Console.writeLine(v.pop() == null);");
                        Expect("true");
                    }
                    else
                    {
                        Line("Console.writeLine(v.pop()!);");
                        Expect(model[^1]);
                        model.RemoveAt(model.Count - 1);
                    }
                }
                else if (choice < 85)
                {
                    Line("Console.writeLine(v.length);");
                    Expect(model.Count);
                }
                else
                {
                    var name = Fresh("t");
                    Line($"var {name}: usize = 0;");
                    Line($"for (part in v) {{ {name} += part.length; }}");
                    Line($"Console.writeLine({name});");
                    Expect(model.Sum(m => Encoding.UTF8.GetByteCount(m)));
                }
            }
            Line("Console.writeLine(v.length);");
            Expect(model.Count);
            Line("text.clear(); // the vector and its views are dead here");
            Line("Console.writeLine(text.length);");
            Expect(0);
            Program.Append("}\n");
        }

        /// <summary><c>SlotMap&lt;i32&gt;</c> with every handle kept in a vector, including stale ones.</summary>
        public void SlotMap(Random random)
        {
            Program.Append("""
                fn main() {
                    var m = SlotMap<i32>.create();
                    var hs = Vector<Handle<i32>>.create();

                """);
            var handles = new List<(int Value, bool Live)>();
            for (var op = 0; op < Operations; op++)
            {
                var choice = random.Next(100);
                if (choice < 35 || handles.Count == 0)
                {
                    var value = random.Next(-1000, 1000);
                    handles.Add((value, true));
                    Line($"hs.push(m.insert({value})!)!;");
                    continue;
                }
                var j = random.Next(handles.Count);
                var (current, live) = handles[j];
                if (choice < 55)
                {
                    var name = Fresh("g");
                    Line($"match (m.get(hs[{j}])) {{ Some({name}) => {{ Console.writeLine({name}); }} null => {{ Console.writeLine(\"null\"); }} }}");
                    Expect(live ? current : "null");
                }
                else if (choice < 70)
                {
                    var name = Fresh("x");
                    Line($"match (m.remove(hs[{j}])) {{ Some({name}) => {{ Console.writeLine({name}); }} null => {{ Console.writeLine(\"null\"); }} }}");
                    Expect(live ? current : "null");
                    handles[j] = (current, false);
                }
                else if (choice < 82)
                {
                    var value = random.Next(-1000, 1000);
                    var name = Fresh("w");
                    Line($"match (m.getMut(hs[{j}])) {{ Some({name}) => {{ {name} = {value}; }} null => {{ }} }}");
                    if (live)
                    {
                        handles[j] = (value, true);
                    }
                }
                else if (choice < 92)
                {
                    Line($"Console.writeLine(m.contains(hs[{j}]));");
                    Expect(Bool(live));
                }
                else
                {
                    Line("Console.writeLine(m.length);");
                    Expect(handles.Count(h => h.Live));
                }
            }
            Program.Append("}\n");
        }

        /// <summary><c>StableVector&lt;i32&gt;</c>, including element borrows held across pushes.</summary>
        public void StableVector(Random random)
        {
            Program.Append("""
                fn main() {
                    var sv = StableVector<i32>.create();

                """);
            var model = new List<int>();
            for (var op = 0; op < Operations; op++)
            {
                var choice = random.Next(100);
                if (choice < 40 || model.Count == 0)
                {
                    var value = random.Next(-1000, 1000);
                    Line($"Console.writeLine(sv.push({value})!);");
                    Expect(model.Count);
                    model.Add(value);
                }
                else if (choice < 55)
                {
                    var i = random.Next(model.Count);
                    Line($"Console.writeLine(sv[{i}]);");
                    Expect(model[i]);
                }
                else if (choice < 70)
                {
                    var i = random.Next(model.Count);
                    var value = random.Next(-1000, 1000);
                    Line($"sv[{i}] = {value};");
                    model[i] = value;
                }
                else if (choice < 85)
                {
                    // A borrow held across pushes, which may allocate new chunks (§24.2).
                    var i = random.Next(model.Count);
                    var name = Fresh("p");
                    Line($"let {name} = borrow sv[{i}];");
                    var pushes = random.Next(1, 20);
                    for (var k = 0; k < pushes; k++)
                    {
                        var value = random.Next(-1000, 1000);
                        Line($"sv.push({value})!;");
                        model.Add(value);
                    }
                    Line($"Console.writeLine({name});");
                    Expect(model[i]);
                }
                else
                {
                    Line("Console.writeLine(sv.length);");
                    Expect(model.Count);
                }
            }
            Program.Append("}\n");
        }
    }
}
