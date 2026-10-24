using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Nova.Compiler.Hir;
using Nova.Compiler.Nir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Backend.C;

/// <summary>Options of the C emitter.</summary>
/// <param name="LineDirectives">Map function bodies to Nova source with <c>#line</c> (c-backend.md §7).</param>
/// <param name="Placement">Place non-escaping <c>SystemHeap</c> boxes on the stack (M5; c-backend.md §2.2).</param>
/// <param name="Structured">Reconstruct if/else and loops (c-backend.md §3); false emits the flat form (one label per block).</param>
public sealed record CEmitOptions(bool LineDirectives = false, bool Structured = true, bool Placement = false)
{
    public static CEmitOptions Default { get; } = new();
}

/// <summary>
/// Emits C11 from NIR (docs/c-backend.md). Each NIR block becomes a label, terminators become
/// goto/if/return, and every operation that can be undefined in C calls a checked helper from
/// nova/rt.h. Output is deterministic: it depends only on the program and the target.
/// </summary>
public sealed class CEmitter
{
    private readonly NirProgram _program;
    private readonly SymbolTable _symbols;
    private readonly TypeTable _types;
    private readonly Dictionary<SourceText, int> _fileIds = [];
    private readonly StringBuilder _sb = new();
    private readonly CEmitOptions _options;
    private readonly Analysis.PlacementAnalysis? _placement;
    private Analysis.FunctionPlacement? _boxes; // the current function's stack-placed boxes
    private NirFunction? _function;

    // Source mapping (§7): the physical C lines written so far, the Nova line the next C line is
    // attributed to by the last directive, and the line the current statement wants.
    private int _cLines;
    private (SourceText File, int Line)? _mapped;
    private (SourceText File, int Line)? _wanted;
    private string? _pendingLabel; // with directives, a label shares the next statement's line
    private List<string>? _capture; // structured form: statement lines collected for the tree

    private CEmitter(NirProgram program, SymbolTable symbols, TypeTable types, IEnumerable<SourceText> sources, CEmitOptions options)
    {
        _program = program;
        _symbols = symbols;
        _types = types;
        _options = options;
        _placement = options.Placement ? Analysis.PlacementAnalysis.Analyze(program, symbols) : null;
        _usedLibrary = UsedLibraryFunctions(program);
        _usedTypes = UsedTypes();
        // A library file is named only if code from it is emitted (type-system.md §29.2).
        foreach (var source in sources.Where(s => !CoreLibrary.Contains(s) || _usedLibrary.Any(f => f.Source == s)))
        {
            _fileIds.TryAdd(source, _fileIds.Count);
        }
    }

    /// <summary>The lib/core functions that compiled code calls, directly or through other library functions (§29.2).</summary>
    private readonly HashSet<HirFunction> _usedLibrary;

    /// <summary>The types emitted code mentions: a lib/core type is emitted only if it is one (§29.2).</summary>
    private readonly HashSet<NovaType> _usedTypes;

    private static HashSet<HirFunction> UsedLibraryFunctions(NirProgram program)
    {
        var byHir = program.Functions.ToDictionary(f => f.Source);
        var used = new HashSet<HirFunction>();
        var work = new Stack<NirFunction>(program.Functions.Where(f => !f.Source.IsGeneric && !CoreLibrary.Contains(f.Source.Source)));
        while (work.TryPop(out var function))
        {
            foreach (var callee in Callees(function))
            {
                if (CoreLibrary.Contains(callee.Source) && used.Add(callee) && byHir.TryGetValue(callee, out var body))
                {
                    work.Push(body);
                }
            }
        }
        return used;
    }

    private static IEnumerable<HirFunction> Callees(NirFunction function) =>
        function.Blocks.SelectMany(b => b.Statements).SelectMany(s => s switch
        {
            NirAssign { Value: NirCall call } => [call.Callee],
            NirEval { Value: NirCall call } => [call.Callee],
            _ => Array.Empty<HirFunction>(),
        });

    public static string Emit(NirProgram program, SymbolTable symbols, TypeTable types, IEnumerable<SourceText> sources,
        CEmitOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(sources);
        var emitter = new CEmitter(program, symbols, types, sources, options ?? CEmitOptions.Default);
        emitter.EmitProgram();
        return emitter._sb.ToString();
    }

    /// <summary>
    /// Writes one or more C lines. Inside a function with directives on, each line is attributed to
    /// the wanted Nova line: a <c>#line</c> is written first whenever the next line would otherwise map elsewhere.
    /// </summary>
    /// <param name="code">False for lines that generate no code (braces, <c>else</c>): they need no directive of their own.</param>
    private void Line(string text = "", bool code = true)
    {
        if (_capture is not null)
        {
            _capture.Add(text);
            return;
        }
        if (code && _wanted is { } wanted && _mapped != wanted)
        {
            _sb.Append(CultureInfo.InvariantCulture, $"#line {wanted.Line} {CStringLiteral(wanted.File.Path)}\n");
            _cLines++;
            _mapped = wanted;
        }
        if (_pendingLabel is { } label)
        {
            _sb.Append(label);
            text = text.TrimStart();
            _pendingLabel = null;
        }
        _sb.Append(text).Append('\n');
        var lines = 1 + text.Count(c => c == '\n');
        _cLines += lines;
        if (_mapped is { } mapped)
        {
            _mapped = (mapped.File, mapped.Line + lines);
        }
    }

    /// <summary>Attributes the following C lines to the first line of <paramref name="span"/> (§7); spanless code keeps the current line.</summary>
    private void MapTo(TextSpan span)
    {
        if (_options.LineDirectives && _function is { } function && !(span.Start == 0 && span.Length == 0))
        {
            _wanted = (function.Source.Source, function.Source.Source.GetLinePosition(span.Start).Line);
        }
    }

    /// <summary>Ends a function's mapping: the following lines belong to main.c again.</summary>
    private void EndMapping()
    {
        if (_mapped is not null)
        {
            // The directive is physical line _cLines + 1, so the line after it is _cLines + 2.
            _sb.Append(CultureInfo.InvariantCulture, $"#line {_cLines + 2} \"{CProgram.MainFileName}\"\n");
            _cLines++;
        }
        _mapped = null;
        _wanted = null;
    }

    private void EmitProgram()
    {
        Line($"/* Generated by novac {NovaVersion.Text}. Do not edit. */");
        Line("#include \"nova/rt.h\"");
        foreach (var header in _program.Headers)
        {
            Line($"#include \"{header}\""); // imported C types (type-system.md §31.3)
        }
        Line();
        Line($"_Static_assert(sizeof(void *) * 8 == {_types.Target.PointerBits}, \"novac: generated for a {_types.Target.PointerBits}-bit target\");");
        Line();

        foreach (var (source, id) in _fileIds)
        {
            Line($"static NV_UNUSED const char nv_file_{id}[] = {CStringLiteral(source.Path)};");
        }
        Line();

        EmitStructs();
        EmitImportedFieldChecks();
        EmitExterns();
        EmitExportPrototypes();

        foreach (var function in CompiledFunctions)
        {
            Line($"static NV_UNUSED {Signature(function, withNames: false)};");
        }
        EmitDropGluePrototypes();
        EmitBoxCreatePrototypes();
        EmitArenaHelperPrototypes();
        EmitVectorHelperPrototypes();
        EmitContainerHelperPrototypes();
        EmitIoHelperPrototypes();
        foreach (var result in PoolTakeResults)
        {
            Line($"static NV_UNUSED {PoolTakeSignature(result)};");
        }
        Line();

        foreach (var function in CompiledFunctions)
        {
            EmitFunction(function);
            Line();
        }
        EmitDropGlue();
        EmitPlacedHelpers();
        EmitBoxCreate();
        EmitBoxFree();
        EmitArenaHelpers();
        EmitVectorHelpers();
        EmitContainerHelpers();
        EmitIoHelpers();
        EmitPoolTake();

        EmitExports();
        EmitCallbacks();
        EmitMainWrapper();
    }

    /// <summary>
    /// Struct definitions, each after the structs it contains by value, with static assertions that
    /// the C compiler's layout matches Nova's (type-system.md §12).
    /// </summary>
    private void EmitStructs()
    {
        // A box is a pointer, not a C struct; a pool box is a struct of two pointers.
        var aggregates = Aggregates.Where(t => !t.IsBox || t.IsPoolBox).ToList();
        if (aggregates.Count == 0)
        {
            EmitFunctionPointerTypedefs();
            return;
        }
        foreach (var type in aggregates)
        {
            Line($"typedef struct {StructTag(type)} {CTypeName(type)};");
        }
        EmitFunctionPointerTypedefs(); // after the struct names, before the structs that may hold them
        Line();

        var emitted = new HashSet<NovaType>();
        void Emit(NovaType type)
        {
            if (!emitted.Add(type) || type.Struct is { IsImported: true }) // an imported type is the header's (§31.3)
            {
                return;
            }
            if (type.Enum is not null)
            {
                EmitEnum(type, Emit);
                return;
            }
            if (type.Kind is TypeKind.Array or TypeKind.Reference or TypeKind.Vector or TypeKind.StableVector or TypeKind.SlotMap || type.IsPoolBox)
            {
                EmitComposite(type, Emit);
                return;
            }
            var definition = type.Struct!;
            foreach (var field in definition.Fields.Where(f => IsAggregate(f.Type)))
            {
                Emit(field.Type);
            }

            var name = CTypeName(type);
            Line($"struct {StructTag(type)} {{");
            if (definition.Fields.Count == 0)
            {
                Line($"    {AlignPrefix(definition, 0)}uint8_t nv_empty; /* C has no empty structs */");
            }
            foreach (var field in definition.Fields)
            {
                Line($"    {AlignPrefix(definition, field.Index)}{CType(field.Type)} {FieldName(field)};");
            }
            Line("};");
            if (TypeTable.IsCLaidOut(type))
            {
                Line(); // it holds C state: the C compiler lays it out, and Nova has no layout to assert (§33.1)
                return;
            }
            var (size, align) = TypeTable.LayoutOf(type);
            Line($"_Static_assert(sizeof({name}) == {size}, \"novac layout: sizeof({type})\");");
            Line($"_Static_assert(_Alignof({name}) == {align}, \"novac layout: alignof({type})\");");
            foreach (var field in definition.Fields)
            {
                Line($"_Static_assert(offsetof({name}, {FieldName(field)}) == {field.Offset}, \"novac layout: {type}.{_symbols.GetName(field.Name)}\");");
            }
            Line();
        }

        foreach (var type in aggregates)
        {
            Emit(type);
        }
    }

    /// <summary>
    /// Every declared field of an imported C type has exactly its declared type and size in the header (type-system.md
    /// §31.3); neither expression is evaluated.
    /// </summary>
    private void EmitImportedFieldChecks()
    {
        var imported = _program.Structs.Where(t => t.Struct is { IsImported: true, Fields.Count: > 0 }).ToList();
        foreach (var type in imported)
        {
            var definition = type.Struct!;
            foreach (var field in definition.Fields)
            {
                var member = $"((({definition.CSpelling} *)0)->{FieldName(field)})";
                var cType = BoundaryType(field.Type);
                var where = $"{definition.Source.Path}:{definition.Source.GetLinePosition(field.Span.Start).Line}";
                var name = $"{definition.CSpelling}.{FieldName(field)}";
                Line($"_Static_assert(_Generic({member}, {cType}: 1, default: 0), {CStringLiteral($"novac import: {name} is not {cType} ({where})")});");
                if (!TypeTable.IsCLaidOut(field.Type)) // an embedded member has only C's size (§31.1)
                {
                    Line($"_Static_assert(sizeof {member} == {TypeTable.LayoutOf(field.Type).Size}, {CStringLiteral($"novac import: {name} is not {TypeTable.LayoutOf(field.Type).Size} bytes ({where})")});");
                }
            }
        }
        if (imported.Count > 0)
        {
            Line();
        }
    }

    /// <summary>Types defined as C structs, which must be defined before a struct containing them by value.</summary>
    /// <summary>
    /// A type whose C definition must come before a struct that holds it by value: user and built-in aggregates,
    /// and since M6x the composites a field or payload may now be (slices, collections; type-system.md §25.4).
    /// </summary>
    private static bool IsAggregate(NovaType type) =>
        type.Kind is TypeKind.Struct or TypeKind.Enum or TypeKind.Array or TypeKind.Vector or TypeKind.StableVector or TypeKind.SlotMap
        || (type.IsSlice && !type.IsStringView) || type.IsPoolBox;

    /// <summary>
    /// <c>[T; N]</c> is <c>struct { T e[N]; }</c>, so arrays are values in C too; a slice is
    /// <c>struct { T *ptr; uint64_t len; }</c>, with <c>const T</c> for <c>Slice&lt;T&gt;</c> (type-system.md §16.1).
    /// </summary>
    private void EmitComposite(NovaType type, Action<NovaType> emitDependency)
    {
        var name = CTypeName(type);
        if (type.Kind == TypeKind.Array)
        {
            if (IsAggregate(type.Element!))
            {
                emitDependency(type.Element!);
            }
            Line($"struct {name} {{ {CType(type.Element!)} e[{type.ArrayLength}]; }};");
        }
        else if (type.IsPoolBox)
        {
            Line($"struct {name} {{ {CType(type.Pointee!)} *ptr; nv_pool *pool; }};");
        }
        else if (type.IsVector)
        {
            Line($"struct {name} {{ {CType(type.Pointee!.Element!)} *ptr; uint64_t len; uint64_t cap; }};");
        }
        else if (type.IsStableVector)
        {
            Line($"struct {name} {{ void *chunks[NV_SVEC_CHUNKS]; uint64_t len; }};");
        }
        else if (type.Kind == TypeKind.SlotMap)
        {
            // Slots hold values by value: the element type is complete first.
            if (IsAggregate(type.Element!))
            {
                emitDependency(type.Element!);
            }
            var slot = SlotName(type);
            Line($"typedef struct {slot} {{ uint32_t gen; uint32_t next; {CType(type.Element!)} value; }} {slot};");
            Line($"struct {name} {{ {slot} *ptr; uint64_t len; uint64_t cap; uint64_t count; uint32_t free; }};");
        }
        else
        {
            var element = CType(type.Pointee!.Element!);
            Line($"struct {name} {{ {(type.IsMutableReference ? "" : "const ")}{element} *ptr; uint64_t len; }};");
        }
        var (size, align) = TypeTable.LayoutOf(type);
        Line($"_Static_assert(sizeof({name}) == {size}, \"novac layout: sizeof({type})\");");
        Line($"_Static_assert(_Alignof({name}) == {align}, \"novac layout: alignof({type})\");");
        Line();
    }

    /// <summary>An enum: <c>struct { uintN_t tag; union { struct { … } v_V; … } u; }</c> (type-system.md §15.1).</summary>
    private void EmitEnum(NovaType type, Action<NovaType> emitDependency)
    {
        var definition = type.Enum!;
        foreach (var field in definition.Variants.SelectMany(v => v.Payload).Where(f => IsAggregate(f.Type)))
        {
            emitDependency(field.Type);
        }
        var name = CTypeName(type);
        var (size, align, tagSize, unionOffset) = TypeTable.EnumLayout(type);
        Line($"struct {name} {{");
        Line($"    uint{tagSize * 8}_t tag;");
        var withPayload = definition.Variants.Where(v => v.Payload.Count > 0).ToList();
        if (withPayload.Count > 0)
        {
            Line("    union {");
            foreach (var variant in withPayload)
            {
                Line($"        struct {{ {string.Concat(variant.Payload.Select(f => $"{CType(f.Type)} {FieldName(f)}; "))}}} {VariantName(variant)};");
            }
            Line("    } u;");
        }
        Line("};");
        Line($"_Static_assert(sizeof({name}) == {size}, \"novac layout: sizeof({type})\");");
        Line($"_Static_assert(_Alignof({name}) == {align}, \"novac layout: alignof({type})\");");
        if (unionOffset >= 0)
        {
            Line($"_Static_assert(offsetof({name}, u) == {unionOffset}, \"novac layout: {type} payload offset\");");
        }
        Line();
    }

    private string VariantName(EnumVariant variant) => "v_" + _symbols.GetName(variant.Name);

    private string FieldName(StructField field) => (field.HasNovaCName ? "" : "f_") + _symbols.GetName(field.Name);

    /// <summary><c>_Alignas(n) </c> on the first member of an <c>@align(n)</c> struct (type-system.md §29.4).</summary>
    private static string AlignPrefix(StructDefinition definition, int index) =>
        index == 0 && definition.ExplicitAlign > 1 ? $"_Alignas({definition.ExplicitAlign}) " : "";

    /// <summary>The C tag of a struct type: its Nova name for <c>@repr(C)</c> (type-system.md §28.5), else its C type name.</summary>
    private static string StructTag(NovaType type) => type.Struct is { IsReprC: true } ? type.Name : CTypeName(type);

    // ---- C interoperability (type-system.md §28, c-backend.md §2.3) ----

    /// <summary>The C type of a value crossing to or from C (§28.3); in the header, @repr(C) structs are <c>struct S</c>.</summary>
    private string BoundaryType(NovaType type, bool header = false) => type.Kind switch
    {
        TypeKind.Integer when ReferenceEquals(type, _types.USize) => "size_t",
        TypeKind.Integer when ReferenceEquals(type, _types.ISize) => "ptrdiff_t",
        TypeKind.Enum => header ? type.Name : CTypeName(type.Enum!.Repr!), // the header's typedef names the same integer type
        TypeKind.Struct when type.Struct!.IsImported => type.Struct.CSpelling!,
        TypeKind.Struct => header ? $"struct {type.Name}" : CTypeName(type),
        TypeKind.RawPointer => $"{type.Pointee!.Kind switch { TypeKind.CChar => "char", TypeKind.Void => "void", _ => BoundaryType(type.Pointee!, header) }}{(type.IsMutablePointer ? "" : " const")} *",
        _ => CTypeName(type),
    };

    /// <summary>A member of a @repr(C) struct in <c>nova_exports.h</c>: the types the generated code uses (§28.5).</summary>
    private static string HeaderMemberType(NovaType type) => type.Kind switch
    {
        TypeKind.Struct => $"struct {StructTag(type)}", // a @repr(C) struct, or lib/core's Volatile<T> (§29.2)
        TypeKind.FunctionPointer => CTypeName(type), // declared by a typedef earlier in the header
        TypeKind.RawPointer => $"{type.Pointee!.Kind switch { TypeKind.CChar => "char", TypeKind.Void => "void", _ => HeaderMemberType(type.Pointee!) }} *",
        _ => CTypeName(type),
    };

    /// <summary>A Nova value as the C type of §28.3.</summary>
    private string ToBoundary(NovaType type, string value) => type.Kind switch
    {
        TypeKind.RawPointer => $"({BoundaryType(type)})(uintptr_t){value}",
        TypeKind.Integer when type.Bits == 64 && (ReferenceEquals(type, _types.USize) || ReferenceEquals(type, _types.ISize)) => $"({BoundaryType(type)}){value}",
        TypeKind.Enum => $"({BoundaryType(type)}){value}.tag",
        _ => value,
    };

    /// <summary>A C value of the type of §28.3 as Nova's; an enum must have been checked (<see cref="CheckEnumFromC"/>).</summary>
    private string FromBoundary(NovaType type, string value) => type.Kind switch
    {
        TypeKind.RawPointer => $"({CType(type)})(uintptr_t){value}",
        TypeKind.Integer when ReferenceEquals(type, _types.USize) || ReferenceEquals(type, _types.ISize) => $"({CType(type)}){value}",
        TypeKind.Enum => $"(({CType(type)}){{ .tag = (uint{TypeTable.EnumLayout(type).TagSize * 8}_t){value} }})",
        _ => value,
    };

    /// <summary>A @repr enum value from C must be a variant's index (§28.5): panics otherwise.</summary>
    private void CheckEnumFromC(NovaType type, string value, HirFunction function)
    {
        if (type.Kind != TypeKind.Enum)
        {
            return;
        }
        var count = type.Enum!.Variants.Count;
        var repr = type.Enum.Repr!;
        var tests = new List<string>();
        if (repr.IsSigned)
        {
            tests.Add($"{value} < 0");
        }
        if (count - 1 < repr.Max)
        {
            tests.Add($"{value} >= {count}");
        }
        if (tests.Count > 0)
        {
            Line($"    if ({string.Join(" || ", tests)}) nova_panic(NV_PANIC_FFI_ENUM, {Location(function.Source, function.Syntax.Name.Span)});");
        }
    }

    private static string CParameters(HirFunction function, Func<NovaType, string> type, bool withNames) =>
        function.Parameters.IsEmpty ? "void" : string.Join(", ", function.Parameters.Select((p, i) => withNames ? $"{type(p.Type)} a{i}" : type(p.Type)));

    /// <summary>Each extern function's C prototype, and a static wrapper with Nova's types that converts (c-backend §2.3).</summary>
    private void EmitExterns()
    {
        foreach (var function in _program.Externs)
        {
            var name = _symbols.GetName(function.Name);
            Line($"{BoundaryType(function.ReturnType)} {name}({CParameters(function, t => BoundaryType(t), withNames: false)});");
            var parameters = function.Parameters.IsEmpty
                ? "void"
                : string.Join(", ", function.Parameters.Select((p, i) => $"{CType(p.Type)} a{i}"));
            Line($"static NV_UNUSED {CType(function.ReturnType)} nvC_{name}({parameters}) {{");
            var call = $"{name}({string.Join(", ", function.Parameters.Select((p, i) => ToBoundary(p.Type, $"a{i}")))})";
            if (function.ReturnType.Kind == TypeKind.Void)
            {
                Line($"    {call};");
            }
            else
            {
                Line($"    {BoundaryType(function.ReturnType)} r = {call};");
                CheckEnumFromC(function.ReturnType, "r", function);
                Line($"    return {FromBoundary(function.ReturnType, "r")};");
            }
            Line("}");
        }
        if (!_program.Externs.IsEmpty)
        {
            Line();
        }
    }

    private IEnumerable<HirFunction> Exports => CompiledFunctions.Select(f => f.Source).Where(f => f.ExportName is not null);

    /// <summary>The <c>@callback</c> functions whose address the program takes (type-system.md §34.1), in a stable order.</summary>
    private List<HirFunction> Callbacks => CompiledFunctions
        .SelectMany(f => f.Blocks.SelectMany(b => b.Statements))
        .Select(s => s switch { NirAssign { Value: NirFunctionAddress a } => a.Function, NirEval { Value: NirFunctionAddress a } => a.Function, _ => null })
        .OfType<HirFunction>().Where(f => f.IsCallback).Distinct().ToList();

    /// <summary>A callback's C function: <c>nvW_</c> and the Nova function's own name (§34.1).</summary>
    private string CallbackName(HirFunction function) => "nvW_" + FunctionName(function)["nvF_".Length..];

    private string CallbackSignature(HirFunction function) =>
        $"static {BoundaryType(function.ReturnType)} {CallbackName(function)}({CParameters(function, t => BoundaryType(t), withNames: true)})";

    /// <summary>Each callback in use: a static C function with the C types that converts and calls the Nova function.</summary>
    private void EmitCallbacks()
    {
        foreach (var function in Callbacks)
        {
            Line($"{CallbackSignature(function)} {{");
            var call = $"{FunctionName(function)}({string.Join(", ", function.Parameters.Select((p, i) => FromBoundary(p.Type, $"a{i}")))})";
            Line(function.ReturnType.Kind == TypeKind.Void ? $"    {call};" : $"    return {ToBoundary(function.ReturnType, call)};");
            Line("}");
            Line();
        }
    }

    private string ExportSignature(HirFunction function) =>
        $"{BoundaryType(function.ReturnType)} {function.ExportName}({CParameters(function, t => BoundaryType(t), withNames: true)})";

    /// <summary>The exports' prototypes come before every function, so their addresses can be taken anywhere (§29.5).</summary>
    private void EmitExportPrototypes()
    {
        foreach (var function in Exports)
        {
            Line($"{ExportSignature(function)};");
        }
        foreach (var function in Callbacks)
        {
            Line($"{CallbackSignature(function)};");
        }
    }

    /// <summary>
    /// <c>typedef R (*nvP_…)(A, …);</c> for each C function pointer type, with the C types of §28.3. Struct names are
    /// declared already (forward typedefs), and a function declarator may name an incomplete struct type.
    /// </summary>
    private void EmitFunctionPointerTypedefs()
    {
        foreach (var type in _types.FunctionPointers.Where(t => !t.IsGeneric))
        {
            Line($"typedef {BoundaryType(type.ResultType!)} (*{CTypeName(type)})({FunctionPointerParameters(type, header: false)});");
        }
    }

    private string FunctionPointerParameters(NovaType type, bool header) =>
        type.ParameterTypes.IsEmpty ? "void" : string.Join(", ", type.ParameterTypes.Select(p => BoundaryType(p, header)));

    /// <summary>A C-callable wrapper for each <c>@export</c> function, with the C types of §28.3 (c-backend §2.3).</summary>
    private void EmitExports()
    {
        foreach (var function in Exports)
        {
            Line($"{ExportSignature(function)} {{");
            for (var i = 0; i < function.Parameters.Length; i++)
            {
                CheckEnumFromC(function.Parameters[i].Type, $"a{i}", function);
            }
            var call = $"{FunctionName(function)}({string.Join(", ", function.Parameters.Select((p, i) => FromBoundary(p.Type, $"a{i}")))})";
            Line(function.ReturnType.Kind == TypeKind.Void ? $"    {call};" : $"    return {ToBoundary(function.ReturnType, call)};");
            Line("}");
            Line();
        }
    }

    /// <summary>
    /// <c>nova_exports.h</c> (c-backend §2.3): the @repr(C) structs and @repr enums, and the exports' prototypes; null
    /// when the program has none.
    /// </summary>
    public static string? ExportsHeader(NirProgram program, SymbolTable symbols, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(types);
        var emitter = new CEmitter(program, symbols, types, [], CEmitOptions.Default);
        var structs = program.Structs.Where(t => t.Struct is { IsReprC: true }).ToList();
        var enums = program.Structs.Where(t => t.Enum is { Repr: not null }).ToList();
        var exports = program.Functions.Select(f => f.Source).Where(f => f.ExportName is not null && !f.IsGeneric).ToList();
        if (structs.Count == 0 && enums.Count == 0 && exports.Count == 0)
        {
            return null;
        }
        var lines = new List<string>
        {
            $"/* Generated by novac {NovaVersion.Text}. Do not edit. Nova's C interface (type-system.md §28). */",
            "#ifndef NOVA_EXPORTS_H",
            "#define NOVA_EXPORTS_H",
            "",
            "#include <stdbool.h>",
            "#include <stddef.h>",
            "#include <stdint.h>",
        };
        lines.AddRange(program.Headers.Select(h => $"#include \"{h}\"")); // imported C types (type-system.md §31.3)
        lines.Add("");
        // Struct names first: function pointer types may name them, and structs may hold function pointers.
        lines.AddRange(structs.Select(t => $"struct {StructTag(t)};"));
        lines.AddRange(types.FunctionPointers.Where(t => !t.IsGeneric).Select(t =>
            $"typedef {emitter.BoundaryType(t.ResultType!, header: true)} (*{CTypeName(t)})({emitter.FunctionPointerParameters(t, header: true)});"));
        lines.Add("");
        var written = new HashSet<NovaType>();
        void Struct(NovaType type)
        {
            if (!written.Add(type))
            {
                return;
            }
            foreach (var field in type.Struct!.Fields.Where(f => f.Type.Kind == TypeKind.Struct))
            {
                Struct(field.Type);
            }
            lines.Add($"struct {StructTag(type)} {{");
            lines.AddRange(type.Struct.Fields.Select(f =>
                $"    {AlignPrefix(type.Struct, f.Index)}{HeaderMemberType(f.Type)} {(f.HasNovaCName ? "" : "f_")}{symbols.GetName(f.Name)};"));
            lines.Add("};");
            lines.Add("");
        }
        structs.ForEach(Struct);
        foreach (var type in enums)
        {
            lines.Add($"typedef {CTypeName(type.Enum!.Repr!)} {type.Name};");
            lines.Add($"enum {{ {string.Join(", ", type.Enum.Variants.Select(v => $"{type.Name}_{symbols.GetName(v.Name)} = {v.Index}"))} }};");
            lines.Add("");
        }
        foreach (var function in exports)
        {
            lines.Add($"{emitter.BoundaryType(function.ReturnType, header: true)} {function.ExportName}({CParameters(function, t => emitter.BoundaryType(t, header: true), withNames: false)});");
        }
        lines.Add("");
        lines.Add("#endif");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// The drop glue of a type that needs drop: <c>nvD_&lt;length&gt;&lt;Name&gt;</c> for user structs and enums,
    /// <c>nvD_&lt;mangled&gt;</c> for built-in instantiations (which start with a letter, so they cannot collide).
    /// </summary>
    private static string DropGlueName(NovaType type) => type.Kind == TypeKind.Resource ? "nova_fd_drop"
        : type.Kind == TypeKind.Region
            ? type.Region switch { RegionKind.Arena => "nova_arena_drop", RegionKind.Pool => "nova_pool_drop", _ => "nova_scratch_drop" }
        : type.Enum?.ResultTypes is not null || type.Enum?.OptionType is not null || type.IsBox || type.Kind is TypeKind.Vector or TypeKind.StableVector or TypeKind.SlotMap || IsInstance(type) ? $"nvD_{Mangle(type)}" : $"nvD_{type.Name.Length}{type.Name}";

    /// <summary>An instantiation of a generic struct or enum (§20.4); its C names come from <see cref="Mangle"/>.</summary>
    private static bool IsInstance(NovaType type) =>
        !(type.Struct?.TypeArguments.IsEmpty ?? true) || !(type.Enum?.TypeArguments.IsEmpty ?? true);

    /// <summary>The declared name of a struct or enum (an instantiation's name is <c>Base&lt;Args&gt;</c>).</summary>
    private static string BaseName(NovaType type) => type.Name.IndexOf('<', StringComparison.Ordinal) is var i and >= 0 ? type.Name[..i] : type.Name;

    /// <summary>The mangled type arguments of an instantiation: their count, then each (prefix-free) code.</summary>
    private static string MangledArguments(ImmutableArray<NovaType> arguments) =>
        arguments.Length.ToString(CultureInfo.InvariantCulture) + string.Concat(arguments.Select(Mangle));

    /// <summary>
    /// Does destroying a <paramref name="type"/> end, as its last step, in destroying a <paramref name="box"/>?
    /// The last component that needs drop is followed through structs and every enum variant (§18.5).
    /// </summary>
    private static bool LeadsTo(NovaType type, NovaType box)
    {
        if (type == box)
        {
            return true;
        }
        if (type.Struct is { } definition)
        {
            return definition.Fields.LastOrDefault(f => TypeTable.NeedsDrop(f.Type)) is { } last && LeadsTo(last.Type, box);
        }
        if (type.Enum is { } enumDefinition)
        {
            return enumDefinition.Variants.Any(v => v.Payload.LastOrDefault(f => TypeTable.NeedsDrop(f.Type)) is { } last && LeadsTo(last.Type, box));
        }
        return false; // another box, or a type without drop
    }

    /// <summary>
    /// Destroys the <paramref name="type"/> value at <paramref name="place"/> in the order of its drop glue,
    /// except the tail <paramref name="box"/>, whose pointer is stored in <c>next</c> instead.
    /// </summary>
    private void DestroyExceptTail(string place, NovaType type, NovaType box, string indent)
    {
        if (type == box)
        {
            Line($"{indent}next = {place};");
            return;
        }
        void Component(string component, NovaType componentType, bool isTail, string at)
        {
            if (isTail && LeadsTo(componentType, box))
            {
                DestroyExceptTail(component, componentType, box, at);
            }
            else
            {
                Line($"{at}{DropGlueName(componentType)}(&{component});");
            }
        }
        if (type.Struct is { } definition)
        {
            if (definition.DropFunction is { } drop)
            {
                Line($"{indent}{FunctionName(drop)}(&{place});");
            }
            var droppable = definition.Fields.Where(f => TypeTable.NeedsDrop(f.Type)).ToList();
            foreach (var field in droppable)
            {
                Component($"{place}.{FieldName(field)}", field.Type, field == droppable[^1], indent);
            }
            return;
        }
        var enumDefinition = type.Enum!;
        Line($"{indent}switch ({place}.tag) {{");
        foreach (var variant in enumDefinition.Variants.Where(v => v.Payload.Any(f => TypeTable.NeedsDrop(f.Type))))
        {
            Line($"{indent}case {variant.Index}:");
            var droppable = variant.Payload.Where(f => TypeTable.NeedsDrop(f.Type)).ToList();
            foreach (var field in droppable)
            {
                Component($"{place}.u.{VariantName(variant)}.{FieldName(field)}", field.Type, field == droppable[^1], indent + "    ");
            }
            Line($"{indent}    break;");
        }
        Line($"{indent}default:");
        Line($"{indent}    break;");
        Line($"{indent}}}");
    }

    /// <summary>
    /// The <c>Result&lt;borrow mut T, AllocError&gt;</c> and <c>Result&lt;SliceMut&lt;T&gt;, AllocError&gt;</c> types
    /// that arena allocation produces (§19.2), in creation order.
    /// </summary>
    private IEnumerable<NovaType> ArenaResults => Composites.Where(t =>
        t.Enum?.ResultTypes is ({ IsMutableReference: true, Pointee.Kind: not TypeKind.Region }, { Name: "AllocError" }));

    private static string ArenaHelperName(NovaType result)
    {
        var ok = result.Enum!.ResultTypes!.Value.Ok;
        return ok.IsSlice ? $"nvAR_{Mangle(ok.Pointee!.Element!)}" : $"nvAA_{Mangle(ok.Pointee!)}";
    }

    /// <summary>The <c>Result&lt;PoolBox&lt;T&gt;, AllocError&gt;</c> types that <c>pool.take</c> produces (§19.6).</summary>
    private IEnumerable<NovaType> PoolTakeResults => Composites.Where(t => t.Enum?.ResultTypes is ({ IsPoolBox: true }, { Name: "AllocError" }));

    private static string PoolTakeSignature(NovaType result)
    {
        var contents = result.Enum!.ResultTypes!.Value.Ok.Pointee!;
        return $"{CTypeName(result)} nvPT_{Mangle(contents)}(nv_pool *p, {CType(contents)} v)";
    }

    /// <summary><c>pool.take(v)</c>: a free slot, then the value moves in; on failure the value is destroyed.</summary>
    private void EmitPoolTake()
    {
        foreach (var result in PoolTakeResults)
        {
            var box = result.Enum!.ResultTypes!.Value.Ok;
            var contents = CType(box.Pointee!);
            var name = CTypeName(result);
            Line($"static {PoolTakeSignature(result)} {{");
            Line($"    _Static_assert(_Alignof({contents}) <= 16, \"novac: pool slots are 16-aligned\");");
            Line($"    {contents} *slot = ({contents} *)nova_pool_take(p, sizeof({contents}));");
            Line("    if (slot == NULL) {");
            if (TypeTable.NeedsDrop(box.Pointee!))
            {
                Line($"        {DropGlueName(box.Pointee!)}(&v);");
            }
            Line($"        return (({name}){{ .tag = 1, .u.v_Err = {{ .f__0 = (({CTypeName(result.Enum.ResultTypes.Value.Err)}){{ .tag = 0 }}) }} }});");
            Line("    }");
            Line("    *slot = v;");
            Line($"    return (({name}){{ .tag = 0, .u.v_Ok = {{ .f__0 = (({CTypeName(box)}){{ .ptr = slot, .pool = p }}) }} }});");
            Line("}");
            Line();
        }
    }

    /// <summary>The runtime's id for the region a (reference to a) region value names (nova/rt.h).</summary>
    private static string RegionId(NovaType region) => (region.Pointee ?? region).Region switch
    {
        RegionKind.Arena => "NV_REGION_ARENA",
        RegionKind.FixedBuffer => "NV_REGION_FBA",
        _ => "NV_REGION_SCRATCH",
    };

    private static string ArenaHelperSignature(NovaType result)
    {
        var ok = result.Enum!.ResultTypes!.Value.Ok;
        return ok.IsSlice
            ? $"{CTypeName(result)} {ArenaHelperName(result)}(void *r, uint32_t kind, uint64_t n, {CType(ok.Pointee!.Element!)} fill)"
            : $"{CTypeName(result)} {ArenaHelperName(result)}(void *r, uint32_t kind, {CType(ok.Pointee!)} v)";
    }

    private void EmitArenaHelperPrototypes()
    {
        foreach (var result in ArenaResults)
        {
            Line($"static NV_UNUSED {ArenaHelperSignature(result)};");
        }
    }

    /// <summary>
    /// Arena allocation (§19.2): bump-allocate, then move the value in (or fill <c>n</c> copies) and return
    /// <c>Ok</c>; on failure return <c>Err(AllocError.OutOfMemory)</c>. Nothing needs drop (R2).
    /// </summary>
    private void EmitArenaHelpers()
    {
        foreach (var result in ArenaResults)
        {
            var ok = result.Enum!.ResultTypes!.Value.Ok;
            var name = CTypeName(result);
            var error = $"(({name}){{ .tag = 1, .u.v_Err = {{ .f__0 = (({CTypeName(result.Enum.ResultTypes.Value.Err)}){{ .tag = 0 }}) }} }})";
            Line($"static {ArenaHelperSignature(result)} {{");
            if (ok.IsSlice)
            {
                var element = CType(ok.Pointee!.Element!);
                if (TypeTable.LayoutOf(ok.Pointee!.Element!).Size > 1)
                {
                    // Only for elements wider than a byte: GCC's -Wtype-limits flags `n > UINT64_MAX`.
                    Line($"    if (n > UINT64_MAX / sizeof({element})) return {error};");
                }
                Line($"    {element} *p = ({element} *)nova_region_alloc(r, kind, n == 0u ? 1u : n * sizeof({element}), _Alignof({element}));");
                Line($"    if (p == NULL) return {error};");
                Line("    for (uint64_t i = 0; i < n; i++) p[i] = fill;");
                Line($"    return (({name}){{ .tag = 0, .u.v_Ok = {{ .f__0 = (({CTypeName(ok)}){{ .ptr = p, .len = n }}) }} }});");
            }
            else
            {
                var contents = CType(ok.Pointee!);
                Line($"    {contents} *p = ({contents} *)nova_region_alloc(r, kind, sizeof({contents}), _Alignof({contents}));");
                Line($"    if (p == NULL) return {error};");
                Line("    *p = v;");
                Line($"    return (({name}){{ .tag = 0, .u.v_Ok = {{ .f__0 = p }} }});");
            }
            Line("}");
            Line();
        }
    }

    /// <summary>The runtime's id for an allocator type (nova/rt.h).</summary>
    private static string AllocatorId(NovaType allocator) =>
        allocator.Name == "FailingAllocator" ? "NV_ALLOC_FAILING" : "NV_ALLOC_SYSTEM";

    /// <summary>The <c>Result&lt;Box&lt;T, A&gt;, AllocError&gt;</c> types that <c>Box.create</c> produces, in creation order.</summary>
    private IEnumerable<NovaType> BoxCreateResults => Composites.Where(t => t.Enum?.ResultTypes is ({ IsBox: true, IsPoolBox: false }, { Name: "AllocError" }));

    private static string BoxCreateName(NovaType result) => $"nvB_{Mangle(result.Enum!.ResultTypes!.Value.Ok)}";

    private void EmitBoxCreatePrototypes()
    {
        foreach (var result in BoxCreateResults)
        {
            var contents = result.Enum!.ResultTypes!.Value.Ok.Pointee!;
            Line($"static NV_UNUSED {CTypeName(result)} {BoxCreateName(result)}({CType(contents)});");
        }
        foreach (var box in UserAllocatedBoxes)
        {
            Line($"static NV_UNUSED void {BoxFreeName(box)}({CTypeName(box)} p);");
        }
    }

    /// <summary>Boxes whose allocator is user-defined (type-system.md §30): their memory comes from Nova methods.</summary>
    private IEnumerable<NovaType> UserAllocatedBoxes =>
        Aggregates.Concat(BoxCreateResults.Select(r => r.Enum!.ResultTypes!.Value.Ok)).Where(t => t is { IsBox: true, IsPoolBox: false } && IsUserAllocator(t.Allocator!))
            .Distinct();

    private static bool IsUserAllocator(NovaType allocator) => allocator.Struct?.AllocateMethod is not null;

    private static string BoxFreeName(NovaType box) => $"nvBF_{Mangle(box)}";

    /// <summary>The <c>Layout</c> of a box's contents, as the lib/core struct's C literal (§30.3).</summary>
    private string LayoutOf(NovaType allocator, NovaType contents)
    {
        var layout = allocator.Struct!.AllocateMethod!.Parameters[1].Type;
        var fields = layout.Struct!.Fields;
        return $"(({CTypeName(layout)}){{ .{FieldName(fields[0])} = sizeof({CType(contents)}), .{FieldName(fields[1])} = _Alignof({CType(contents)}) }})";
    }

    /// <summary>Frees a user-allocated box's memory: its allocator's <c>deallocate</c> (§30.3).</summary>
    private void EmitBoxFree()
    {
        foreach (var box in UserAllocatedBoxes)
        {
            var allocator = box.Allocator!;
            Line($"static void {BoxFreeName(box)}({CTypeName(box)} p) {{");
            Line($"    {CTypeName(allocator)} a = {{ 0 }};");
            Line($"    {FunctionName(allocator.Struct!.DeallocateMethod!)}(&a, (uint8_t *)(uintptr_t)p, {LayoutOf(allocator, box.Pointee!)});");
            Line("}");
            Line();
        }
    }

    /// <summary>Does placing a value of this type run any <c>@placed</c> method (type-system.md §33.3)?</summary>
    private static bool HasPlacedMethods(NovaType type) =>
        type.Struct is { IsImported: false } definition && TypeTable.IsCLaidOut(type)
        && (definition.PlacedMethod is not null || definition.Fields.Any(f => HasPlacedMethods(f.Type)));

    private static string PlacedName(NovaType type) => $"nvPL_{Mangle(type)}";

    /// <summary>The C-laid-out types whose placement runs <c>@placed</c> methods: box contents and their fields' types.</summary>
    private List<NovaType> PlacedTypes()
    {
        var result = new List<NovaType>();
        void Visit(NovaType type)
        {
            if (!HasPlacedMethods(type) || result.Contains(type))
            {
                return;
            }
            foreach (var field in type.Struct!.Fields)
            {
                Visit(field.Type);
            }
            result.Add(type);
        }
        foreach (var result1 in BoxCreateResults)
        {
            Visit(result1.Enum!.ResultTypes!.Value.Ok.Pointee!);
        }
        return result;
    }

    /// <summary><c>nvPL_&lt;T&gt;(T *p)</c>: the fields' placement initialisers, in field order, then the type's own (§33.3).</summary>
    private void EmitPlacedHelpers()
    {
        var types = PlacedTypes();
        foreach (var type in types)
        {
            Line($"static void {PlacedName(type)}({CType(type)} *p);");
        }
        foreach (var type in types)
        {
            var definition = type.Struct!;
            Line($"static void {PlacedName(type)}({CType(type)} *p) {{");
            foreach (var field in definition.Fields.Where(f => HasPlacedMethods(f.Type)))
            {
                Line($"    {PlacedName(field.Type)}(&(*p).{FieldName(field)});");
            }
            if (definition.PlacedMethod is { } placed)
            {
                Line($"    {FunctionName(placed)}(p);");
            }
            Line("}");
            Line();
        }
    }

    /// <summary>
    /// <c>Box.create</c> (type-system.md §18.2): allocate, then move the value in and return <c>Ok(box)</c>;
    /// on failure destroy the value and return <c>Err(AllocError.OutOfMemory)</c>.
    /// </summary>
    private void EmitBoxCreate()
    {
        foreach (var result in BoxCreateResults)
        {
            var box = result.Enum!.ResultTypes!.Value.Ok;
            var contents = box.Pointee!;
            var name = CTypeName(result);
            var errorType = result.Enum.ResultTypes.Value.Err;
            Line($"static {name} {BoxCreateName(result)}({CType(contents)} v) {{");
            if (IsUserAllocator(box.Allocator!))
            {
                // The allocator's own `allocate` (§30.3); a field-less struct's value carries nothing.
                var allocate = box.Allocator!.Struct!.AllocateMethod!;
                Line($"    {CTypeName(box.Allocator!)} a = {{ 0 }};");
                Line($"    {CTypeName(allocate.ReturnType)} r = {FunctionName(allocate)}(&a, {LayoutOf(box.Allocator!, contents)});");
                Line($"    {CType(contents)} *p = r.tag == 0 ? ({CType(contents)} *)(uintptr_t)r.u.v_Ok.f__0 : NULL;");
            }
            else
            {
                Line($"    _Static_assert(_Alignof({CType(contents)}) <= _Alignof(nv_max_align_t), \"novac: {box} needs over-aligned memory\");");
                Line($"    {CType(contents)} *p = ({CType(contents)} *)nova_alloc(sizeof({CType(contents)}), {AllocatorId(box.Allocator!)});");
            }
            Line("    if (p == NULL) {");
            if (TypeTable.NeedsDrop(contents))
            {
                Line($"        {DropGlueName(contents)}(&v);");
            }
            Line($"        return (({name}){{ .tag = 1, .u.v_Err = {{ .f__0 = (({CTypeName(errorType)}){{ .tag = 0 }}) }} }});");
            Line("    }");
            Line("    *p = v;");
            if (HasPlacedMethods(contents))
            {
                Line($"    {PlacedName(contents)}(p); /* C state is initialised at its final address (type-system.md §33.3) */");
            }
            Line($"    return (({name}){{ .tag = 0, .u.v_Ok = {{ .f__0 = p }} }});");
            Line("}");
            Line();
        }
    }

    private IEnumerable<NovaType> Vectors => Composites.Where(t => t.IsVector);

    /// <summary>The <c>Result&lt;Vector&lt;T, A&gt;, AllocError&gt;</c> types that <c>withCapacity</c> produces.</summary>
    private IEnumerable<NovaType> VectorWithCapacityResults => Composites.Where(t => t.Enum?.ResultTypes is ({ IsVector: true }, { Name: "AllocError" }));

    private NovaType VoidAllocResult => Composites.First(t => t.Enum?.ResultTypes is ({ Kind: TypeKind.Void }, { Name: "AllocError" }));

    private static string IoHelper(NovaType result) => $"nvIOR_{Mangle(result)}";

    /// <summary>The <c>Result&lt;X, IoError&gt;</c> types that I/O built-ins produce (§26).</summary>
    private IEnumerable<NovaType> IoResults => Composites.Where(t => t.Enum?.ResultTypes is (_, { Name: "IoError" }));

    private void EmitIoHelperPrototypes()
    {
        foreach (var result in IoResults)
        {
            Line($"static NV_UNUSED {CTypeName(result)} {IoHelper(result)}(nv_io_result);");
        }
        if (OptionOfView is { } option)
        {
            Line($"static NV_UNUSED {CTypeName(option)} nvARG(uint64_t);");
        }
    }

    private NovaType? OptionOfView => _types.Composites.FirstOrDefault(t => t.Enum?.OptionType is { IsStringView: true });

    /// <summary>
    /// The conversions from the runtime's <c>nv_io_result</c> to Nova results (§26): <c>Err</c> with the
    /// <c>IoError</c> variant for the errno (its number kept for <c>Other</c>), or <c>Ok</c> with the value.
    /// </summary>
    private void EmitIoHelpers()
    {
        foreach (var result in IoResults)
        {
            var (ok, error) = result.Enum!.ResultTypes!.Value;
            var name = CTypeName(result);
            var other = error.Enum!.Variants[^1];
            Line($"static {name} {IoHelper(result)}(nv_io_result r) {{");
            Line("    if (r.error != 0) {");
            var (_, _, tagSize, _) = TypeTable.EnumLayout(error);
            Line($"        {CTypeName(error)} e = {{ .tag = (uint{tagSize * 8}_t)nova_io_kind(r.error) }};");
            Line($"        if (e.tag == NV_IO_OTHER) e.u.{VariantName(other)}.{FieldName(other.Payload[0])} = r.error;");
            Line($"        return (({name}){{ .tag = 1, .u.v_Err = {{ .f__0 = e }} }});");
            Line("    }");
            Line(ok.Kind == TypeKind.Void
                ? $"    return (({name}){{ .tag = 0 }});"
                : $"    return (({name}){{ .tag = 0, .u.v_Ok = {{ .f__0 = ({CType(ok)})r.value }} }});");
            Line("}");
            Line();
        }
        if (OptionOfView is { } option)
        {
            var name = CTypeName(option);
            Line($"static {name} nvARG(uint64_t index) {{");
            Line("    nv_str text;");
            Line($"    if (!nova_arg(index, &text)) return (({name}){{ .tag = 0 }});");
            Line($"    return (({name}){{ .tag = 1, .u.v_Some = {{ .f__0 = text }} }});");
            Line("}");
            Line();
        }
    }

    private static string SlotName(NovaType map) => $"nvKS_{Mangle(map)}";

    private IEnumerable<NovaType> Containers => Composites.Where(t => t.Kind is TypeKind.StableVector or TypeKind.SlotMap);

    private static string AllocErrorResult(NovaType result) =>
        $"(({CTypeName(result)}){{ .tag = 1, .u.v_Err = {{ .f__0 = (({CTypeName(result.Enum!.ResultTypes!.Value.Err)}){{ .tag = 0 }}) }} }})";

    private NovaType ResultOfOk(NovaType ok) => Composites.First(t => t.Enum?.ResultTypes is var (o, e) && o == ok && e.Name == "AllocError");

    private NovaType OptionFor(NovaType value) => _types.Composites.First(t => t.Enum?.OptionType == value);

    private void EmitContainerHelperPrototypes()
    {
        foreach (var type in Containers)
        {
            var (name, m) = (CTypeName(type), Mangle(type));
            if (type.IsStableVector)
            {
                var element = CType(type.Pointee!.Element!);
                Line($"static NV_UNUSED {element} *nvSVE_{m}({name} *, uint64_t);");
                Line($"static NV_UNUSED {CTypeName(ResultOfOk(_types.USize))} nvSVP_{m}({name} *, {element});");
                continue;
            }
            var value = type.Element!;
            Line($"static NV_UNUSED bool nvKV_{m}(const {name} *, nv_handle);");
            Line($"static NV_UNUSED {CTypeName(ResultOfOk(_types.HandleOf(value)))} nvKI_{m}({name} *, {CType(value)});");
            Line($"static NV_UNUSED {CTypeName(OptionFor(_types.ReferenceTo(value, false)))} nvKG_{m}(const {name} *, nv_handle);");
            Line($"static NV_UNUSED {CTypeName(OptionFor(_types.ReferenceTo(value, true)))} nvKM_{m}({name} *, nv_handle);");
            Line($"static NV_UNUSED {CTypeName(OptionFor(value))} nvKR_{m}({name} *, nv_handle);");
        }
    }

    /// <summary>
    /// Stable vectors (§24.2: chunks from <c>nova_svec_slot</c>, never moved) and slot maps (§24.1: slots with
    /// generations, odd while live; a slot whose generation would wrap is retired, never reused).
    /// </summary>
    private void EmitContainerHelpers()
    {
        foreach (var type in Containers)
        {
            var (name, m) = (CTypeName(type), Mangle(type));
            var allocator = AllocatorId(type.Allocator!);
            if (type.IsStableVector)
            {
                var elementType = type.Pointee!.Element!;
                var element = CType(elementType);
                var result = ResultOfOk(_types.USize);
                Line($"static {element} *nvSVE_{m}({name} *v, uint64_t i) {{");
                Line($"    return ({element} *)nova_svec_at(v->chunks, i, sizeof({element}));");
                Line("}");
                Line();
                Line($"static {CTypeName(result)} nvSVP_{m}({name} *v, {element} x) {{");
                Line($"    _Static_assert(_Alignof({element}) <= _Alignof(nv_max_align_t), \"novac: {type} needs over-aligned memory\");");
                Line($"    {element} *slot = ({element} *)nova_svec_slot(v->chunks, v->len, sizeof({element}), {allocator});");
                Line("    if (slot == NULL) {");
                if (TypeTable.NeedsDrop(elementType))
                {
                    Line($"        {DropGlueName(elementType)}(&x);");
                }
                Line($"        return {AllocErrorResult(result)};");
                Line("    }");
                Line("    *slot = x;");
                Line("    v->len += 1u;");
                Line($"    return (({CTypeName(result)}){{ .tag = 0, .u.v_Ok = {{ .f__0 = v->len - 1u }} }});");
                Line("}");
                Line();
                continue;
            }
            var valueType = type.Element!;
            var value = CType(valueType);
            var slot = SlotName(type);
            var insertResult = ResultOfOk(_types.HandleOf(valueType));
            Line($"static bool nvKV_{m}(const {name} *map, nv_handle h) {{");
            Line("    return h.index < map->len && map->ptr[h.index].gen == h.gen;");
            Line("}");
            Line();
            Line($"static {CTypeName(insertResult)} nvKI_{m}({name} *map, {value} v) {{");
            Line($"    _Static_assert(_Alignof({slot}) <= _Alignof(nv_max_align_t), \"novac: {type} needs over-aligned memory\");");
            Line("    uint32_t index;");
            Line("    if (map->free != UINT32_MAX) {");
            Line("        index = map->free;");
            Line("        map->free = map->ptr[index].next;");
            Line("    } else {");
            Line($"        void *p = map->len < UINT32_MAX - 1u ? nova_vec_grow(map->ptr, map->len, &map->cap, 1u, sizeof({slot}), {allocator}) : NULL;");
            Line("        if (p == NULL) {");
            if (TypeTable.NeedsDrop(valueType))
            {
                Line($"            {DropGlueName(valueType)}(&v);");
            }
            Line($"            return {AllocErrorResult(insertResult)};");
            Line("        }");
            Line($"        map->ptr = ({slot} *)p;");
            Line("        index = (uint32_t)map->len;");
            Line("        map->ptr[index].gen = 0u;");
            Line("        map->len += 1u;");
            Line("    }");
            Line("    map->ptr[index].gen += 1u;");
            Line("    map->ptr[index].value = v;");
            Line("    map->count += 1u;");
            Line($"    return (({CTypeName(insertResult)}){{ .tag = 0, .u.v_Ok = {{ .f__0 = ((nv_handle){{ .index = index, .gen = map->ptr[index].gen }}) }} }});");
            Line("}");
            Line();
            foreach (var (letter, mutable) in new[] { ("G", false), ("M", true) })
            {
                var option = CTypeName(OptionFor(_types.ReferenceTo(valueType, mutable)));
                Line($"static {option} nvK{letter}_{m}({(mutable ? "" : "const ")}{name} *map, nv_handle h) {{");
                Line($"    if (!nvKV_{m}(map, h)) return (({option}){{ .tag = 0 }});");
                Line($"    return (({option}){{ .tag = 1, .u.v_Some = {{ .f__0 = &map->ptr[h.index].value }} }});");
                Line("}");
                Line();
            }
            var removed = CTypeName(OptionFor(valueType));
            Line($"static {removed} nvKR_{m}({name} *map, nv_handle h) {{");
            Line($"    if (!nvKV_{m}(map, h)) return (({removed}){{ .tag = 0 }});");
            Line("    map->ptr[h.index].gen += 1u;");
            Line("    map->count -= 1u;");
            Line("    if (map->ptr[h.index].gen != UINT32_MAX - 1u) {");
            Line("        map->ptr[h.index].next = map->free;");
            Line("        map->free = h.index;");
            Line("    }");
            Line($"    return (({removed}){{ .tag = 1, .u.v_Some = {{ .f__0 = map->ptr[h.index].value }} }});");
            Line("}");
            Line();
        }
    }

    private void EmitVectorHelperPrototypes()
    {
        foreach (var vector in Vectors)
        {
            var (name, element, m) = (CTypeName(vector), CType(vector.Pointee!.Element!), Mangle(vector));
            if (vector.IsString)
            {
                Line($"static NV_UNUSED {CTypeName(VoidAllocResult)} nvVA_{m}({name} *, nv_str);");
            }
            else
            {
                Line($"static NV_UNUSED {CTypeName(VoidAllocResult)} nvVP_{m}({name} *, {element});");
                Line($"static NV_UNUSED {CTypeName(_types.Composites.First(t => t.Enum?.OptionType == vector.Pointee!.Element))} nvVO_{m}({name} *);");
            }
            Line($"static NV_UNUSED void nvVC_{m}({name} *);");
        }
        foreach (var result in VectorWithCapacityResults)
        {
            var vector = result.Enum!.ResultTypes!.Value.Ok;
            Line($"static NV_UNUSED {CTypeName(result)} nvVW_{Mangle(vector)}(uint64_t);");
        }
    }

    /// <summary>
    /// The vector and string operations (type-system.md §23): grow with <c>nova_vec_grow</c>, which leaves
    /// the vector unchanged on failure; <c>push</c> then destroys the value it was given.
    /// </summary>
    private void EmitVectorHelpers()
    {
        foreach (var vector in Vectors)
        {
            var (name, elementType, m) = (CTypeName(vector), vector.Pointee!.Element!, Mangle(vector));
            var element = CType(elementType);
            var allocator = AllocatorId(vector.Allocator!);
            var result = CTypeName(VoidAllocResult);
            var error = $"(({result}){{ .tag = 1, .u.v_Err = {{ .f__0 = (({CTypeName(VoidAllocResult.Enum!.ResultTypes!.Value.Err)}){{ .tag = 0 }}) }} }})";
            if (vector.IsString)
            {
                Line($"static {result} nvVA_{m}({name} *s, nv_str text) {{");
                Line($"    void *p = nova_vec_grow(s->ptr, s->len, &s->cap, text.len, 1u, {allocator});");
                Line($"    if (p == NULL) return {error};");
                Line("    s->ptr = (uint8_t *)p;");
                Line("    for (uint64_t i = 0; i < text.len; i++) s->ptr[s->len + i] = text.ptr[i];");
                Line("    s->len += text.len;");
                Line($"    return (({result}){{ .tag = 0 }});");
                Line("}");
                Line();
            }
            else
            {
                var option = CTypeName(_types.Composites.First(t => t.Enum?.OptionType == elementType));
                Line($"static {result} nvVP_{m}({name} *v, {element} x) {{");
                Line($"    _Static_assert(_Alignof({element}) <= _Alignof(nv_max_align_t), \"novac: {vector} needs over-aligned memory\");");
                Line($"    void *p = nova_vec_grow(v->ptr, v->len, &v->cap, 1u, sizeof({element}), {allocator});");
                Line("    if (p == NULL) {");
                if (TypeTable.NeedsDrop(elementType))
                {
                    Line($"        {DropGlueName(elementType)}(&x);");
                }
                Line($"        return {error};");
                Line("    }");
                Line($"    v->ptr = ({element} *)p;");
                Line("    v->ptr[v->len] = x;");
                Line("    v->len += 1u;");
                Line($"    return (({result}){{ .tag = 0 }});");
                Line("}");
                Line();
                Line($"static {option} nvVO_{m}({name} *v) {{");
                Line($"    if (v->len == 0u) return (({option}){{ .tag = 0 }});");
                Line("    v->len -= 1u;");
                Line($"    return (({option}){{ .tag = 1, .u.v_Some = {{ .f__0 = v->ptr[v->len] }} }});");
                Line("}");
                Line();
            }
            Line($"static void nvVC_{m}({name} *v) {{");
            Line("    uint64_t n = v->len;");
            Line("    v->len = 0u;");
            if (TypeTable.NeedsDrop(elementType))
            {
                Line("    for (uint64_t i = 0; i < n; i++) {");
                Line($"        {DropGlueName(elementType)}(&v->ptr[i]);");
                Line("    }");
            }
            else
            {
                Line("    (void)n;");
            }
            Line("}");
            Line();
        }
        foreach (var resultType in VectorWithCapacityResults)
        {
            var vector = resultType.Enum!.ResultTypes!.Value.Ok;
            var (name, element) = (CTypeName(resultType), CType(vector.Pointee!.Element!));
            Line($"static {name} nvVW_{Mangle(vector)}(uint64_t n) {{");
            Line($"    {CTypeName(vector)} v = {{ .ptr = ({element} *)(void *)&nova_vec_empty, .len = 0u, .cap = 0u }};");
            Line($"    void *p = nova_vec_grow(v.ptr, 0u, &v.cap, n, sizeof({element}), {AllocatorId(vector.Allocator!)});");
            Line($"    if (p == NULL) return (({name}){{ .tag = 1, .u.v_Err = {{ .f__0 = (({CTypeName(resultType.Enum.ResultTypes.Value.Err)}){{ .tag = 0 }}) }} }});");
            Line($"    v.ptr = ({element} *)p;");
            Line($"    return (({name}){{ .tag = 0, .u.v_Ok = {{ .f__0 = v }} }});");
            Line("}");
            Line();
        }
    }

    /// <summary>User structs and enums, then the built-in instantiations, in a deterministic order.</summary>
    private IEnumerable<NovaType> Aggregates => _program.Structs.Where(t => (!IsLibraryType(t) || _usedTypes.Contains(t)) && t.Struct is not { IsImported: true }).Concat(Composites);

    private static bool IsLibraryType(NovaType type) =>
        (type.Struct?.Source is { } s && CoreLibrary.Contains(s)) || (type.Enum?.Source is { } e && CoreLibrary.Contains(e));

    /// <summary>Every type the emitted functions' locals, parameters and results mention, through fields, payloads and pointees.</summary>
    private HashSet<NovaType> UsedTypes()
    {
        var used = new HashSet<NovaType>();
        void Add(NovaType? type)
        {
            if (type is null || !used.Add(type))
            {
                return;
            }
            Add(type.Pointee);
            Add(type.Element);
            foreach (var field in type.Struct?.Fields ?? [])
            {
                Add(field.Type);
            }
            foreach (var payload in type.Enum?.Variants.SelectMany(v => v.Payload) ?? [])
            {
                Add(payload.Type);
            }
        }
        foreach (var function in CompiledFunctions)
        {
            Add(function.ReturnType);
            foreach (var local in function.Locals)
            {
                Add(local.Type);
            }
        }
        foreach (var function in _program.Externs)
        {
            Add(function.ReturnType);
            foreach (var parameter in function.Parameters)
            {
                Add(parameter.Type);
            }
        }
        return used;
    }

    /// <summary>The built-in instantiations (arrays, slices, results, boxes) that generic code does not mention.</summary>
    // `nv_str` (StringView) is defined by the runtime header.
    private IEnumerable<NovaType> Composites => _types.Composites.Where(t => !t.IsGeneric && !t.IsStringView);

    private void EmitDropGluePrototypes()
    {
        foreach (var type in Aggregates.Where(TypeTable.NeedsDrop))
        {
            Line($"static NV_UNUSED void {DropGlueName(type)}({CTypeName(type)} *);");
        }
    }

    private void EmitDropGlue()
    {
        foreach (var type in Aggregates.Where(TypeTable.NeedsDrop))
        {
            if (type.IsPoolBox)
            {
                // Destroys the value, then returns its slot to the pool (§19.6).
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
                if (TypeTable.NeedsDrop(type.Pointee!))
                {
                    Line($"    {DropGlueName(type.Pointee!)}(p->ptr);");
                }
                Line("    nova_pool_release(p->pool, p->ptr);");
                Line("}");
                Line();
                continue;
            }
            if (type.IsBox && LeadsTo(type.Pointee!, type))
            {
                // Tail iteration (type-system.md §18.5): destroy the node except its tail, free it,
                // continue with the tail box. Only the time of the free differs from recursion.
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *pp) {{");
                Line($"    {CTypeName(type)} p = *pp;");
                Line("    while (p != NULL) {");
                Line($"        {CTypeName(type)} next = NULL;");
                DestroyExceptTail("(*p)", type.Pointee!, type, "        ");
                Line(IsUserAllocator(type.Allocator!) ? $"        {BoxFreeName(type)}(p);" : $"        nova_free(p, {AllocatorId(type.Allocator!)});");
                Line("        p = next;");
                Line("    }");
                Line("}");
                Line();
                continue;
            }
            if (type.IsStableVector)
            {
                // Destroys the elements from index 0 upward, then frees the chunks (§24.2).
                var element = type.Pointee!.Element!;
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
                if (TypeTable.NeedsDrop(element))
                {
                    Line("    for (uint64_t i = 0; i < p->len; i++) {");
                    Line($"        {DropGlueName(element)}(nvSVE_{Mangle(type)}(p, i));");
                    Line("    }");
                }
                Line($"    nova_svec_free(p->chunks, {AllocatorId(type.Allocator!)});");
                Line("}");
                Line();
                continue;
            }
            if (type.Kind == TypeKind.SlotMap)
            {
                // Destroys the live values (odd generations), then frees the slots (§24.1).
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
                if (TypeTable.NeedsDrop(type.Element!))
                {
                    Line("    for (uint64_t i = 0; i < p->len; i++) {");
                    Line($"        if ((p->ptr[i].gen & 1u) != 0u) {DropGlueName(type.Element!)}(&p->ptr[i].value);");
                    Line("    }");
                }
                Line($"    if (p->cap != 0u) nova_free(p->ptr, {AllocatorId(type.Allocator!)});");
                Line("}");
                Line();
                continue;
            }
            if (type.IsVector)
            {
                // Destroys the elements from index 0 upward, then returns the buffer (§23.1).
                var element = type.Pointee!.Element!;
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
                if (TypeTable.NeedsDrop(element))
                {
                    Line("    for (uint64_t i = 0; i < p->len; i++) {");
                    Line($"        {DropGlueName(element)}(&p->ptr[i]);");
                    Line("    }");
                }
                Line($"    if (p->cap != 0u) nova_free(p->ptr, {AllocatorId(type.Allocator!)});");
                Line("}");
                Line();
                continue;
            }
            if (type.IsBox)
            {
                // Destroys the contents, then returns the memory to the box's allocator (H1).
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
                if (TypeTable.NeedsDrop(type.Pointee!))
                {
                    Line($"    {DropGlueName(type.Pointee!)}(*p);");
                }
                Line(IsUserAllocator(type.Allocator!) ? $"    {BoxFreeName(type)}(*p);" : $"    nova_free(*p, {AllocatorId(type.Allocator!)});");
                Line("}");
                Line();
                continue;
            }
            if (type.Enum is { } enumDefinition)
            {
                // Destroys the payloads of the active variant.
                Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
                Line("    switch (p->tag) {");
                foreach (var variant in enumDefinition.Variants.Where(v => v.Payload.Any(f => TypeTable.NeedsDrop(f.Type))))
                {
                    Line($"    case {variant.Index}:");
                    foreach (var field in variant.Payload.Where(f => TypeTable.NeedsDrop(f.Type)))
                    {
                        Line($"        {DropGlueName(field.Type)}(&p->u.{VariantName(variant)}.{FieldName(field)});");
                    }
                    Line("        break;");
                }
                Line("    default:");
                Line("        break;");
                Line("    }");
                Line("}");
                Line();
                continue;
            }
            var definition = type.Struct!;
            Line($"static void {DropGlueName(type)}({CTypeName(type)} *p) {{");
            if (definition.DropFunction is { } drop)
            {
                Line($"    {FunctionName(drop)}(p);");
            }
            foreach (var field in definition.Fields.Where(f => TypeTable.NeedsDrop(f.Type)))
            {
                Line($"    {DropGlueName(field.Type)}(&(*p).{FieldName(field)});");
            }
            Line("}");
            Line();
        }
    }

    private string FunctionName(NirFunction function) => FunctionName(function.Source);

    /// <summary>
    /// Top-level functions are <c>nvF_name</c>; struct members are <c>nvM_&lt;length&gt;&lt;Struct&gt;_name</c>.
    /// Every generated name starts with <c>nv</c> and a capital letter, which the runtime never uses: its own
    /// names are <c>nv_</c>, <c>nova_</c> and <c>NV_</c> (c-backend.md §2.1). A function <c>add_i32</c> is not <c>nv_add_i32</c>.
    /// The length prefix makes the struct/member boundary unambiguous, so the names cannot collide.
    /// </summary>
    /// <remarks>
    /// Instantiations (§20.4): members of a generic struct's instance are <c>nvM_&lt;mangled owner&gt;_name</c>
    /// (the code starts with a letter, so it cannot collide with the length-prefixed form); a generic
    /// function's instance is <c>nvG_&lt;length&gt;&lt;name&gt;&lt;mangled arguments&gt;</c>.
    /// </remarks>
    private string FunctionName(HirFunction function) => function.IsExtern ? "nvC_" + _symbols.GetName(function.Name) : function.Owner is { } owner
        ? IsInstance(owner)
            ? $"nvM_{Mangle(owner)}_{_symbols.GetName(function.Name)}"
            : $"nvM_{owner.Name.Length}{owner.Name}_{_symbols.GetName(function.Name)}"
        : function.TypeArguments.IsEmpty
            ? "nvF_" + _symbols.GetName(function.Name)
            : $"nvG_{_symbols.GetName(function.Name).Length}{_symbols.GetName(function.Name)}{MangledArguments(function.TypeArguments)}";

    /// <summary>The functions compiled to C: everything but generic versions (§20.3).</summary>
    private IEnumerable<NirFunction> CompiledFunctions =>
        _program.Functions.Where(f => !f.Source.IsGeneric && (!CoreLibrary.Contains(f.Source.Source) || _usedLibrary.Contains(f.Source)));

    private string Signature(NirFunction function, bool withNames)
    {
        var parameters = function.ParameterCount == 0
            ? "void"
            : string.Join(", ", function.Locals.Take(function.ParameterCount)
                .Select(p => withNames ? $"NV_UNUSED {CType(p.Type)} {LocalName(p)}" : CType(p.Type)));
        return $"{CType(function.ReturnType)} {FunctionName(function)}({parameters})";
    }

    private void EmitFunction(NirFunction function)
    {
        _function = function;
        _boxes = _placement?.For(function);
        MapTo(function.Source.Syntax.Name.Span);
        Line($"static {Signature(function, withNames: true)} {{");
        var declarations = new List<string>();
        foreach (var local in function.Locals.Skip(function.ParameterCount))
        {
            // Zero-initialised: Nova's definite assignment already forbids reading it first.
            var zero = local.Type.Kind switch
            {
                TypeKind.Bool => "false",
                TypeKind.Struct or TypeKind.Enum or TypeKind.Array or TypeKind.Region or TypeKind.Vector
                    or TypeKind.StableVector or TypeKind.SlotMap or TypeKind.Handle => "{0}",
                TypeKind.Reference when local.Type.IsSlice => "{0}",
                TypeKind.Box when local.Type.IsPoolBox => "{0}",
                TypeKind.Reference or TypeKind.Box => "NULL",
                _ => "0",
            };
            declarations.Add($"NV_UNUSED {CType(local.Type)} {LocalName(local)} = {zero};");
        }
        for (var slot = 0; slot < (_boxes?.Slots.Count ?? 0); slot++)
        {
            // The storage of a stack-placed box (§2.2).
            declarations.Add($"NV_UNUSED {CType(_boxes!.Slots[slot])} nv_box_{slot} = {{0}};");
        }
        // With directives, the declarations share one line, attributed to the function's name.
        if (_options.LineDirectives)
        {
            if (declarations.Count > 0)
            {
                Line("    " + string.Join(" ", declarations));
            }
        }
        else
        {
            foreach (var declaration in declarations)
            {
                Line("    " + declaration);
            }
        }

        var tree = _options.Structured
            ? CStructurer.Structure(function, StatementNodes, (_, terminator) => Captured(() => EmitTerminator(function, terminator), terminator.Span, completes: false),
                Operand, SwitchCases)
            : null;
        if (tree is not null)
        {
            Print(tree, 1);
        }
        else
        {
            EmitFlatBody(function);
        }
        Line("}");
        EndMapping();
        _function = null;
    }

    /// <summary>The flat form (c-backend.md §3): one label per jump target, terminators as goto/switch/return.</summary>
    private void EmitFlatBody(NirFunction function)
    {
        var targeted = new HashSet<NirBlock>();
        foreach (var block in function.Blocks)
        {
            targeted.UnionWith(block.Terminator!.Successors);
        }

        foreach (var block in function.Blocks)
        {
            if (targeted.Contains(block) && _options.LineDirectives)
            {
                _pendingLabel = $"bb{block.Id}: ";
            }
            else if (targeted.Contains(block))
            {
                Line($"bb{block.Id}:");
            }
            foreach (var statement in block.Statements)
            {
                MapTo(statement.Span);
                EmitStatement(statement);
            }
            MapTo(block.Terminator!.Span);
            EmitTerminator(function, block.Terminator!);
        }
    }

    /// <summary>The block's statements that print something (a label before nothing would end its block, a C23 extension).</summary>
    private List<CNode> StatementNodes(NirBlock block) =>
        [.. block.Statements.Select(statement => Captured(() => EmitStatement(statement), statement.Span, completes: true)).Where(c => c.Lines.Count > 0)];

    private CCode Captured(Action emit, TextSpan span, bool completes)
    {
        _capture = [];
        emit();
        var lines = _capture;
        _capture = null;
        return new CCode(lines, completes) { Span = span };
    }

    private (string Value, IEnumerable<(string Label, NirBlock Target)> Cases) SwitchCases(NirSwitch s)
    {
        // C rejects switching on a bool (-Wswitch-bool): switch on its int value instead.
        var isBool = s.Value.Type.Kind == TypeKind.Bool;
        var cases = s.Cases.Select(c => (isBool ? c.Value.ToString(CultureInfo.InvariantCulture) : Constant(c.Value, s.Value.Type), c.Target));
        return (isBool ? $"(int){Operand(s.Value)}" : Operand(s.Value), [.. cases, ("default", s.Otherwise)]);
    }

    /// <summary>Prints a structured statement tree (c-backend.md §3) at the given depth.</summary>
    private void Print(List<CNode> list, int depth)
    {
        var indent = new string(' ', 4 * depth);
        for (var i = 0; i < list.Count; i++)
        {
            switch (list[i])
            {
                case CLabel label when i + 1 < list.Count && _options.LineDirectives:
                    _pendingLabel = (_pendingLabel ?? "") + $"bb{label.Block}: ";
                    break;
                case CLabel label:
                    Line(i + 1 < list.Count ? $"bb{label.Block}:" : $"bb{label.Block}: ;", code: false);
                    break;
                case CCode code:
                    MapTo(code.Span);
                    foreach (var line in code.Lines)
                    {
                        Line(indent + line.TrimStart());
                    }
                    break;
                case CGoto g:
                    MapTo(g.Span);
                    Line($"{indent}goto bb{g.Block};");
                    break;
                case CBreak b:
                    MapTo(b.Span);
                    Line($"{indent}break;");
                    break;
                case CContinue c:
                    MapTo(c.Span);
                    Line($"{indent}continue;");
                    break;
                case CIf branch:
                {
                    MapTo(branch.Span);
                    Line($"{indent}if ({branch.Condition}) {{");
                    var current = branch;
                    while (true)
                    {
                        Print(current.Then, depth + 1);
                        if (current.Else is [CIf nested])
                        {
                            MapTo(nested.Span);
                            Line($"{indent}}} else if ({nested.Condition}) {{");
                            current = nested;
                            continue;
                        }
                        if (current.Else.Count > 0)
                        {
                            Line($"{indent}}} else {{", code: false);
                            Print(current.Else, depth + 1);
                        }
                        break;
                    }
                    Line($"{indent}}}", code: false);
                    break;
                }
                case CLoop loop:
                    Line($"{indent}for (;;) {{", code: false);
                    Print(loop.Body, depth + 1);
                    Line($"{indent}}}", code: false);
                    break;
                case CSwitch choice:
                    MapTo(choice.Span);
                    Line($"{indent}switch ({choice.Value}) {{");
                    foreach (var (labels, body) in choice.Cases)
                    {
                        foreach (var label in labels)
                        {
                            Line(label == "default" ? $"{indent}default:" : $"{indent}case {label}:", code: false);
                        }
                        Print(body, depth + 1);
                    }
                    Line($"{indent}}}", code: false);
                    break;
            }
        }
    }

    private void EmitStatement(NirStatement statement)
    {
        switch (statement)
        {
            case NirAssign { Value: NirBuiltin { Function: BuiltinFunction.ScratchEnter } } enter:
                Line($"    {Place(enter.Target)} = nova_scratch_enter_root({Location(_function!.Source.Source, enter.Span)});");
                break;
            case NirAssign { Value: NirArrayRepeat repeat } assign:
                Line($"    for (uint64_t nv_i = 0; nv_i < {repeat.Type.ArrayLength}u; nv_i++) {Place(assign.Target)}.e[nv_i] = {Operand(repeat.Value)};");
                break;
            case NirAssign { Value: NirBuiltin { Function: BuiltinFunction.BoxCreate } create } site when _boxes is not null && _boxes.IsSite(site, out var slot):
            {
                // A stack-placed box (§2.2): the value goes into the slot, and nothing is allocated.
                Line($"    nv_box_{slot} = {Operand(create.Arguments[^1])};");
                Line($"    {Place(site.Target)} = (({CTypeName(create.Type)}){{ .tag = 0, .u.v_Ok = {{ .f__0 = &nv_box_{slot} }} }});");
                break;
            }
            case NirAssign assign:
            {
                var target = Place(assign.Target);
                var value = IsBoundaryField(assign.Target) ? ToBoundary(assign.Target.Type, Rvalue(assign.Value)) : Rvalue(assign.Value);
                // `x = x;` is a no-op in Nova; in C it trips -Wself-assign (c-backend.md §5).
                if (value != target)
                {
                    Line($"    {target} = {value};");
                }
                break;
            }
            case NirUnpack unpack:
                for (var i = 0; i < unpack.Targets.Length; i++)
                {
                    if (unpack.Targets[i] is { } target)
                    {
                        Line($"    {LocalName(target)} = {Place(unpack.Source)}.u.{VariantName(unpack.Variant)}.{FieldName(unpack.Variant.Payload[i])};");
                    }
                }
                break;
            case NirBoxFree free when IsPlacedBox(free.Box):
                break; // a stack-placed box's memory is the frame's (§2.2)
            case NirBoxFree free:
            {
                var box = Place(free.Box);
                var release = free.Box.Type.IsPoolBox
                    ? $"nova_pool_release({box}.pool, {box}.ptr);"
                    : IsUserAllocator(free.Box.Type.Allocator!) ? $"{BoxFreeName(free.Box.Type)}({box});"
                    : $"nova_free({box}, {AllocatorId(free.Box.Type.Allocator!)});";
                Line(free.Flag is null ? $"    {release}" : $"    if ({LocalName(free.Flag)}) {release}");
                break;
            }
            case NirBoundsCheck check:
                Line($"    nv_check_index({Operand(check.Index)}, {Operand(check.Length)}, {Location(_function!.Source.Source, check.Span)});");
                break;
            case NirRangeCheck { Text: { } text } check:
                // The text of a view or of a string: both have the bytes' (ptr, len).
                Line($"    nv_check_str_range(((nv_str){{ .ptr = {Place(text)}.ptr, .len = {Place(text)}.len }}), {Operand(check.Low)}, {Operand(check.High)}, {Location(_function!.Source.Source, check.Span)});");
                break;
            case NirRangeCheck check:
                Line($"    nv_check_range({Operand(check.Low)}, {Operand(check.High)}, {Operand(check.Length)}, {Location(_function!.Source.Source, check.Span)});");
                break;
            case NirStorageDead:
                break; // storage is the C local's; destruction is an explicit NirDrop
            case NirDrop drop when IsPlacedBox(drop.Place):
                // A stack-placed box: destroy the contents, free nothing (§2.2).
                if (TypeTable.NeedsDrop(drop.Place.Type.Pointee!))
                {
                    var contents = $"{DropGlueName(drop.Place.Type.Pointee!)}({Place(drop.Place)});";
                    Line(drop.Flag is null ? $"    {contents}" : $"    if ({LocalName(drop.Flag)}) {contents}");
                }
                break;
            case NirDrop { Flag: null } drop:
                Line($"    {DropGlueName(drop.Place.Type)}(&{Place(drop.Place)});");
                break;
            case NirDrop drop:
                Line($"    if ({LocalName(drop.Flag!)}) {DropGlueName(drop.Place.Type)}(&{Place(drop.Place)});");
                break;
            case NirEval { Value: NirBuiltin builtin }:
                EmitBuiltin(builtin);
                break;
            case NirEval { Value: NirUse discarded }:
                Line($"    (void){Operand(discarded.Operand)};"); // e.g. the Copy payload of a discarded `e!`
                break;
            case NirEval { Value: NirCall call }:
                Line(call.Type.Kind == TypeKind.Void ? $"    {Rvalue(call)};" : $"    (void){Rvalue(call)};");
                break;
            default:
                throw new InvalidOperationException($"cannot emit statement {statement}");
        }
    }

    private bool IsPlacedBox(NirPlace place) => place.IsLocal && _boxes is not null && _boxes.BoxLocals.Contains(place.Local);

    private void EmitBuiltin(NirBuiltin builtin)
    {
        switch (builtin.Function)
        {
            case BuiltinFunction.VectorPush or BuiltinFunction.StringAppend or BuiltinFunction.VectorPop
                or BuiltinFunction.VectorClear or BuiltinFunction.VectorWithCapacity or BuiltinFunction.StableVectorPush
                or BuiltinFunction.SlotMapInsert or BuiltinFunction.SlotMapGet or BuiltinFunction.SlotMapRemove
                or BuiltinFunction.SlotMapContains or BuiltinFunction.FileOpen or BuiltinFunction.FileCreate or BuiltinFunction.FileRemove
                or BuiltinFunction.ResourceRead or BuiltinFunction.ResourceWrite or BuiltinFunction.ConsoleRead
                or BuiltinFunction.TcpListen or BuiltinFunction.TcpConnect or BuiltinFunction.TcpPort or BuiltinFunction.TcpAccept
                or BuiltinFunction.TcpShutdownWrite or BuiltinFunction.Argument or BuiltinFunction.ArgumentCount or BuiltinFunction.ProcessRun
                or BuiltinFunction.IoErrorMessage or BuiltinFunction.PtrAdd or BuiltinFunction.PtrSub or BuiltinFunction.PtrOffset
                or BuiltinFunction.PtrIsNull or BuiltinFunction.SlicePtr or BuiltinFunction.SliceFromRaw or BuiltinFunction.PtrReadVolatile
                or BuiltinFunction.PtrWriteVolatile or BuiltinFunction.SpinHint or BuiltinFunction.TargetIsX8664 or BuiltinFunction.TargetIsAArch64
                or BuiltinFunction.CallPointer:
                Line(builtin.Type.Kind == TypeKind.Void ? $"    {Rvalue(builtin)};" : $"    (void){Rvalue(builtin)};");
                return;
            case BuiltinFunction.FailAfter:
                Line($"    nova_fail_after({Operand(builtin.Arguments[0])});");
                return;
            case BuiltinFunction.FailReset:
                Line("    nova_fail_reset();");
                return;
            case BuiltinFunction.ArenaReset:
                Line($"    nova_arena_reset({Operand(builtin.Arguments[0])});");
                return;
            case BuiltinFunction.ConsoleWriteBytes:
                Line($"    nova_write_bytes({Operand(builtin.Arguments[0])}.ptr, {Operand(builtin.Arguments[0])}.len);");
                return;
            case BuiltinFunction.ConsoleWriteError or BuiltinFunction.ConsoleWriteErrorLine:
                foreach (var text in builtin.Arguments)
                {
                    Line($"    nova_write_error({Operand(text)});");
                }
                if (builtin.Function == BuiltinFunction.ConsoleWriteErrorLine)
                {
                    Line("    nova_write_error(((nv_str){ .ptr = (const uint8_t *)\"\\n\", .len = 1u }));");
                }
                return;
        }
        foreach (var argument in builtin.Arguments)
        {
            Line(argument switch
            {
                NirString s => $"    nova_write_str({CStringLiteral(s.Value)}, {Encoding.UTF8.GetByteCount(s.Value)}u);",
                _ when argument.Type.Kind == TypeKind.Bool => $"    nova_write_bool({Operand(argument)});",
                _ when argument.Type.IsStringView => $"    nova_write_view({Operand(argument)});",
                _ when argument.Type.IsSigned => $"    nova_write_i64((int64_t){Operand(argument)});",
                _ => $"    nova_write_u64((uint64_t){Operand(argument)});",
            });
        }
        if (builtin.Function == BuiltinFunction.ConsoleWriteLine)
        {
            Line("    nova_write_newline();");
        }
    }

    private void EmitTerminator(NirFunction function, NirTerminator terminator)
    {
        switch (terminator)
        {
            case NirGoto g:
                Line($"    goto bb{g.Target.Id};");
                break;
            case NirBranch b:
                Line($"    if ({Operand(b.Condition)}) goto bb{b.Then.Id}; else goto bb{b.Else.Id};");
                break;
            case NirSwitch s:
            {
                // C rejects switching on a bool (-Wswitch-bool): switch on its int value instead.
                var isBool = s.Value.Type.Kind == TypeKind.Bool;
                Line(isBool ? $"    switch ((int){Operand(s.Value)}) {{" : $"    switch ({Operand(s.Value)}) {{");
                foreach (var (value, target) in s.Cases)
                {
                    var label = isBool ? value.ToString(CultureInfo.InvariantCulture) : Constant(value, s.Value.Type);
                    Line($"    case {label}: goto bb{target.Id};");
                }
                Line($"    default: goto bb{s.Otherwise.Id};");
                Line("    }");
                break;
            }
            case NirReturn { Value: null }:
                Line("    return;");
                break;
            case NirReturn r:
                Line($"    return {Operand(r.Value)};");
                break;
            case NirExit exit:
                Line($"    nova_exit({Operand(exit.Code)});");
                break;
            case NirPanic panic:
                Line($"    nova_panic({(panic.Kind == NirPanicKind.UnwrapNull ? "NV_PANIC_UNWRAP_NULL" : "NV_PANIC_UNWRAP")}, {Location(function.Source.Source, terminator.Span)});");
                break;
            case NirUnreachable:
                Line($"    nova_panic(NV_PANIC_UNREACHABLE, {Location(function.Source.Source, function.Source.Syntax.Name.Span)});");
                break;
            default:
                throw new InvalidOperationException($"cannot emit terminator {terminator}");
        }
    }

    /// <summary><c>p.add(n)</c>, <c>sub</c>, <c>offset</c>: address arithmetic modulo 2^64, never C pointer arithmetic (unsafe.md §4).</summary>
    private string PointerStep(NirBuiltin step)
    {
        var pointer = step.Arguments[0];
        var op = step.Function == BuiltinFunction.PtrSub ? "-" : "+";
        return $"(({CType(step.Type)})((uintptr_t){Operand(pointer)} {op} (uintptr_t){Operand(step.Arguments[1])} * sizeof({CType(pointer.Type.Pointee!)})))";
    }

    private string Rvalue(NirRvalue rvalue) => rvalue switch
    {
        NirUse use => Operand(use.Operand),
        NirBinary binary => Binary(binary),
        NirUnary { Op: UnaryOp.Not } not => $"!{Operand(not.Operand)}",
        NirUnary negate => $"nv_neg_{Suffix(negate.Type)}({Operand(negate.Operand)}, {Location(negate.Source, negate.Span)})",
        // `s.bytes()`: the same pointer and length as a `Slice<u8>` (§22.2).
        NirCast { Operand.Type.IsStringView: true } bytes =>
            $"(({CType(bytes.Type)}){{ .ptr = {Operand(bytes.Operand)}.ptr, .len = {Operand(bytes.Operand)}.len }})",
        // Pointers convert through uintptr_t: no pointer-to-pointer cast, so no -Wcast-qual or -Wcast-align (unsafe.md §4).
        NirCast { Type.IsRawPointer: true } cast => $"(({CType(cast.Type)})(uintptr_t){Operand(cast.Operand)})",
        NirCast { Operand.Type.IsRawPointer: true, Type.IsInteger: true } cast => $"((uint64_t)(uintptr_t){Operand(cast.Operand)})",
        NirCast { Type.IsFunctionPointer: true } cast => $"(({CType(cast.Type)})(uintptr_t){Operand(cast.Operand)})",
        NirCast { Operand.Type.IsFunctionPointer: true } cast => $"((uint64_t)(uintptr_t){Operand(cast.Operand)})",
        NirCast cast => $"({CType(cast.Type)}){Operand(cast.Operand)}",
        NirTruncate truncate => Truncate(truncate),
        NirCall call => $"{FunctionName(call.Callee)}({string.Join(", ", call.Arguments.Select(Operand))})",
        NirAggregate aggregate => Aggregate(aggregate),
        // A slice reborrow (or SliceMut to Slice): the place of `s.*` is the slice `s` itself.
        NirRef { Place.Type.IsUnsized: true } reborrow =>
            $"(({CTypeName(reborrow.Type)}){{ .ptr = {Place(reborrow.Place)}.ptr, .len = {Place(reborrow.Place)}.len }})",
        NirRef reference => $"&{Place(reference.Place)}",
        NirSlice slice => Slice(slice),
        NirLength length => $"{Place(length.Slice)}.len",
        NirBuiltin { Function: BuiltinFunction.ArenaCreate } arena =>
            $"nova_arena_create({AllocatorId(arena.Arguments[0].Type)}, {Operand(arena.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.ArenaAlloc } alloc =>
            $"{ArenaHelperName(alloc.Type)}({Operand(alloc.Arguments[0])}, {RegionId(alloc.Arguments[0].Type)}, {Operand(alloc.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.ArenaAllocArray } alloc =>
            $"{ArenaHelperName(alloc.Type)}({Operand(alloc.Arguments[0])}, {RegionId(alloc.Arguments[0].Type)}, {Operand(alloc.Arguments[1])}, {Operand(alloc.Arguments[2])})",
        NirBuiltin { Function: BuiltinFunction.FixedBufferOver } over =>
            $"nova_fba_over({Operand(over.Arguments[0])}.ptr, {Operand(over.Arguments[0])}.len)",
        NirBuiltin { Function: BuiltinFunction.PoolCreate } pool =>
            $"nova_pool_create({AllocatorId(pool.Arguments[0].Type)}, (uint64_t){Operand(pool.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.PoolTake } take => $"nvPT_{Mangle(take.Arguments[1].Type)}({Operand(take.Arguments[0])}, {Operand(take.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.ScratchNest } nest => $"nova_scratch_enter_child({Operand(nest.Arguments[0])})",
        NirBuiltin { Function: BuiltinFunction.FileOpen or BuiltinFunction.FileCreate } open =>
            $"{IoHelper(open.Type)}(nova_file_open({Operand(open.Arguments[0])}, {(open.Function == BuiltinFunction.FileOpen ? "0u" : "1u")}))",
        NirBuiltin { Function: BuiltinFunction.FileRemove } remove => $"{IoHelper(remove.Type)}(nova_file_remove({Operand(remove.Arguments[0])}))",
        NirBuiltin { Function: BuiltinFunction.ResourceRead } read =>
            $"{IoHelper(read.Type)}(nova_fd_read(*{Operand(read.Arguments[0])}, {Operand(read.Arguments[1])}.ptr, {Operand(read.Arguments[1])}.len))",
        NirBuiltin { Function: BuiltinFunction.ConsoleRead } read =>
            $"{IoHelper(read.Type)}(nova_fd_read(0, {Operand(read.Arguments[0])}.ptr, {Operand(read.Arguments[0])}.len))",
        NirBuiltin { Function: BuiltinFunction.ResourceWrite } write =>
            $"{IoHelper(write.Type)}(nova_fd_write(*{Operand(write.Arguments[0])}, {Operand(write.Arguments[1])}.ptr, {Operand(write.Arguments[1])}.len))",
        NirBuiltin { Function: BuiltinFunction.TcpListen or BuiltinFunction.TcpConnect } open =>
            $"{IoHelper(open.Type)}(nova_tcp_{(open.Function == BuiltinFunction.TcpListen ? "listen" : "connect")}({Operand(open.Arguments[0])}, {Operand(open.Arguments[1])}))",
        NirBuiltin { Function: BuiltinFunction.TcpPort } port => $"{IoHelper(port.Type)}(nova_tcp_port(*{Operand(port.Arguments[0])}))",
        NirBuiltin { Function: BuiltinFunction.TcpAccept } accept => $"{IoHelper(accept.Type)}(nova_tcp_accept(*{Operand(accept.Arguments[0])}))",
        NirBuiltin { Function: BuiltinFunction.TcpShutdownWrite } shutdown =>
            $"{IoHelper(shutdown.Type)}(nova_tcp_shutdown_write(*{Operand(shutdown.Arguments[0])}))",
        NirBuiltin { Function: BuiltinFunction.IoErrorMessage } message => $"nova_io_message((uint32_t){Operand(message.Arguments[0])}.tag)",
        NirBuiltin { Function: BuiltinFunction.ArgumentCount } => "nova_arg_count()",
        NirBuiltin { Function: BuiltinFunction.PtrAdd or BuiltinFunction.PtrSub or BuiltinFunction.PtrOffset } step => PointerStep(step),
        NirBuiltin { Function: BuiltinFunction.PtrIsNull } test => $"({Operand(test.Arguments[0])} == NULL)",
        // A box is a pointer to its contents in C (c-backend.md §2), so these are casts (type-system.md §34.2).
        NirBuiltin { Function: BuiltinFunction.BoxIntoRaw or BuiltinFunction.BoxFromRaw } raw => $"(({CType(raw.Type)}){Operand(raw.Arguments[0])})",
        // Volatile accesses (type-system.md §29.1): adding the qualifier needs no cast through uintptr_t.
        NirBuiltin { Function: BuiltinFunction.PtrReadVolatile } read => $"(*(volatile {CType(read.Type)} *){Operand(read.Arguments[0])})",
        NirBuiltin { Function: BuiltinFunction.PtrWriteVolatile } write =>
            $"(*(volatile {CType(write.Arguments[0].Type.Pointee!)} *){Operand(write.Arguments[0])} = {Operand(write.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.SpinHint } => "nv_spin_hint()",
        // A call through a C function pointer converts like an extern wrapper (c-backend §2.3); no @repr enums cross (§29.5).
        NirBuiltin { Function: BuiltinFunction.CallPointer } call => FromBoundary(call.Type,
            $"{Operand(call.Arguments[0])}({string.Join(", ", call.Arguments.Skip(1).Select(a => ToBoundary(a.Type, Operand(a))))})"),
        NirFunctionAddress address => address.Function.IsExtern ? _symbols.GetName(address.Function.Name)
            : address.Function.ExportName ?? CallbackName(address.Function),
        NirBuiltin { Function: BuiltinFunction.TargetIsX8664 } => "nv_target_is_x86_64()",
        NirBuiltin { Function: BuiltinFunction.TargetIsAArch64 } => "nv_target_is_aarch64()",
        NirBuiltin { Function: BuiltinFunction.SlicePtr } address => $"(({CType(address.Type)})(uintptr_t){Operand(address.Arguments[0])}.ptr)",
        NirBuiltin { Function: BuiltinFunction.SliceFromRaw } raw =>
            $"(({CTypeName(raw.Type)}){{ .ptr = {Operand(raw.Arguments[0])}, .len = {Operand(raw.Arguments[1])} }})",
        NirBuiltin { Function: BuiltinFunction.ProcessRun } run =>
            $"{IoHelper(run.Type)}(nova_process_run({Operand(run.Arguments[0])}, {Operand(run.Arguments[1])}->ptr, {Operand(run.Arguments[1])}->len))",
        NirBuiltin { Function: BuiltinFunction.Argument } argument => $"nvARG({Operand(argument.Arguments[0])})",
        NirBuiltin { Function: BuiltinFunction.VectorCreate, Type.IsStableVector: true } create => $"(({CTypeName(create.Type)}){{ .len = 0u }})",
        NirBuiltin { Function: BuiltinFunction.VectorCreate, Type.Kind: TypeKind.SlotMap } create =>
            $"(({CTypeName(create.Type)}){{ .ptr = ({SlotName(create.Type)} *)(void *)&nova_vec_empty, .len = 0u, .cap = 0u, .count = 0u, .free = UINT32_MAX }})",
        NirBuiltin { Function: BuiltinFunction.StableVectorPush } push =>
            $"nvSVP_{Mangle(push.Arguments[0].Type.Pointee!)}({Operand(push.Arguments[0])}, {Operand(push.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.SlotMapInsert } insert =>
            $"nvKI_{Mangle(insert.Arguments[0].Type.Pointee!)}({Operand(insert.Arguments[0])}, {Operand(insert.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.SlotMapGet } get =>
            $"nvK{(get.Arguments[0].Type.IsMutableReference ? "M" : "G")}_{Mangle(get.Arguments[0].Type.Pointee!)}({Operand(get.Arguments[0])}, {Operand(get.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.SlotMapRemove } remove =>
            $"nvKR_{Mangle(remove.Arguments[0].Type.Pointee!)}({Operand(remove.Arguments[0])}, {Operand(remove.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.SlotMapContains } contains =>
            $"nvKV_{Mangle(contains.Arguments[0].Type.Pointee!)}({Operand(contains.Arguments[0])}, {Operand(contains.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.SlotMapLength } length => $"{Operand(length.Arguments[0])}->count",
        NirBuiltin { Function: BuiltinFunction.VectorCreate } create =>
            $"(({CTypeName(create.Type)}){{ .ptr = ({CType(create.Type.Pointee!.Element!)} *)(void *)&nova_vec_empty, .len = 0u, .cap = 0u }})",
        NirBuiltin { Function: BuiltinFunction.VectorWithCapacity } create =>
            $"nvVW_{Mangle(create.Type.Enum!.ResultTypes!.Value.Ok)}({Operand(create.Arguments[0])})",
        NirBuiltin { Function: BuiltinFunction.VectorPush } push =>
            $"nvVP_{Mangle(push.Arguments[0].Type.Pointee!)}({Operand(push.Arguments[0])}, {Operand(push.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.StringAppend } append =>
            $"nvVA_{Mangle(append.Arguments[0].Type.Pointee!)}({Operand(append.Arguments[0])}, {Operand(append.Arguments[1])})",
        NirBuiltin { Function: BuiltinFunction.VectorPop } pop => $"nvVO_{Mangle(pop.Arguments[0].Type.Pointee!)}({Operand(pop.Arguments[0])})",
        NirBuiltin { Function: BuiltinFunction.VectorClear } clear => $"nvVC_{Mangle(clear.Arguments[0].Type.Pointee!)}({Operand(clear.Arguments[0])})",
        NirBuiltin { Function: BuiltinFunction.VectorCapacity } capacity => $"{Operand(capacity.Arguments[0])}->cap",
        NirBuiltin { Function: BuiltinFunction.BoxCreate } create => $"{BoxCreateName(create.Type)}({Operand(create.Arguments[^1])})",
        NirArray array => $"(({CTypeName(array.Type)}){{ .e = {{ {string.Join(", ", array.Elements.Select(Operand))} }} }})",
        NirDiscriminant discriminant => $"(uint32_t){Place(discriminant.Place)}.tag",
        NirEnumAggregate value => EnumAggregate(value),
        _ => throw new InvalidOperationException($"cannot emit rvalue {rvalue}"),
    };

    private string Binary(NirBinary binary)
    {
        var left = Operand(binary.Left);
        var right = Operand(binary.Right);
        var operandType = binary.Left.Type;
        var loc = Location(binary.Source, binary.Span);
        if (operandType.Kind == TypeKind.Handle)
        {
            return $"{(binary.Op == BinaryOp.NotEqual ? "!" : "")}nova_handle_eq({left}, {right})";
        }
        if (operandType.IsStringView)
        {
            return $"{(binary.Op == BinaryOp.NotEqual ? "!" : "")}nova_str_eq({left}, {right})";
        }
        switch (binary.Op)
        {
            case BinaryOp.Add: return $"nv_add_{Suffix(operandType)}({left}, {right}, {loc})";
            case BinaryOp.Subtract: return $"nv_sub_{Suffix(operandType)}({left}, {right}, {loc})";
            case BinaryOp.Multiply: return $"nv_mul_{Suffix(operandType)}({left}, {right}, {loc})";
            case BinaryOp.Divide: return $"nv_div_{Suffix(operandType)}({left}, {right}, {loc})";
            case BinaryOp.Remainder: return $"nv_rem_{Suffix(operandType)}({left}, {right}, {loc})";
            case BinaryOp.BitAnd: return $"({CType(operandType)})({left} & {right})";
            case BinaryOp.BitOr: return $"({CType(operandType)})({left} | {right})";
            case BinaryOp.BitXor: return $"({CType(operandType)})({left} ^ {right})";
            case BinaryOp.ShiftLeft:
            case BinaryOp.ShiftRight:
            {
                var amountType = binary.Right.Type;
                var amount = amountType.IsSigned
                    ? $"nv_shift_amount_s((int64_t){right}, {operandType.Bits}u, {loc})"
                    : $"nv_shift_amount_u((uint64_t){right}, {operandType.Bits}u, {loc})";
                var helper = binary.Op == BinaryOp.ShiftLeft ? "shl" : "shr";
                return $"nv_{helper}_{Suffix(operandType)}({left}, {amount})";
            }
            // Comparing with the type's own limit (`u < 0`, `x <= MAX`): GCC warns (-Wtype-limits); a constant (§5).
            case BinaryOp.Less or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual
                when AgainstLimit(binary.Op, binary.Left, binary.Right) is { } constant:
                return constant ? "true" : "false";
            // Comparing a place with itself: C warns (-Wtautological-compare); the result is a constant (§5).
            case BinaryOp.Equal or BinaryOp.LessEqual or BinaryOp.GreaterEqual when left == right: return "true";
            case BinaryOp.NotEqual or BinaryOp.Less or BinaryOp.Greater when left == right: return "false";
            case BinaryOp.Equal: return $"({left} == {right})";
            case BinaryOp.NotEqual: return $"({left} != {right})";
            case BinaryOp.Less: return $"({left} < {right})";
            case BinaryOp.LessEqual: return $"({left} <= {right})";
            case BinaryOp.Greater: return $"({left} > {right})";
            case BinaryOp.GreaterEqual: return $"({left} >= {right})";
            default: throw new InvalidOperationException($"operator {binary.Op} must have been lowered");
        }
    }

    /// <summary>
    /// The value of an ordering comparison that one operand, a constant at its type's minimum or maximum,
    /// decides by itself (<c>x &lt; MIN</c> is false, <c>x &lt;= MAX</c> true, ...); null otherwise.
    /// </summary>
    private static bool? AgainstLimit(BinaryOp op, NirOperand left, NirOperand right)
    {
        if (left is NirConst l && right is NirConst r)
        {
            return IntegerSemantics.Compare(op, (System.Numerics.BigInteger)l.Value, (System.Numerics.BigInteger)r.Value); // two constants
        }
        // Write it as `x op c`: a constant on the left mirrors the operator.
        var (op2, value, constant) = right is NirConst rc && left is not NirConst
            ? (op, left, rc)
            : left is NirConst lc && right is not NirConst
                ? (op switch { BinaryOp.Less => BinaryOp.Greater, BinaryOp.Greater => BinaryOp.Less, BinaryOp.LessEqual => BinaryOp.GreaterEqual, _ => BinaryOp.LessEqual }, right, lc)
                : (op, left, (NirConst?)null);
        if (constant is null || value.Type.Kind != TypeKind.Integer)
        {
            return null;
        }
        var c = (System.Numerics.BigInteger)constant.Value;
        return op2 switch
        {
            BinaryOp.Less when c == value.Type.Min => false,
            BinaryOp.GreaterEqual when c == value.Type.Min => true,
            BinaryOp.Greater when c == value.Type.Max => false,
            BinaryOp.LessEqual when c == value.Type.Max => true,
            _ => null,
        };
    }

    /// <summary>
    /// truncate&lt;T&gt;: convert to the unsigned type of T's width (well-defined modulo 2^N in C), then
    /// reinterpret as signed with a helper if T is signed (type-system.md §7).
    /// </summary>
    private string Truncate(NirTruncate truncate)
    {
        var target = truncate.Type;
        var unsignedName = $"uint{target.Bits}_t";
        var asUnsigned = $"({unsignedName}){Operand(truncate.Operand)}";
        return target.IsSigned ? $"nv_as_i{target.Bits}({asUnsigned})" : asUnsigned;
    }

    private string Operand(NirOperand? operand) => operand switch
    {
        NirPlaceOperand place when IsBoundaryField(place.Place) => FromBoundary(place.Place.Type, Place(place.Place)),
        NirPlaceOperand place => Place(place.Place),
        NirConst c => Constant(c.Value, c.Type),
        // String literals are static data, so a view of one never dangles (§22.2).
        NirString s => $"((nv_str){{ .ptr = (const uint8_t *){CStringLiteral(s.Value)}, .len = {Encoding.UTF8.GetByteCount(s.Value)}u }})",
        _ => throw new InvalidOperationException($"cannot emit operand {operand}"),
    };

    internal static string Constant(Int128 value, NovaType type)
    {
        if (type.Kind == TypeKind.Bool)
        {
            return value != 0 ? "true" : "false";
        }
        var cType = CTypeName(type);
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (value >= int.MinValue + 1 && value <= int.MaxValue)
        {
            return $"(({cType}){text})";
        }
        if (!type.IsSigned)
        {
            return $"(({cType})UINT64_C({text}))";
        }
        // INT64_MIN cannot be written as a negated literal: its magnitude does not fit in int64_t.
        return value == long.MinValue ? $"(({cType})INT64_MIN)" : $"(({cType})INT64_C({text}))";
    }

    private string Location(SourceText source, TextSpan span)
    {
        var position = source.GetLinePosition(span.Start);
        return $"NV_LOC(nv_file_{_fileIds[source]}, {position.Line}u, {position.Column}u)";
    }

    /// <summary>
    /// A field of an imported C type whose C type differs from Nova's C for it (pointers, which may have <c>const</c>;
    /// <c>size_t</c>): converted when read and written, as at the C boundary (type-system.md §31.3).
    /// </summary>
    private bool IsBoundaryField(NirPlace place)
    {
        if (place.Projections.IsEmpty || place.Projections[^1].Kind != ProjectionKind.Field)
        {
            return false;
        }
        var owner = place.Local.Type;
        foreach (var projection in place.Projections.AsSpan(0, place.Projections.Length - 1))
        {
            owner = projection.Apply(owner);
        }
        var type = place.Type;
        return owner.Struct is { IsImported: true }
            && (type.IsRawPointer || ReferenceEquals(type, _types.USize) || ReferenceEquals(type, _types.ISize));
    }

    /// <summary>
    /// A place as a C lvalue. The deref of a slice <c>s</c> is not an lvalue by itself: it stays <c>s</c>, and
    /// its elements are <c>s.ptr[i]</c>; elements of an array place are <c>a.e[i]</c>.
    /// </summary>
    private string Place(NirPlace place)
    {
        var text = LocalName(place.Local);
        var type = place.Local.Type;
        NovaType? stable = null; // the stable vector whose elements `type` is (§24.2)
        foreach (var projection in place.Projections)
        {
            text = projection.Kind switch
            {
                ProjectionKind.Deref when type.IsStableVector => text,
                ProjectionKind.Index when stable is not null => $"(*nvSVE_{Mangle(stable)}(&{text}, {Operand((NirOperand)projection.Index!)}))",
                ProjectionKind.Deref when type.IsSlice || type.IsVector => text, // the elements' (ptr, len) are the value's
                ProjectionKind.Deref when type.IsPoolBox => $"(*{text}.ptr)",
                ProjectionKind.Deref => $"(*{text})",
                ProjectionKind.Field when projection.Field!.IsRegionPart => text, // the frame itself (§19.5)
                ProjectionKind.Field => $"{text}.{FieldName(projection.Field!)}",
                ProjectionKind.Downcast => $"{text}.u.{VariantName(projection.Variant!)}",
                // A constant index too large for any object (its check panics first) would trip -Warray-bounds (§5).
                _ when type.IsUnsized && projection.Index is NirConst { Value: var huge } && huge > long.MaxValue / TypeTable.LayoutOf(type.Element!).Size
                    => $"{text}.ptr[nv_opaque_index({Operand((NirOperand)projection.Index!)})]",
                _ when type.IsUnsized => $"{text}.ptr[{Operand((NirOperand)projection.Index!)}]",
                // A constant index past the end is checked first and always panics; hiding the constant keeps
                // -Warray-bounds quiet about the access that never happens (§5).
                _ when projection.Index is NirConst { Value: var past } && past >= type.ArrayLength => $"{text}.e[nv_opaque_index({Operand((NirOperand)projection.Index!)})]",
                _ => $"{text}.e[{Operand((NirOperand)projection.Index!)}]",
            };
            stable = projection.IsDeref && type.IsStableVector ? type : null;
            type = projection.Apply(type);
        }
        return text;
    }

    /// <summary>Elements [low, high) of an array or slice place; the range was checked before.</summary>
    private string Slice(NirSlice slice)
    {
        var start = slice.Base.Type.IsUnsized ? $"{Place(slice.Base)}.ptr" : $"{Place(slice.Base)}.e";
        var low = Operand(slice.Low);
        return $"(({CTypeName(slice.Type)}){{ .ptr = {start} + {low}, .len = {Operand(slice.High)} - {low} }})";
    }

    private string EnumAggregate(NirEnumAggregate value)
    {
        var name = CTypeName(value.Type);
        if (value.Payload.IsEmpty)
        {
            return $"(({name}){{ .tag = {value.Variant.Index} }})";
        }
        var inits = value.Variant.Payload.Select((f, i) => $".{FieldName(f)} = {Operand(value.Payload[i])}");
        return $"(({name}){{ .tag = {value.Variant.Index}, .u.{VariantName(value.Variant)} = {{ {string.Join(", ", inits)} }} }})";
    }

    private string Aggregate(NirAggregate aggregate)
    {
        var fields = aggregate.Type.Struct!.Fields;
        if (fields.Count == 0)
        {
            return $"(({CTypeName(aggregate.Type)}){{ .nv_empty = 0 }})";
        }
        // Fields of C state are left to C's zero-filling of unnamed members (type-system.md §33.2).
        var inits = fields.Select((f, i) => (Field: f, Value: aggregate.Fields[i])).Where(p => p.Value is not NirZero)
            .Select(p => $".{FieldName(p.Field)} = {Operand(p.Value)}").ToList();
        if (inits.Count == 0)
        {
            inits.Add($".{FieldName(fields[0])} = {{ 0 }}");
        }
        return $"(({CTypeName(aggregate.Type)}){{ {string.Join(", ", inits)} }})";
    }

    private string LocalName(NirLocal local) =>
        local.Name is { } name ? $"v_{_symbols.GetName(name)}_{local.Id}" : $"t_{local.Id}";

    private static string CType(NovaType type) => CTypeName(type);

    private static string CTypeName(NovaType type) => type.Kind switch
    {
        TypeKind.Bool => "bool",
        TypeKind.Void => "void",
        TypeKind.Integer => $"{(type.IsSigned ? "int" : "uint")}{type.Bits}_t",
        TypeKind.Struct when type.Struct!.IsImported => type.Struct.CSpelling!, // the header's type (type-system.md §31.3)
        TypeKind.Struct when IsInstance(type) => $"nvT_{Mangle(type)[1..]}",
        TypeKind.Struct => $"nvT_{type.Name}",
        TypeKind.Enum when type.Enum!.ResultTypes is not null => $"nvR_{Mangle(type)[1..]}",
        TypeKind.Enum when type.Enum!.OptionType is not null => $"nvO_{Mangle(type)[1..]}",
        TypeKind.Enum when IsInstance(type) => $"nvE_{Mangle(type)[1..]}",
        TypeKind.Enum => $"nvE_{type.Name}",
        TypeKind.Reference when type.IsStringView => "nv_str",
        TypeKind.Reference when type.IsSlice => $"nvS{(type.IsMutableReference ? "m" : "l")}_{Mangle(type.Pointee!.Element!)}",
        // A shared reference to a box is a pointer to a const pointer (`T *const *`), not to a const T.
        // Region methods mutate through a shared reference (allocation is interior mutability, §19.2).
        TypeKind.Reference when type.Pointee!.Kind is TypeKind.Region or TypeKind.StableVector => $"{CTypeName(type.Pointee)} *",
        TypeKind.Reference when type.Pointee!.IsBox => type.IsMutableReference ? $"{CTypeName(type.Pointee)}*" : $"{CTypeName(type.Pointee)}const *",
        TypeKind.Reference => type.IsMutableReference ? $"{CTypeName(type.Pointee!)} *" : $"const {CTypeName(type.Pointee!)} *",
        TypeKind.Array => $"nvA{type.ArrayLength}_{Mangle(type.Element!)}",
        TypeKind.Box when type.IsPoolBox => $"nvPB_{Mangle(type.Pointee!)}",
        TypeKind.Box => $"{CTypeName(type.Pointee!)} *",
        TypeKind.RawPointer => $"{CTypeName(type.Pointee!)} *", // no const (unsafe.md §4); `*void` is `void *`
        TypeKind.CChar => "char", // only behind a pointer, for C library prototypes (type-system.md §28.3)
        TypeKind.FunctionPointer => $"nvP_{Mangle(type)}", // a typedef of the C function pointer type (§29.5)
        TypeKind.Vector => $"nvV_{Mangle(type)[1..]}",
        TypeKind.StableVector => $"nvSV_{Mangle(type)[1..]}",
        TypeKind.SlotMap => $"nvK_{Mangle(type)[1..]}",
        TypeKind.Handle => "nv_handle",
        TypeKind.Resource => "int32_t", // a descriptor (§26)
        TypeKind.Region => type.Region switch
        {
            RegionKind.Arena => "nv_arena",
            RegionKind.FixedBuffer => "nv_fba",
            RegionKind.Pool => "nv_pool",
            _ => "nv_scratch",
        },
        _ => throw new InvalidOperationException($"type '{type}' has no C representation"),
    };

    /// <summary>
    /// A type inside the name of an array, slice or result type. Struct and enum names carry their
    /// length and every other code is prefix-free, so concatenations cannot collide:
    /// <c>[[i32; 3]; 2]</c> is <c>nvA2_A3_i32</c>, <c>Slice&lt;Point&gt;</c> is <c>nvSl_S5Point</c>,
    /// <c>Result&lt;void, E&gt;</c> is <c>nvR_vE1E</c>.
    /// </summary>
    private static string Mangle(NovaType type) => type.Kind switch
    {
        TypeKind.Struct when IsInstance(type) => $"S{BaseName(type).Length}{BaseName(type)}{MangledArguments(type.Struct!.TypeArguments)}",
        TypeKind.Struct => $"S{type.Name.Length}{type.Name}",
        TypeKind.Enum when type.Enum!.ResultTypes is var (ok, err) => $"R{Mangle(ok)}{Mangle(err)}",
        TypeKind.Enum when type.Enum!.OptionType is { } value => $"O{Mangle(value)}",
        TypeKind.Enum when IsInstance(type) => $"E{BaseName(type).Length}{BaseName(type)}{MangledArguments(type.Enum!.TypeArguments)}",
        TypeKind.Enum => $"E{type.Name.Length}{type.Name}",
        TypeKind.Array when type.IsStr => "Z",
        TypeKind.Array when type.IsUnsized => $"U{Mangle(type.Element!)}",
        TypeKind.Array => $"A{type.ArrayLength}_{Mangle(type.Element!)}",
        TypeKind.Box when type.IsPoolBox => $"Y{Mangle(type.Pointee!)}",
        TypeKind.Box => $"X{Mangle(type.Pointee!)}{Mangle(type.Allocator!)}",
        TypeKind.Vector => $"V{Mangle(type.Pointee!)}{Mangle(type.Allocator!)}",
        TypeKind.StableVector => $"W{Mangle(type.Pointee!.Element!)}{Mangle(type.Allocator!)}",
        TypeKind.SlotMap => $"K{Mangle(type.Element!)}{Mangle(type.Allocator!)}",
        TypeKind.Handle => $"H{Mangle(type.Element!)}",
        TypeKind.Resource => type.Resource switch { ResourceKind.File => "IF", ResourceKind.TcpListener => "IL", _ => "IS" },
        TypeKind.Reference => $"{(type.IsMutableReference ? "Q" : "P")}{Mangle(type.Pointee!)}",
        TypeKind.RawPointer => $"{(type.IsMutablePointer ? "M" : "N")}{Mangle(type.Pointee!)}",
        TypeKind.FunctionPointer => $"F{type.ParameterTypes.Length}{string.Concat(type.ParameterTypes.Select(Mangle))}{Mangle(type.ResultType!)}",
        TypeKind.Region when type.Region == RegionKind.Pool => $"G4Pool{Mangle(type.Element!)}",
        TypeKind.Region => $"G{type.Name.Length}{type.Name}",
        TypeKind.Void => "v",
        _ => type.Name,
    };

    /// <summary>Helper suffix: isize/usize use the helpers of their fixed width.</summary>
    private static string Suffix(NovaType type) => $"{(type.IsSigned ? "i" : "u")}{type.Bits}";

    private void EmitMainWrapper()
    {
        var main = _program.Functions.FirstOrDefault(f => f.Source.Owner is null && _symbols.GetName(f.Name) == "main");
        if (main is null)
        {
            return;
        }
        // C's signature for `main` is the one place plain `char` appears; argv goes to the runtime as bytes.
        Line("int main(int argc, char **argv) {");
        Line("    nova_rt_args((int32_t)argc, (const uint8_t *const *)(void *)argv);");
        Line("    nova_rt_start();");
        Line(main.ReturnType.Kind == TypeKind.Void
            ? "    nvF_main();\n    return (int)nova_rt_finish(0);"
            : "    return (int)nova_rt_finish(nvF_main());");
        Line("}");
    }

    /// <summary>A C string literal of the UTF-8 bytes; anything outside printable ASCII uses octal escapes.</summary>
    internal static string CStringLiteral(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b is >= 0x20 and < 0x7F && b is not ((byte)'"' or (byte)'\\' or (byte)'?'))
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
        }
        return sb.Append('"').ToString();
    }
}
