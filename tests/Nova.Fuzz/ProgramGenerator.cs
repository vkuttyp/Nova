using System.Globalization;
using System.Numerics;
using System.Text;

namespace Nova.Fuzz;

/// <summary>
/// A Csmith-style generator of well-typed, terminating, deterministic Nova programs (docs/testing.md §5).
/// The same seed gives the same program everywhere. Calls form a DAG, loops have constant trip counts,
/// and a cost budget bounds the work; most arithmetic goes through guarded helpers written in Nova.
/// </summary>
public sealed class ProgramGenerator
{
    private const double RawChance = 0.01; // unguarded operations, which may panic
    private const int FunctionBudget = 400;
    private const int MainBudget = 4000;
    private const int MaxMultiplier = 64;

    private readonly Random _random;
    private readonly StringBuilder _out = new();
    private readonly List<StructDef> _structs = [];
    private readonly List<EnumDef> _enums = [];
    private readonly List<Function> _functions = [];
    private Function? _current;
    private int _dropIds;

    private ProgramGenerator(int seed) => _random = new Random(seed);

    /// <summary>The program for <paramref name="seed"/>.</summary>
    public static string Generate(int seed) => new ProgramGenerator(seed).Build(seed);

    // ---- Types ----

    private sealed record IntType(string Name, int Bits, bool Signed)
    {
        public BigInteger Min => Signed ? -(BigInteger.One << (Bits - 1)) : BigInteger.Zero;

        public BigInteger Max => Signed ? (BigInteger.One << (Bits - 1)) - 1 : (BigInteger.One << Bits) - 1;

        /// <summary>A suffixed literal; negative ones in parentheses so they nest anywhere.</summary>
        public string Literal(BigInteger value) => value.Sign < 0
            ? $"(-{(-value).ToString(CultureInfo.InvariantCulture)}{Name})"
            : $"{value.ToString(CultureInfo.InvariantCulture)}{Name}";
    }

    private static readonly IntType[] s_ints =
    [
        new("i8", 8, true), new("i16", 16, true), new("i32", 32, true), new("i64", 64, true),
        new("u8", 8, false), new("u16", 16, false), new("u32", 32, false), new("u64", 64, false),
        new("usize", 64, false),
    ];

    private static IntType Int(string name) => s_ints.First(t => t.Name == name);

    private static readonly IntType s_usize = Int("usize");
    private static readonly IntType s_i32 = Int("i32");
    private static readonly IntType s_u32 = Int("u32");
    private static readonly IntType s_u8 = Int("u8");

    /// <summary>A scalar type: an integer type, or null for <c>bool</c>.</summary>
    private sealed record Scalar(IntType? Int)
    {
        public bool IsBool => Int is null;

        public string Name => Int?.Name ?? "bool";
    }

    private sealed record StructDef(string Name, List<(string Name, Scalar Type)> Fields);

    private sealed record EnumDef(string Name, List<(string Name, List<Scalar> Payload)> Variants);

    private enum ReturnKind { Void, Scalar, Result, Option }

    private sealed record Param(string Name, Scalar? Scalar = null, StructDef? Struct = null, bool Mutable = false, IntType? Slice = null);

    private sealed class Function(string name, int index)
    {
        public string Name { get; } = name;

        public int Index { get; } = index;

        public List<Param> Params { get; } = [];

        public ReturnKind Return { get; set; }

        public Scalar? ReturnType { get; set; } // for Scalar, Result (the Ok type) and Option

        public int Cost { get; set; } = 1;

        public bool IsMain => Index < 0;

        public bool ScalarParamsOnly => Params.All(p => p.Scalar is not null);
    }

    // ---- Variables in scope ----

    private enum VarKind { Scalar, Struct, Array, Slice, Enum, Vector, Option, Drop }

    private sealed class Var(string name, VarKind kind, bool mutable)
    {
        public string Name { get; } = name;

        public VarKind Kind { get; } = kind;

        public bool Mutable { get; } = mutable;

        public Scalar? Scalar { get; init; }          // Scalar, Option (its value type)
        public StructDef? Struct { get; init; }       // Struct (a local, a box, or a borrow parameter)
        public bool IsReference { get; init; }        // a borrow parameter: cannot be passed on
        public IntType? Element { get; init; }        // Array, Slice, Vector
        public int Length { get; init; }              // Array
        public EnumDef? Enum { get; init; }
        public int MinLength { get; set; }            // Vector: statically known minimum length
        public int Depth { get; init; }               // the scope depth it was declared at
        public bool Moved { get; set; }               // Drop: moved into a box
    }

    private readonly List<List<Var>> _scopes = [];
    private readonly HashSet<string> _excluded = []; // variables an argument list must not mention
    private int _names;
    private int _multiplier = 1;
    private int _budget;
    private bool _inLoop;
    private bool _noVectorReads;

    private IEnumerable<Var> Visible => _scopes.SelectMany(s => s).Where(v => !_excluded.Contains(v.Name) && !v.Moved);

    private string Fresh(string prefix) => $"{prefix}{_names++}";

    private Var Declare(Var variable)
    {
        _scopes[^1].Add(variable);
        return variable;
    }

    // ---- Random helpers ----

    private bool Chance(double p) => _random.NextDouble() < p;

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    private IntType AnyInt() => Pick(s_ints);

    private Scalar AnyScalar() => Chance(0.15) ? new Scalar(null) : new Scalar(AnyInt());

    // ---- Output ----

    private int _indent;

    private void Line(string text) => _out.Append(' ', 4 * _indent).Append(text).Append('\n');

    // ---- The program ----

    private string Build(int seed)
    {
        _out.Append(CultureInfo.InvariantCulture, $"// Generated by Nova.Fuzz from seed {seed} (docs/testing.md §5).\n");
        EmitHelpers();
        DeclareTypes();
        var count = _random.Next(3, 8);
        for (var i = 0; i < count; i++)
        {
            _functions.Add(DeclareFunction(i));
        }
        // Callees first, so their costs are known when a caller is generated.
        var bodies = new List<string>();
        for (var i = count - 1; i >= 0; i--)
        {
            bodies.Add(Body(_functions[i]));
        }
        bodies.Reverse();
        foreach (var body in bodies)
        {
            _out.Append(body);
        }
        _out.Append(Body(new Function("main", -1) { Return = ReturnKind.Scalar, ReturnType = new Scalar(s_i32) }));
        return _out.ToString();
    }

    /// <summary>Guarded arithmetic (Csmith's safe math): each returns its left operand where the operation would panic.</summary>
    private void EmitHelpers()
    {
        foreach (var t in s_ints)
        {
            var (n, min, max) = (t.Name, t.Literal(t.Min), t.Literal(t.Max));
            if (t.Signed)
            {
                _out.Append(string.Create(CultureInfo.InvariantCulture, $$"""
                    fn add_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if ((b > 0{{n}} && a > {{max}} - b) || (b < 0{{n}} && a < {{min}} - b)) { return a; } return a + b; }
                    fn sub_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if ((b < 0{{n}} && a > {{max}} + b) || (b > 0{{n}} && a < {{min}} + b)) { return a; } return a - b; }
                    fn div_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if (b == 0{{n}} || (a == {{min}} && b == (-1{{n}}))) { return a; } return a / b; }
                    fn rem_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if (b == 0{{n}}) { return a; } return a % b; }
                    fn neg_{{n}}(a: {{n}}) -> {{n}} { if (a == {{min}}) { return a; } return -a; }

                    """));
                _out.Append(t.Bits < 64
                    ? $"fn mul_{n}(a: {n}, b: {n}) -> {n} {{ let p = (a as i64) * (b as i64); if (p > ({max} as i64) || p < ({min} as i64)) {{ return a; }} return truncate<{n}>(p); }}\n"
                    : $"fn mul_{n}(a: {n}, b: {n}) -> {n} {{ return (a % 3037000499{n}) * (b % 3037000499{n}); }}\n");
            }
            else
            {
                _out.Append(string.Create(CultureInfo.InvariantCulture, $$"""
                    fn add_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if (a > {{max}} - b) { return a; } return a + b; }
                    fn sub_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if (a < b) { return a; } return a - b; }
                    fn div_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if (b == 0{{n}}) { return a; } return a / b; }
                    fn rem_{{n}}(a: {{n}}, b: {{n}}) -> {{n}} { if (b == 0{{n}}) { return a; } return a % b; }
                    fn shl_{{n}}(a: {{n}}, s: u32) -> {{n}} { return a << (s % {{t.Bits}}u32); }
                    fn shr_{{n}}(a: {{n}}, s: u32) -> {{n}} { return a >> (s % {{t.Bits}}u32); }

                    """));
                _out.Append(t.Bits < 64
                    ? $"fn mul_{n}(a: {n}, b: {n}) -> {n} {{ let p = (a as u64) * (b as u64); if (p > ({max} as u64)) {{ return a; }} return truncate<{n}>(p); }}\n"
                    : $"fn mul_{n}(a: {n}, b: {n}) -> {n} {{ return (a % 4294967296{n}) * (b % 4294967296{n}); }}\n");
            }
        }
        _out.Append('\n');
    }

    private void DeclareTypes()
    {
        for (var i = 0; i < _random.Next(1, 3); i++)
        {
            var fields = new List<(string, Scalar)>();
            for (var f = 0; f < _random.Next(1, 4); f++)
            {
                fields.Add(($"f{f}", AnyScalar()));
            }
            var s = new StructDef($"S{i}", fields);
            _structs.Add(s);
            _out.Append(CultureInfo.InvariantCulture, $"struct {s.Name} {{ {string.Concat(fields.Select(f => $"{f.Item1}: {f.Item2.Name}; "))}}}\n");
        }
        _out.Append("struct Noisy { id: i32; drop { Console.write(\"drop \"); Console.writeLine(this.id); } }\n");
        _out.Append("enum Fail { Bad, Worse(i32) }\n");
        for (var i = 0; i < _random.Next(1, 3); i++)
        {
            var variants = new List<(string, List<Scalar>)>();
            for (var v = 0; v < _random.Next(2, 5); v++)
            {
                var payload = new List<Scalar>();
                for (var p = 0; p < _random.Next(0, 3); p++)
                {
                    payload.Add(AnyScalar());
                }
                variants.Add(($"V{v}", payload));
            }
            var e = new EnumDef($"E{i}", variants);
            _enums.Add(e);
            var text = variants.Select(v => v.Item2.Count == 0 ? v.Item1 : $"{v.Item1}({string.Join(", ", v.Item2.Select(p => p.Name))})");
            _out.Append(CultureInfo.InvariantCulture, $"enum {e.Name} {{ {string.Join(", ", text)} }}\n");
        }
        _out.Append('\n');
    }

    private Function DeclareFunction(int index)
    {
        var function = new Function($"f{index}", index);
        for (var i = 0; i < _random.Next(0, 4); i++)
        {
            function.Params.Add(new Param($"p{i}", Scalar: AnyScalar()));
        }
        if (Chance(0.4))
        {
            function.Params.Add(new Param($"sp{index}", Struct: Pick(_structs), Mutable: Chance(0.6)));
        }
        if (Chance(0.3))
        {
            function.Params.Add(new Param($"xs{index}", Slice: AnyInt()));
        }
        function.Return = (ReturnKind)_random.Next(4);
        function.ReturnType = function.Return == ReturnKind.Void ? null : new Scalar(AnyInt());
        return function;
    }

    private static string TypeText(Function f) => f.Return switch
    {
        ReturnKind.Void => "",
        ReturnKind.Scalar => $" -> {f.ReturnType!.Name}",
        ReturnKind.Result => $" -> Result<{f.ReturnType!.Name}, Fail>",
        _ => $" -> {f.ReturnType!.Name}?",
    };

    private static string ParamText(Param p) =>
        p.Scalar is { } s ? $"{p.Name}: {s.Name}"
        : p.Struct is { } st ? $"{p.Name}: borrow {(p.Mutable ? "mut " : "")}{st.Name}"
        : $"{p.Name}: Slice<{p.Slice!.Name}>";

    private string Body(Function function)
    {
        _current = function;
        _names = 0;
        _multiplier = 1;
        _inLoop = false;
        _budget = function.IsMain ? MainBudget : FunctionBudget;
        var start = _out.Length;
        _scopes.Clear();
        _scopes.Add([]);
        foreach (var p in function.Params)
        {
            Declare(p.Scalar is { } s ? new Var(p.Name, VarKind.Scalar, false) { Scalar = s }
                : p.Struct is { } st ? new Var(p.Name, VarKind.Struct, p.Mutable) { Struct = st, IsReference = true }
                : new Var(p.Name, VarKind.Slice, false) { Element = p.Slice });
        }
        var header = $"fn {function.Name}({string.Join(", ", function.Params.Select(ParamText))}){TypeText(function)} {{\n";
        _indent = 1;
        var bodyStart = _out.Length;
        Block(_random.Next(4, function.IsMain ? 16 : 12), 0);
        if (function.IsMain)
        {
            // Run every function at least once, with the arguments it needs.
            foreach (var callee in _functions)
            {
                CallFunction(callee, 0, provide: true);
            }
        }
        // Checksums and the final return.
        foreach (var v in Visible.Where(v => v.Kind == VarKind.Scalar).Take(6))
        {
            Line($"Console.writeLine({v.Name});");
        }
        Line(FinalReturn(function));
        _indent = 0;
        var body = _out.ToString(bodyStart, _out.Length - bodyStart);
        _out.Length = start;
        function.Cost = Math.Max(1, (function.IsMain ? MainBudget : FunctionBudget) - _budget);
        return header + body + "}\n\n";
    }

    private string FinalReturn(Function function) => function.IsMain
        ? $"return (truncate<u8>({IntExpr(AnyInt(), 1)}) as i32);"
        : function.Return switch
        {
            ReturnKind.Void => "return;",
            ReturnKind.Scalar => $"return {IntExpr(function.ReturnType!.Int!, 1)};",
            ReturnKind.Result => $"return Ok({IntExpr(function.ReturnType!.Int!, 1)});",
            _ => Chance(0.2) ? "return null;" : $"return {IntExpr(function.ReturnType!.Int!, 1)};",
        };

    // ---- Statements ----

    private void Block(int statements, int depth)
    {
        _scopes.Add([]);
        for (var i = 0; i < statements && _budget > 0; i++)
        {
            Statement(depth);
        }
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    /// <summary>A block: <paramref name="header"/> (none: the previous line opened it), statements, <paramref name="close"/>.</summary>
    private void Nested(string? header, int depth, Action? before = null, string close = "}")
    {
        if (header is not null)
        {
            Line(header);
        }
        _indent++;
        _scopes.Add([]);
        before?.Invoke();
        Block(_random.Next(1, 5), depth + 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        _indent--;
        Line(close);
    }

    private int Depth => _scopes.Count;

    private void Statement(int depth)
    {
        _budget -= _multiplier;
        var mutableScalars = Visible.Where(v => v.Kind == VarKind.Scalar && v.Mutable).ToList();
        var structs = Visible.Where(v => v.Kind == VarKind.Struct).ToList();
        var mutableStructs = structs.Where(v => v.Mutable).ToList();
        var arrays = Visible.Where(v => v.Kind == VarKind.Array).ToList();
        var enums = Visible.Where(v => v.Kind == VarKind.Enum).ToList();
        var vectors = Visible.Where(v => v.Kind == VarKind.Vector).ToList();
        var options = Visible.Where(v => v.Kind == VarKind.Option).ToList();
        var canNest = depth < 3;
        var choice = _random.Next(100);
        switch (choice)
        {
            case < 14:
            {
                var type = AnyScalar();
                var mutable = Chance(0.7);
                var name = Fresh("x");
                Line($"{(mutable ? "var" : "let")} {name}: {type.Name} = {ScalarExpr(type, 0)};");
                Declare(new Var(name, VarKind.Scalar, mutable) { Scalar = type });
                return;
            }
            case < 24 when mutableScalars.Count > 0:
            {
                var v = Pick(mutableScalars);
                Line($"{v.Name} = {ScalarExpr(v.Scalar!, 0)};");
                return;
            }
            case < 30:
            {
                var type = AnyScalar();
                Line($"Console.writeLine({ScalarExpr(type, 0)});");
                return;
            }
            case < 35:
            {
                var s = Pick(_structs);
                var name = Fresh("s");
                Line($"var {name} = {StructLiteral(s)};");
                Declare(new Var(name, VarKind.Struct, true) { Struct = s });
                return;
            }
            case < 41 when mutableStructs.Count > 0:
            {
                var v = Pick(mutableStructs);
                var (field, type) = Pick(v.Struct!.Fields);
                Line($"{v.Name}.{field} = {ScalarExpr(type, 0)};");
                return;
            }
            case < 44:
            {
                var element = AnyInt();
                var length = _random.Next(1, 7);
                var name = Fresh("a");
                Line(Chance(0.5)
                    ? $"var {name}: [{element.Name}; {length}] = [{IntExpr(element, 1)}; {length}];"
                    : $"var {name}: [{element.Name}; {length}] = [{string.Join(", ", Enumerable.Range(0, length).Select(_ => IntExpr(element, 2)))}];");
                Declare(new Var(name, VarKind.Array, true) { Element = element, Length = length });
                return;
            }
            case < 48 when arrays.Count > 0:
            {
                var a = Pick(arrays);
                Line($"{a.Name}[{Index(a.Length.ToString(CultureInfo.InvariantCulture) + "usize")}] = {IntExpr(a.Element!, 0)};");
                return;
            }
            case < 56 when canNest:
                if (Chance(0.5))
                {
                    Nested($"if ({BoolExpr(0)}) {{", depth, close: "} else {");
                    Nested(null, depth);
                }
                else
                {
                    Nested($"if ({BoolExpr(0)}) {{", depth);
                }
                return;
            case < 62 when canNest:
                Loop(depth);
                return;
            case < 66 when enums.Count > 0 && canNest:
                MatchEnum(Pick(enums), depth);
                return;
            case < 69:
            {
                var e = Pick(_enums);
                var name = Fresh("e");
                Line($"var {name} = {EnumValue(e)};");
                Declare(new Var(name, VarKind.Enum, true) { Enum = e });
                return;
            }
            case < 71 when enums.Count > 0:
            {
                var e = Pick(enums);
                Line($"{e.Name} = {EnumValue(e.Enum!)};");
                return;
            }
            case < 74 when canNest:
            {
                var type = AnyInt();
                var values = Enumerable.Range(0, _random.Next(1, 4)).Select(_ => (BigInteger)_random.Next(type.Signed ? -3 : 0, 6)).Distinct().ToList();
                Line($"match ({IntExpr(type, 0)}) {{");
                _indent++;
                foreach (var value in values)
                {
                    Nested($"{value.ToString(CultureInfo.InvariantCulture)} => {{", depth);
                }
                Nested("_ => {", depth);
                _indent--;
                Line("}");
                return;
            }
            case < 79:
                Call(depth);
                return;
            case < 82:
            {
                var s = Pick(_structs);
                var name = Fresh("b");
                Line($"var {name} = Box.create({StructLiteral(s)})!;");
                Declare(new Var(name, VarKind.Struct, true) { Struct = s });
                return;
            }
            case < 84:
            {
                var name = Fresh("d");
                var id = _dropIds++;
                if (Chance(0.4))
                {
                    Line($"let {name} = Box.create(Noisy {{ id: {id} }})!;");
                }
                else
                {
                    Line($"let {name} = Noisy {{ id: {id} }};");
                }
                Declare(new Var(name, VarKind.Drop, false));
                return;
            }
            case < 86:
            {
                var element = AnyInt();
                var name = Fresh("v");
                Line($"var {name} = Vector<{element.Name}>.create();");
                Declare(new Var(name, VarKind.Vector, true) { Element = element, Depth = Depth });
                return;
            }
            case < 90 when vectors.Count > 0:
            {
                var v = Pick(vectors);
                _noVectorReads = true;
                Line($"{v.Name}.push({IntExpr(v.Element!, 0)})!;");
                _noVectorReads = false;
                if (v.Depth == Depth)
                {
                    v.MinLength++;
                }
                return;
            }
            case < 91 when vectors.Count > 0:
            {
                var v = Pick(vectors);
                var name = Fresh("o");
                Line($"let {name} = {v.Name}.pop();");
                v.MinLength = v.Depth == Depth ? Math.Max(0, v.MinLength - 1) : 0;
                Declare(new Var(name, VarKind.Option, false) { Scalar = new Scalar(v.Element) });
                return;
            }
            case < 93:
            {
                var type = AnyInt();
                var name = Fresh("o");
                Line(Chance(0.4) ? $"var {name}: {type.Name}? = null;" : $"var {name}: {type.Name}? = {IntExpr(type, 1)};");
                Declare(new Var(name, VarKind.Option, true) { Scalar = new Scalar(type) });
                return;
            }
            case < 95 when options.Count > 0 && canNest:
            {
                var o = Pick(options);
                var target = mutableScalars.FirstOrDefault(v => v.Scalar == o.Scalar);
                if (Chance(RawChance))
                {
                    Line($"Console.writeLine({o.Name}!);");
                }
                else
                {
                    Nested($"if ({o.Name} != null) {{", depth, () =>
                        Line(target is null ? $"Console.writeLine({o.Name}!);" : $"{target.Name} = {o.Name}!;"));
                }
                return;
            }
            case < 96 when options.Any(o => o.Mutable):
            {
                var o = Pick(options.Where(o => o.Mutable).ToList());
                Line(Chance(0.3) ? $"{o.Name} = null;" : $"{o.Name} = {IntExpr(o.Scalar!.Int!, 0)};");
                return;
            }
            // Early exits are rare in main, so most programs reach the calls of every function at its end.
            case < 98 when canNest && (!_current!.IsMain || Chance(0.1)):
                Nested($"if ({BoolExpr(1)}) {{", depth, () => Line(EarlyExit()));
                return;
            case < 99 when _inLoop:
                Line($"if ({BoolExpr(1)}) {{ {(Chance(0.5) ? "break" : "continue")}; }}");
                return;
            default:
            {
                var type = AnyScalar();
                Line($"Console.writeLine({ScalarExpr(type, 1)});");
                return;
            }
        }
    }

    /// <summary>A return from the current function, or (rarely, in main) Process.exit.</summary>
    private string EarlyExit()
    {
        var function = _current!;
        if (function.IsMain && Chance(0.3))
        {
            return $"Process.exit({_random.Next(0, 100)});";
        }
        return function.IsMain ? $"return {_random.Next(0, 100)};" : function.Return switch
        {
            ReturnKind.Void => "return;",
            ReturnKind.Scalar => $"return {IntExpr(function.ReturnType!.Int!, 1)};",
            ReturnKind.Result => Chance(0.5) ? "return Err(Fail.Bad);" : $"return Err(Fail.Worse({IntExpr(s_i32, 2)}));",
            _ => "return null;",
        };
    }

    private void Loop(int depth)
    {
        var trips = _random.Next(1, 7);
        if (_multiplier * trips > MaxMultiplier)
        {
            trips = 1;
        }
        var (savedMultiplier, savedInLoop) = (_multiplier, _inLoop);
        _multiplier *= trips;
        _inLoop = true;
        var arrays = Visible.Where(v => v.Kind is VarKind.Array or VarKind.Slice).ToList();
        switch (_random.Next(4))
        {
            case 0:
            {
                var i = Fresh("i");
                Nested($"for ({i} in 0..{trips}) {{", depth, () => Declare(new Var(i, VarKind.Scalar, false) { Scalar = new Scalar(s_i32) }));
                break;
            }
            case 1 when arrays.Count > 0:
            {
                var a = Pick(arrays);
                var x = Fresh("y");
                _multiplier = savedMultiplier * Math.Max(1, a.Length == 0 ? 8 : a.Length);
                Nested($"for ({x} in {a.Name}) {{", depth, () => Declare(new Var(x, VarKind.Scalar, false) { Scalar = new Scalar(a.Element) }));
                break;
            }
            case 2:
            {
                var c = Fresh("c");
                Line($"var {c} = 0i32;");
                Nested($"while ({c} < {trips}i32) {{", depth, () => Line($"{c} = {c} + 1i32;"));
                break;
            }
            default:
            {
                var c = Fresh("c");
                Line($"var {c} = 0i32;");
                Nested("loop {", depth, () =>
                {
                    Line($"{c} = {c} + 1i32;");
                    Line($"if ({c} > {trips}i32) {{ break; }}");
                });
                break;
            }
        }
        (_multiplier, _inLoop) = (savedMultiplier, savedInLoop);
    }

    private void MatchEnum(Var e, int depth)
    {
        Line($"match ({e.Name}) {{");
        _indent++;
        var variants = e.Enum!.Variants;
        var covered = Chance(0.7) ? variants.Count : _random.Next(1, variants.Count);
        foreach (var (name, payload) in variants.Take(covered))
        {
            var bindings = payload.Select(p => Chance(0.2) ? ("_", p) : (Fresh("m"), p)).ToList();
            var pattern = payload.Count == 0 ? $"{e.Enum.Name}.{name}" : $"{e.Enum.Name}.{name}({string.Join(", ", bindings.Select(b => b.Item1))})";
            Nested($"{pattern} => {{", depth, () =>
            {
                foreach (var (binding, type) in bindings.Where(b => b.Item1 != "_"))
                {
                    Declare(new Var(binding, VarKind.Scalar, false) { Scalar = type });
                }
            });
        }
        if (covered < variants.Count)
        {
            Nested("_ => {", depth);
        }
        _indent--;
        Line("}");
    }

    /// <summary>A call statement whose result (if any) is used.</summary>
    private void Call(int depth)
    {
        var current = _current!;
        var candidates = _functions.Where(f => f.Index > current.Index && f.Cost * _multiplier <= _budget).ToList();
        if (candidates.Count == 0)
        {
            Line($"Console.writeLine({BoolExpr(1)});");
            return;
        }
        CallFunction(Pick(candidates), depth, provide: false);
    }

    /// <summary>
    /// Calls <paramref name="callee"/> and uses its result. With <paramref name="provide"/>, declares a struct or
    /// array the arguments need if none is in scope; otherwise prints instead when one is missing.
    /// </summary>
    private void CallFunction(Function callee, int depth, bool provide)
    {
        var current = _current!;
        if (provide)
        {
            foreach (var p in callee.Params)
            {
                if (p.Struct is { } st && !Visible.Any(v => v.Kind == VarKind.Struct && v.Struct == st && !v.IsReference && v.Mutable))
                {
                    var structName = Fresh("s");
                    Line($"var {structName} = {StructLiteral(st)};");
                    Declare(new Var(structName, VarKind.Struct, true) { Struct = st });
                }
                if (p.Slice is { } element && !Visible.Any(v => v.Kind == VarKind.Array && v.Element == element))
                {
                    var arrayName = Fresh("a");
                    Line($"var {arrayName}: [{element.Name}; 3] = [{IntExpr(element, 2)}, {IntExpr(element, 2)}, {IntExpr(element, 2)}];");
                    Declare(new Var(arrayName, VarKind.Array, true) { Element = element, Length = 3 });
                }
            }
        }
        if (Arguments(callee) is not { } arguments)
        {
            Line($"Console.writeLine({IntExpr(AnyInt(), 1)});");
            return;
        }
        _budget -= callee.Cost * _multiplier;
        var call = $"{callee.Name}({arguments})";
        var name = Fresh("r");
        switch (callee.Return)
        {
            case ReturnKind.Void:
                Line($"{call};");
                return;
            case ReturnKind.Scalar:
                Line($"let {name} = {call};");
                Line($"Console.writeLine({name});");
                Declare(new Var(name, VarKind.Scalar, false) { Scalar = callee.ReturnType });
                return;
            case ReturnKind.Result when current.Return == ReturnKind.Result && !current.IsMain && Chance(0.6):
                Line($"let {name} = {call}?;");
                Declare(new Var(name, VarKind.Scalar, false) { Scalar = callee.ReturnType });
                return;
            case ReturnKind.Result when Chance(RawChance * 3):
                Line($"let {name} = {call}!;");
                Declare(new Var(name, VarKind.Scalar, false) { Scalar = callee.ReturnType });
                return;
            case ReturnKind.Result:
            {
                Line($"match ({call}) {{");
                _indent++;
                Nested($"Ok({name}) => {{", depth, () => Declare(new Var(name, VarKind.Scalar, false) { Scalar = callee.ReturnType }));
                var error = Fresh("q");
                Nested($"Err({error}) => {{", depth, () =>
                    Line($"match ({error}) {{ Fail.Bad => {{ Console.writeLine(\"bad\"); }} Fail.Worse(w{error}) => {{ Console.writeLine(w{error}); }} }}"));
                _indent--;
                Line("}");
                return;
            }
            case ReturnKind.Option when current.Return == ReturnKind.Option && !current.IsMain && Chance(0.6):
                Line($"let {name} = {call}?;");
                Declare(new Var(name, VarKind.Scalar, false) { Scalar = callee.ReturnType });
                return;
            default:
                Line($"let {name} = {call};");
                Declare(new Var(name, VarKind.Option, false) { Scalar = callee.ReturnType });
                return;
        }
    }

    /// <summary>
    /// Arguments for a call, or null if no suitable struct or array is in scope. A struct is lent last
    /// (<c>mut s</c> or <c>borrow s</c>), and no other argument mentions it.
    /// </summary>
    private string? Arguments(Function callee)
    {
        var parts = new string?[callee.Params.Count];
        var lent = new List<(int Index, string Text, string Var)>();
        foreach (var (p, i) in callee.Params.Select((p, i) => (p, i)))
        {
            if (p.Struct is { } s)
            {
                var owner = Visible.Where(v => v.Kind == VarKind.Struct && v.Struct == s && !v.IsReference && (!p.Mutable || v.Mutable)).ToList();
                if (owner.Count == 0)
                {
                    return null;
                }
                var chosen = Pick(owner);
                lent.Add((i, p.Mutable ? $"mut {chosen.Name}" : $"borrow {chosen.Name}", chosen.Name));
            }
            else if (p.Slice is { } element)
            {
                var arrays = Visible.Where(v => v.Kind == VarKind.Array && v.Element == element).ToList();
                if (arrays.Count == 0)
                {
                    return null;
                }
                parts[i] = $"{Pick(arrays).Name}[..]";
            }
        }
        foreach (var (_, _, name) in lent)
        {
            _excluded.Add(name);
        }
        foreach (var (p, i) in callee.Params.Select((p, i) => (p, i)))
        {
            if (p.Scalar is { } scalar)
            {
                parts[i] = ScalarExpr(scalar, 1);
            }
        }
        _excluded.Clear();
        foreach (var (index, text, _) in lent)
        {
            parts[index] = text;
        }
        return string.Join(", ", parts);
    }

    private string StructLiteral(StructDef s) =>
        $"{s.Name} {{ {string.Join(", ", s.Fields.Select(f => $"{f.Name}: {ScalarExpr(f.Type, 2)}"))} }}";

    private string EnumValue(EnumDef e)
    {
        var (name, payload) = Pick(e.Variants);
        return payload.Count == 0 ? $"{e.Name}.{name}" : $"{e.Name}.{name}({string.Join(", ", payload.Select(p => ScalarExpr(p, 2)))})";
    }

    /// <summary>An index below <paramref name="length"/> (a usize expression), or rarely an unchecked one.</summary>
    private string Index(string length) => Chance(RawChance)
        ? IntExpr(s_usize, 2)
        : $"({IntExpr(s_usize, 2)} % {length})";

    // ---- Expressions ----

    private string ScalarExpr(Scalar type, int depth) => type.IsBool ? BoolExpr(depth) : IntExpr(type.Int!, depth);

    private string BoolExpr(int depth)
    {
        var bools = Visible.Where(v => v.Kind == VarKind.Scalar && v.Scalar!.IsBool).ToList();
        var options = Visible.Where(v => v.Kind == VarKind.Option).ToList();
        switch (depth >= 3 ? _random.Next(3) : _random.Next(9))
        {
            case 0:
                return Chance(0.5) ? "true" : "false";
            case 1 when bools.Count > 0:
                return Pick(bools).Name;
            case 2 when options.Count > 0:
                return $"({Pick(options).Name} {(Chance(0.5) ? "==" : "!=")} null)";
            case 3 or 4 or 5:
            {
                var type = AnyInt();
                var op = Pick(["==", "!=", "<", "<=", ">", ">="]);
                return $"({IntExpr(type, depth + 1)} {op} {IntExpr(type, depth + 1)})";
            }
            case 6:
                return $"({BoolExpr(depth + 1)} {(Chance(0.5) ? "&&" : "||")} {BoolExpr(depth + 1)})";
            case 7:
                return $"!{BoolExpr(depth + 1)}";
            case 8 when FieldOf(null) is { } field:
                return field;
            default:
                return Chance(0.5) ? "true" : "false";
        }
    }

    /// <summary>A struct field of the given scalar type (null: bool) from a struct in scope, or null.</summary>
    private string? FieldOf(IntType? type)
    {
        var fields = Visible.Where(v => v.Kind == VarKind.Struct)
            .SelectMany(v => v.Struct!.Fields.Where(f => f.Type.Int == type).Select(f => $"{v.Name}.{f.Name}"))
            .ToList();
        return fields.Count == 0 ? null : Pick(fields);
    }

    private string IntExpr(IntType type, int depth)
    {
        if (depth >= 4)
        {
            return Literal(type);
        }
        var scalars = Visible.Where(v => v.Kind == VarKind.Scalar && v.Scalar!.Int == type).ToList();
        var arrays = Visible.Where(v => v.Kind is VarKind.Array or VarKind.Slice && v.Element == type).ToList();
        var vectors = _noVectorReads ? [] : Visible.Where(v => v.Kind == VarKind.Vector && v.Element == type && v.MinLength > 0).ToList();
        switch (_random.Next(16))
        {
            case 0 or 1:
                return Literal(type);
            case 2 or 3 when scalars.Count > 0:
                return Pick(scalars).Name;
            case 4 when FieldOf(type) is { } field:
                return field;
            case 5 when arrays.Count > 0:
            {
                var a = Pick(arrays);
                return a.Kind == VarKind.Array
                    ? $"{a.Name}[{Index($"{a.Length}usize")}]"
                    : $"{a.Name}[{Index($"{a.Name}.length")}]";
            }
            case 6 when vectors.Count > 0:
            {
                var v = Pick(vectors);
                return $"{v.Name}[{Index($"{v.Name}.length")}]";
            }
            case 7 or 8 or 9:
            {
                var a = IntExpr(type, depth + 1);
                var b = IntExpr(type, depth + 1);
                var ops = type.Signed ? new[] { "add", "sub", "mul", "div", "rem" } : ["add", "sub", "mul", "div", "rem"];
                var op = Pick(ops);
                if (Chance(RawChance))
                {
                    var symbol = op switch { "add" => "+", "sub" => "-", "mul" => "*", "div" => "/", _ => "%" };
                    return $"({a} {symbol} {b})";
                }
                return $"{op}_{type.Name}({a}, {b})";
            }
            case 10 when type.Signed:
                return Chance(RawChance) ? $"(-{IntExpr(type, depth + 1)})" : $"neg_{type.Name}({IntExpr(type, depth + 1)})";
            case 10:
            {
                var op = Pick(["&", "|", "^"]);
                return $"({IntExpr(type, depth + 1)} {op} {IntExpr(type, depth + 1)})";
            }
            case 11 when !type.Signed:
            {
                var amount = IntExpr(s_u32, depth + 1);
                if (Chance(RawChance))
                {
                    return $"({IntExpr(type, depth + 1)} {(Chance(0.5) ? "<<" : ">>")} {amount})";
                }
                return $"{(Chance(0.5) ? "shl" : "shr")}_{type.Name}({IntExpr(type, depth + 1)}, {amount})";
            }
            case 12:
            {
                var source = AnyInt();
                return $"truncate<{type.Name}>({IntExpr(source, depth + 1)})";
            }
            case 13 when Widens(type) is { } narrower:
                return $"({IntExpr(narrower, depth + 1)} as {type.Name})";
            case 14:
                return $"({BoolExpr(depth + 1)} as {type.Name})";
            case 15 when CallExpr(type, depth) is { } call:
                return call;
            default:
                return Literal(type);
        }
    }

    /// <summary>A type every value of which fits in <paramref name="type"/> (for a lossless <c>as</c>), or null.</summary>
    /// <remarks>type-system.md §7: uN → uM and iN → iM for M &gt; N, uN → iM for M &gt; N; usize is 64 bits wide.</remarks>
    private IntType? Widens(IntType type)
    {
        var narrower = s_ints.Where(t => t.Name != "usize" && t.Bits < type.Bits && (t.Signed == type.Signed || (!t.Signed && type.Signed))).ToList();
        return narrower.Count == 0 ? null : Pick(narrower);
    }

    /// <summary>A call to a later function with scalar parameters returning <paramref name="type"/>, within budget.</summary>
    private string? CallExpr(IntType type, int depth)
    {
        var current = _current!;
        var candidates = _functions.Where(f => f.Index > current.Index && f.Return == ReturnKind.Scalar && f.ReturnType!.Int == type
            && f.ScalarParamsOnly && f.Cost * _multiplier <= _budget).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        var callee = Pick(candidates);
        _budget -= callee.Cost * _multiplier;
        return $"{callee.Name}({string.Join(", ", callee.Params.Select(p => ScalarExpr(p.Scalar!, depth + 1)))})";
    }

    private string Literal(IntType type)
    {
        var edges = new[] { type.Min, type.Max, BigInteger.Zero, BigInteger.One, type.Signed ? BigInteger.MinusOne : type.Max - 1 };
        var value = Chance(0.25) ? Pick(edges) : (BigInteger)_random.Next(type.Signed ? -100 : 0, 100);
        return type.Literal(value);
    }
}
