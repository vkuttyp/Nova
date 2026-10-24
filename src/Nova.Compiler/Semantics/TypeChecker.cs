using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Semantics;

/// <summary>
/// Name resolution and type checking of the M1 subset (docs/type-system.md), producing HIR.
/// Phases: declare all items (signatures), evaluate constants (lazily, with cycle detection),
/// check function bodies, then run flow analysis per function.
/// </summary>
internal sealed class TypeChecker
{
    private enum ConstState
    {
        Unvisited,
        InProgress,
        Done,
    }

    private sealed class ConstEntry(ConstDecl decl, SourceText source, HirConst hir)
    {
        public ConstDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public HirConst Hir { get; } = hir;
        public ConstState State { get; set; }
    }

    /// <summary>
    /// A struct, or one instantiation of a generic struct (§20). For a generic declaration the entry in
    /// <c>_items</c> is its own generic instance (type parameters as arguments), whose
    /// <see cref="Instances"/> cache the others.
    /// </summary>
    private sealed class StructEntry(StructDecl decl, SourceText source, NovaType type)
    {
        public StructDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public NovaType Type { get; } = type;
        public IReadOnlyDictionary<Symbol, NovaType> Environment { get; init; } = new Dictionary<Symbol, NovaType>();
        public StructEntry? Template { get; init; }
        public Dictionary<string, StructEntry> Instances { get; } = [];
    }

    /// <summary>An enum, or one instantiation of a generic enum (as <see cref="StructEntry"/>).</summary>
    private sealed class EnumEntry(EnumDecl decl, SourceText source, NovaType type)
    {
        public EnumDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public NovaType Type { get; } = type;
        public IReadOnlyDictionary<Symbol, NovaType> Environment { get; init; } = new Dictionary<Symbol, NovaType>();
        public EnumEntry? Template { get; init; }
        public Dictionary<string, EnumEntry> Instances { get; } = [];
    }

    /// <summary>An interface (§21): its declaration and type parameters.</summary>
    private sealed class InterfaceEntry(InterfaceDecl decl, SourceText source)
    {
        public InterfaceDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public ImmutableArray<NovaType> Parameters { get; set; } = [];
    }

    /// <summary>A generic function (§20): its generic version, and its instantiations by type arguments.</summary>
    private sealed class GenericFunctionEntry(FunctionDecl decl, SourceText source)
    {
        public FunctionDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public HirFunction Generic { get; set; } = null!;
        public ImmutableArray<NovaType> Parameters { get; set; } = [];
        public Dictionary<string, HirFunction> Instances { get; } = [];
    }

    private enum Builtin
    {
        Console,
        Process,
        Truncate,
        Box,
        Arena,
        FixedBufferAllocator,
        Scratch,
        Pool,
        Vector,
        String,
        SlotMap,
        StableVector,
        File,
        TcpListener,
        TcpStream,
        Ptr,
        Slice,
        SliceMut,
        Cpu,
        Target,
    }

    private readonly IReadOnlyList<CompilationUnit> _units;
    private readonly Profile _profile;
    private readonly HashSet<(SourceText, TextSpan)> _profileReports = [];
    private readonly SymbolTable _symbols;
    private readonly TypeTable _types;
    private DiagnosticBag _diagnostics;
    /// <summary>Type parameters in scope while resolving types (§20): the current declaration's substitution.</summary>
    private IReadOnlyDictionary<Symbol, NovaType> _typeEnvironment = new Dictionary<Symbol, NovaType>();
    /// <summary>The region names in scope (§25.1): the current item's region parameters (and its struct's, for a method).</summary>
    private HashSet<Symbol> _regions = [];
    /// <summary>Set once every item is declared and every member resolved; later instantiations resolve at once.</summary>
    private bool _membersResolved;
    private readonly List<(ImplDecl Decl, SourceText Source)> _impls = [];
    /// <summary>Methods from `impl` blocks, by type (§21.1).</summary>
    private readonly Dictionary<NovaType, Dictionary<Symbol, HirFunction>> _implMethods = [];
    /// <summary>Which type implements which interface instantiation: keys "typeId|interface|args" (§21.1).</summary>
    private readonly HashSet<string> _implementations = [];
    /// <summary>Bound checks made before every `impl` is known; run once they are (§21.2).</summary>
    private readonly List<Action> _pendingBoundChecks = [];
    private bool _implsReady;
    /// <summary>Signatures of bound methods called on a type parameter (§21.2), by type parameter, bound and name.</summary>
    private readonly Dictionary<string, HirFunction> _boundMethods = [];

    private readonly Dictionary<Symbol, object> _items = [];
    private readonly Dictionary<Symbol, Builtin> _builtins;
    private readonly List<HirFunction> _functions = [];
    private readonly List<ConstEntry> _constants = [];
    private readonly List<StructEntry> _structs = [];
    private readonly List<EnumEntry> _enums = [];
    private static readonly bool[] s_boolValues = [false, true];
    private readonly Symbol _thisSymbol;

    // Context of the body or constant currently being checked.
    private SourceText _source = null!;
    private HirFunction? _function;
    private List<Dictionary<Symbol, HirLocal>> _scopes = [];
    private int _loopDepth;
    /// <summary>Array and slice element types named before Copy-ness was known; checked after <see cref="ComputeCopyAndDrop"/>.</summary>
    private readonly List<(NovaType Element, long Length, TextSpan Span, SourceText Source)> _pendingElements = [];
    private bool _copyKnown;
    /// <summary>Nesting depth of deferred statements being checked: `?` may not appear in them (N0254).</summary>
    private int _deferDepth;
    /// <summary>Nesting of `unsafe` blocks and expressions (type-system.md §27.4).</summary>
    private int _unsafeDepth;
    /// <summary>Did the current function's body use `unsafe`? (The Safety rule, §27.4.)</summary>
    private bool _usedUnsafe;
    private EnumEntry _allocError = null!;
    private EnumEntry _ioError = null!;
    private StructEntry _systemHeap = null!;
    private StructEntry _failingAllocator = null!;
    /// <summary>Built-in items (AllocError, the allocators): emitted only if the program uses them.</summary>
    private readonly HashSet<object> _builtinItems = [];
    private readonly HashSet<object> _usedBuiltins = [];
    private static readonly string[] s_reservedNames = ["Result", "Ok", "Err", "Box", "Scratch", "Pool", "PoolBox", "Vector", "String", "SlotMap", "StableVector", "Handle", "File", "TcpListener", "TcpStream", "Ptr", "Slice", "SliceMut", "Cpu", "Target"];

    public TypeChecker(IReadOnlyList<CompilationUnit> units, SymbolTable symbols, TypeTable types, DiagnosticBag diagnostics,
        Profile profile = Profile.Hosted)
    {
        _profile = profile;
        _units = units;
        _symbols = symbols;
        _types = types;
        _diagnostics = diagnostics;
        _builtins = new Dictionary<Symbol, Builtin>
        {
            [symbols.Intern("Console")] = Builtin.Console,
            [symbols.Intern("Process")] = Builtin.Process,
            [symbols.Intern("truncate")] = Builtin.Truncate,
            [symbols.Intern("Box")] = Builtin.Box,
            [symbols.Intern("Arena")] = Builtin.Arena,
            [symbols.Intern("FixedBufferAllocator")] = Builtin.FixedBufferAllocator,
            [symbols.Intern("Scratch")] = Builtin.Scratch,
            [symbols.Intern("Pool")] = Builtin.Pool,
            [symbols.Intern("Vector")] = Builtin.Vector,
            [symbols.Intern("String")] = Builtin.String,
            [symbols.Intern("SlotMap")] = Builtin.SlotMap,
            [symbols.Intern("StableVector")] = Builtin.StableVector,
            [symbols.Intern("File")] = Builtin.File,
            [symbols.Intern("TcpListener")] = Builtin.TcpListener,
            [symbols.Intern("TcpStream")] = Builtin.TcpStream,
            [symbols.Intern("Ptr")] = Builtin.Ptr,
            [symbols.Intern("Slice")] = Builtin.Slice,
            [symbols.Intern("SliceMut")] = Builtin.SliceMut,
            [symbols.Intern("Cpu")] = Builtin.Cpu,
            [symbols.Intern("Target")] = Builtin.Target,
        };
        _thisSymbol = symbols.Intern("this");
        _frameMemory = new StructField(symbols.Intern("memory"), types.ScratchFrame, 0, default) { IsRegionPart = true };
        _frameAllocation = new StructField(symbols.Intern("allocation"), types.ScratchFrame, 1, default) { IsRegionPart = true };
    }

    /// <summary>The two borrow-checker parts of a scratch frame (type-system.md §19.5).</summary>
    private readonly StructField _frameMemory;
    private readonly StructField _frameAllocation;

    public HirProgram Check()
    {
        DeclareAllocError();
        // Interfaces first: bounds on every later declaration refer to them (§21).
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items.OfType<InterfaceDecl>())
            {
                DeclareInterface(item);
            }
        }
        // Struct names next, so every signature and field can refer to any struct.
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items)
            {
                switch (item)
                {
                    case StructDecl s:
                        DeclareStruct(s);
                        break;
                    case EnumDecl e:
                        DeclareEnum(e);
                        break;
                }
            }
        }
        ProcessTypeAttributes();
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items.Where(i => i is not (StructDecl or EnumDecl or InterfaceDecl)))
            {
                Declare(item);
            }
        }
        ProcessFunctionAttributes();
        ProcessImpls();
        // Instantiations made while resolving members are resolved too (§20.4).
        for (int s = 0, e = 0; s < _structs.Count || e < _enums.Count;)
        {
            for (; s < _structs.Count; s++)
            {
                ResolveStructMembers(_structs[s]);
            }
            for (; e < _enums.Count; e++)
            {
                ResolveEnumVariants(_enums[e]);
            }
        }
        _membersResolved = true;
        CheckStructRecursion();
        ComputeCopyAndDrop();
        _copyKnown = true;
        CheckForeignInterfaces();
        foreach (var (element, length, span, source) in _pendingElements)
        {
            _source = source;
            CheckElementCopy(element, length, span);
        }

        foreach (var constant in _constants)
        {
            EvaluateConstant(constant);
        }

        // Instantiations made while checking bodies are checked too.
        for (var i = 0; i < _functions.Count; i++)
        {
            var function = _functions[i];
            if (function.IsExtern)
            {
                continue; // defined in C (§28.2)
            }
            if (function.IsInstantiation)
            {
                // Its generic version reports (§20.3); a scoped type argument the body cannot take is N0274.
                var (site, span) = function.InstantiationSite ?? (function.Source, function.Syntax.Name.Span);
                CheckInstance(() => { CheckFunctionBody(function); return 0; }, function.TypeArguments,
                    Name(function.Syntax.Name.Name), site, span);
            }
            else
            {
                CheckFunctionBody(function);
            }
        }

        CheckMain();
        // Built-in items are emitted only if the program uses them.
        bool Emitted(object entry) => !_builtinItems.Contains(entry) || _usedBuiltins.Contains(entry);
        // Generic types (with type parameters unsubstituted) are checked but never emitted (§20.3).
        return new HirProgram([.. _functions], [.. _constants.Select(c => c.Hir)],
            [.. _structs.Where(Emitted).Select(s => s.Type).Where(t => !t.IsGeneric),
             .. _enums.Where(Emitted).Select(e => e.Type).Where(t => !t.IsGeneric)]);
    }

    private string Name(Symbol symbol) => _symbols.GetName(symbol);

    private void Error(string code, TextSpan span, string message) => _diagnostics.Error(code, _source, span, message);

    private HirError ErrorResult(TextSpan span) => new(_types.Error, span);

    // ---- Declarations ----

    /// <summary>The built-in <c>enum AllocError { OutOfMemory }</c> (type-system.md §17.1), declared like a user enum.</summary>
    private void DeclareAllocError()
    {
        var source = new SourceText("<built-in>", "enum AllocError { OutOfMemory }");
        var name = new Identifier(_symbols.Intern("AllocError"), new TextSpan(5, 10));
        var variant = new Identifier(_symbols.Intern("OutOfMemory"), new TextSpan(18, 11));
        var decl = new EnumDecl(Visibility.Public, name, [new VariantDecl(variant, [], variant.Span)], new TextSpan(0, source.Text.Length));
        _allocError = new EnumEntry(decl, source, TypeTable.CreateEnum("AllocError", decl, source));
        _items.Add(name.Name, _allocError);
        _enums.Add(_allocError);
        _builtinItems.Add(_allocError);
        _ioError = DeclareIoError();
        _systemHeap = DeclareAllocatorType("SystemHeap");
        _failingAllocator = DeclareAllocatorType("FailingAllocator");
    }

    /// <summary>
    /// <c>IoError</c> (type-system.md §26.2), parsed from built-in source. The variant order is the runtime's
    /// (<c>NV_IO_*</c> in nova/rt.h).
    /// </summary>
    private EnumEntry DeclareIoError()
    {
        var source = new SourceText("<built-in>", "enum IoError { NotFound, PermissionDenied, AlreadyExists, IsDirectory, NotDirectory, "
            + "InvalidInput, BrokenPipe, ConnectionRefused, AddressInUse, OutOfMemory, Other(i32) }");
        var decl = (EnumDecl)Parser.ParseText(source, _symbols, new DiagnosticBag()).Items[0];
        var entry = new EnumEntry(decl, source, TypeTable.CreateEnum("IoError", decl, source));
        _items.Add(decl.Name.Name, entry);
        _enums.Add(entry);
        _builtinItems.Add(entry);
        return entry;
    }

    /// <summary>A built-in allocator type (type-system.md §18.1): a field-less struct.</summary>
    private StructEntry DeclareAllocatorType(string text)
    {
        var source = new SourceText("<built-in>", $"struct {text} {{ }}");
        var name = new Identifier(_symbols.Intern(text), new TextSpan(7, text.Length));
        var decl = new StructDecl(Visibility.Public, name, [], [], new TextSpan(0, source.Text.Length));
        var entry = new StructEntry(decl, source, TypeTable.CreateStruct(text, decl, source));
        _items.Add(name.Name, entry);
        _structs.Add(entry);
        _builtinItems.Add(entry);
        return entry;
    }

    private bool IsAllocatorType(NovaType type) => type == _systemHeap.Type || type == _failingAllocator.Type;

    /// <summary>Reports N0201 if <paramref name="name"/> is a built-in name that items cannot take (type-system.md §17.1).</summary>
    private bool IsReservedName(Identifier name)
    {
        var text = Name(name.Name);
        if (!s_reservedNames.Contains(text) && !(_items.GetValueOrDefault(name.Name) is { } item && _builtinItems.Contains(item)))
        {
            return false;
        }
        Error(DiagnosticCodes.DuplicateItem, name.Span, $"'{text}' is a built-in name");
        return true;
    }

    private void Declare(Item item)
    {
        if (item is ImplDecl impl)
        {
            _impls.Add((impl, _source)); // processed once every item is declared (§21.1)
            return;
        }
        if (item is ExternBlock block)
        {
            DeclareExternBlock(block);
            return;
        }
        var name = item.Name.Name;
        if (IsReservedName(item.Name))
        {
            return;
        }
        if (_items.ContainsKey(name))
        {
            Error(DiagnosticCodes.DuplicateItem, item.Name.Span, $"'{Name(name)}' is already defined");
            return;
        }

        switch (item)
        {
            case FunctionDecl fn:
            {
                if (fn.Receiver is not null)
                {
                    Error(DiagnosticCodes.ThisOutsideMethod, fn.Receiver.Span,
                        "'this' parameters are only allowed in functions declared inside a struct");
                }
                if (!fn.TypeParameters.IsEmpty)
                {
                    // Checked once with opaque type parameters; instantiated on use (§20).
                    var generic = new GenericFunctionEntry(fn, _source);
                    var (parameters, environment) = DeclareTypeParameters(fn.TypeParameters, new Dictionary<Symbol, NovaType>());
                    generic.Parameters = parameters;
                    generic.Generic = DeclareFunction(fn, owner: null, environment);
                    _items.Add(name, generic);
                    break;
                }
                _items.Add(name, DeclareFunction(fn, owner: null));
                break;
            }
            case ConstDecl c:
            {
                var type = ResolveType(c.Type, allowVoid: false);
                if (type.Kind is TypeKind.Struct or TypeKind.Array)
                {
                    Error(DiagnosticCodes.Unsupported, c.Type.Span, $"constants of {(type.Kind == TypeKind.Array ? "array" : "struct")} type are not supported yet");
                    type = _types.Error;
                }
                else if (TypeTable.IsScoped(type))
                {
                    Error(DiagnosticCodes.ReferenceNotAllowed, c.Type.Span, "constants cannot be references");
                    type = _types.Error;
                }
                var entry = new ConstEntry(c, _source, new HirConst(name, type, _source, c.Span));
                _items.Add(name, entry);
                _constants.Add(entry);
                break;
            }
        }
    }

    /// <summary>Creates the HIR function for a top-level function or a struct member, resolving its signature.</summary>
    private HirFunction DeclareFunction(FunctionDecl fn, NovaType? owner, IReadOnlyDictionary<Symbol, NovaType>? environment = null,
        bool register = true)
    {
        var (savedEnvironment, savedRegions) = (_typeEnvironment, _regions);
        _typeEnvironment = environment ?? new Dictionary<Symbol, NovaType>();
        _regions = RegionScope(fn, owner);
        try
        {
            return DeclareFunctionCore(fn, owner, register);
        }
        finally
        {
            (_typeEnvironment, _regions) = (savedEnvironment, savedRegions);
        }
    }

    /// <summary>The regions a function may name: its own and, for a method, its struct's (§25.1).</summary>
    private static HashSet<Symbol> RegionScope(FunctionDecl fn, NovaType? owner) =>
        [.. fn.RegionParameters.Select(r => r.Name.Name), .. (owner?.Struct?.Syntax.RegionParameters ?? []).Select(r => r.Name.Name)];

    /// <summary>Region parameters are declared once each, and not twice with the same name (§25.1).</summary>
    private void CheckRegionParameters(ImmutableArray<TypeParameter> regions, ImmutableArray<TypeParameter> types)
    {
        var names = new HashSet<Symbol>();
        foreach (var region in regions)
        {
            if (!names.Add(region.Name.Name) || types.Any(t => t.Name.Name == region.Name.Name) || Name(region.Name.Name) == "static")
            {
                Error(DiagnosticCodes.DuplicateParameter, region.Name.Span, $"the region name '{Name(region.Name.Name)}' is already used");
            }
        }
    }

    /// <summary>Is <paramref name="syntax"/> a region name in scope (or <c>static</c>)? Reports N0273 if it is a plain name that is not.</summary>
    private bool CheckRegionArgument(TypeSyntax syntax)
    {
        if (syntax is NamedType { Name.Parts.Length: 1 } named
            && (Name(named.Name.Parts[0].Name) == "static" || _regions.Contains(named.Name.Parts[0].Name)))
        {
            return true;
        }
        Error(DiagnosticCodes.UnknownRegion, syntax.Span, syntax is NamedType { Name.Parts.Length: 1 } unknown
            ? $"'{Name(unknown.Name.Parts[0].Name)}' is not a region in scope; declare it with 'region {Name(unknown.Name.Parts[0].Name)}' or use 'static'"
            : "a region argument must be a region name");
        return false;
    }

    private bool IsRegionName(TypeSyntax syntax) =>
        syntax is NamedType { Name.Parts.Length: 1 } named
        && (Name(named.Name.Parts[0].Name) == "static" || _regions.Contains(named.Name.Parts[0].Name));

    /// <param name="register">False for a signature only (an interface method): checked, never compiled.</param>
    private HirFunction DeclareFunctionCore(FunctionDecl fn, NovaType? owner, bool register)
    {
        if (owner is not null && !fn.RegionParameters.IsEmpty)
        {
            Error(DiagnosticCodes.Unsupported, fn.RegionParameters[0].Span,
                "methods cannot declare region parameters of their own; use the struct's regions (type-system.md §25.1)");
        }
        CheckRegionParameters(fn.RegionParameters, fn.TypeParameters);
        if (owner is not null && !fn.TypeParameters.IsEmpty)
        {
            Error(DiagnosticCodes.Unsupported, fn.TypeParameters[0].Span,
                "methods cannot declare type parameters of their own in v0; use the struct's, or a top-level generic function");
            _typeEnvironment = DeclareTypeParameters(fn.TypeParameters, _typeEnvironment).Environment; // no cascade of unknown types
        }
        var returnType = fn.ReturnType is null ? _types.Void : ResolveType(fn.ReturnType, allowVoid: true, allowSources: true);
        var hir = new HirFunction(register ? _functions.Count : -1, fn.Name.Name, returnType, _source, fn, owner) { Environment = _typeEnvironment };
        if (owner is not null && fn.Receiver is not null)
        {
            // `this` is an immutable binding: the struct by value, or a reference to it (type-system.md §12, §13.2).
            var thisType = fn.Receiver.Kind switch
            {
                ReceiverKind.Borrow => _types.ReferenceTo(owner, isMutable: false),
                ReceiverKind.BorrowMut => _types.ReferenceTo(owner, isMutable: true),
                _ => owner,
            };
            hir.AddLocal(_thisSymbol, thisType, isMutable: false, isParameter: true, fn.Receiver.Span);
        }
        var seen = new HashSet<Symbol>();
        foreach (var parameter in fn.Parameters)
        {
            var type = ResolveType(parameter.Type, allowVoid: false);
            if (!seen.Add(parameter.Name.Name))
            {
                Error(DiagnosticCodes.DuplicateParameter, parameter.Name.Span,
                    $"parameter '{Name(parameter.Name.Name)}' is declared more than once");
            }
            hir.AddLocal(parameter.Name.Name, type, isMutable: false, isParameter: true, parameter.Name.Span);
        }
        hir.Parameters = [.. hir.Locals];
        ResolveFlows(fn, hir, owner);
        if (register)
        {
            _functions.Add(hir);
        }
        return hir;
    }

    /// <summary>True if every scoped value in <paramref name="type"/> is a <c>StringView</c> (§22.2).</summary>
    private static bool OnlyViews(NovaType type) => type switch
    {
        { IsStringView: true } => true,
        { IsReference: true } => false,
        { Enum.ResultTypes: var (ok, err) } => (!TypeTable.IsScoped(ok) || OnlyViews(ok)) && (!TypeTable.IsScoped(err) || OnlyViews(err)),
        { Enum.OptionType: { } value } => OnlyViews(value),
        _ => false,
    };

    /// <summary>
    /// The flows of a signature (type-system.md §25.6): which parameters' loans the result may carry (its return
    /// sources) and which parameters' loans the callee may store through each <c>borrow mut</c> parameter.
    /// </summary>
    private void ResolveFlows(FunctionDecl fn, HirFunction hir, NovaType? owner)
    {
        var typeParameters = fn.TypeParameters.Select(p => p.Name.Name)
            .Concat(owner?.Struct?.Syntax.TypeParameters.Select(p => p.Name.Name) ?? []).ToHashSet();
        var ownerRegions = owner?.Struct?.Syntax.RegionParameters.Select(r => "r:" + Name(r.Name.Name)) ?? [];
        var receiverOffset = hir.Parameters.Length - fn.Parameters.Length;
        var names = new List<HashSet<string>>();
        for (var i = 0; i < hir.Parameters.Length; i++)
        {
            var set = new HashSet<string>();
            if (i < receiverOffset)
            {
                set.UnionWith(ownerRegions);
                set.UnionWith(typeParameters.Where(t => owner!.Struct!.Syntax.TypeParameters.Any(p => p.Name.Name == t)).Select(t => "t:" + Name(t)));
            }
            else
            {
                CollectFlowNames(fn.Parameters[i - receiverOffset].Type, typeParameters, set, parameterNames: null);
            }
            if (TypeTable.IsScoped(hir.Parameters[i].Type) && !set.Any(n => n.StartsWith("r:", StringComparison.Ordinal)))
            {
                set.Add($"#{i}"); // elided: a region of its own
            }
            names.Add(set);
        }

        if (MayHoldLoans(hir.ReturnType))
        {
            var byName = hir.Parameters.Select((p, i) => (p.Name, i)).ToDictionary(x => x.Name, x => names[x.i]);
            var resultNames = new HashSet<string>();
            CollectFlowNames(fn.ReturnType!, typeParameters, resultNames, byName);
            var sources = Enumerable.Range(0, hir.Parameters.Length).Where(i => names[i].Overlaps(resultNames)).ToHashSet();
            var named = resultNames.Any(n => n.StartsWith("r:", StringComparison.Ordinal) || n.StartsWith('#'))
                || (fn.ReturnType is ReferenceType { HasSources: true });
            if (fn.ReturnType is ReferenceType { HasSources: true } || (TypeTable.IsScoped(hir.ReturnType) && !named))
            {
                sources.UnionWith(ResolveReturnSources(fn, hir)); // v0: `borrow(a, b)`, or elision (§13.5)
            }
            hir.ReturnSources = [.. sources.Order()];
        }

        var flows = ImmutableArray.CreateBuilder<(int, int)>();
        for (var i = 0; i < hir.Parameters.Length; i++)
        {
            if (hir.Parameters[i].Type is not { IsMutableReference: true } reference || !MayHoldLoans(reference.Pointee!))
            {
                continue;
            }
            var target = new HashSet<string>();
            if (i < receiverOffset)
            {
                target.UnionWith(names[i]);
            }
            else if (fn.Parameters[i - receiverOffset].Type is ReferenceType { Pointee: var pointee })
            {
                CollectFlowNames(pointee, typeParameters, target, parameterNames: null);
            }
            target.RemoveWhere(n => n.StartsWith('#'));
            for (var j = 0; j < hir.Parameters.Length; j++)
            {
                if (j != i && names[j].Overlaps(target))
                {
                    flows.Add((i, j));
                }
            }
        }
        hir.MutableFlows = flows.ToImmutable();
    }

    /// <summary>Can a value of this type carry loans? Scoped types, and in generic code anything mentioning a type parameter (§25.7).</summary>
    internal static bool MayHoldLoans(NovaType type) => TypeTable.IsScoped(type) || type.IsGeneric;

    /// <summary>
    /// The flow names a type written in a signature mentions (§25.6): <c>r:name</c> for a region it names (not
    /// <c>static</c>), <c>t:name</c> for a type parameter. In a result, <c>borrow(p)</c> adds parameter <c>p</c>'s names.
    /// </summary>
    private void CollectFlowNames(TypeSyntax syntax, HashSet<Symbol> typeParameters, HashSet<string> names,
        Dictionary<Symbol, HashSet<string>>? parameterNames)
    {
        void Region(TypeSyntax argument)
        {
            if (IsRegionName(argument) && argument is NamedType region && Name(region.Name.Parts[0].Name) != "static")
            {
                names.Add("r:" + Name(region.Name.Parts[0].Name));
            }
        }
        switch (syntax)
        {
            case OptionalType optional:
                CollectFlowNames(optional.Inner, typeParameters, names, parameterNames);
                break;
            case ArrayType array:
                CollectFlowNames(array.Element, typeParameters, names, parameterNames);
                break;
            case ReferenceType reference:
                foreach (var source in reference.Sources)
                {
                    if (parameterNames is not null && parameterNames.TryGetValue(source.Name, out var parameter))
                    {
                        names.UnionWith(parameter);
                    }
                    else
                    {
                        Region(new NamedType(new QualifiedName([source], source.Span), source.Span));
                    }
                }
                CollectFlowNames(reference.Pointee, typeParameters, names, parameterNames);
                break;
            case NamedType { Name.Parts.Length: 1 } named when typeParameters.Contains(named.Name.Parts[0].Name):
                names.Add("t:" + Name(named.Name.Parts[0].Name));
                break;
            case GenericType { Name.Parts.Length: 1 } generic:
            {
                var text = Name(generic.Name.Parts[0].Name);
                var regions = _items.GetValueOrDefault(generic.Name.Parts[0].Name) switch
                {
                    StructEntry s => s.Decl.RegionParameters.Length,
                    EnumEntry e => e.Decl.RegionParameters.Length,
                    _ => text == "StringView" || (text is "Slice" or "SliceMut" && generic.Arguments.Length == 2) ? 1 : 0,
                };
                var hasRegions = regions > 0 && generic.Arguments.Length > 0 && IsRegionName(generic.Arguments[0]);
                for (var i = 0; i < generic.Arguments.Length; i++)
                {
                    if (hasRegions && i < regions)
                    {
                        Region(generic.Arguments[i]);
                    }
                    else
                    {
                        CollectFlowNames(generic.Arguments[i], typeParameters, names, parameterNames);
                    }
                }
                break;
            }
        }
    }

    /// <summary>The parameters a returned reference may be derived from (type-system.md §13.5, lifetimes.md §7).</summary>
    private ImmutableArray<int> ResolveReturnSources(FunctionDecl fn, HirFunction hir)
    {
        // A slice return type has no source list: it uses the default rule.
        var syntax = fn.ReturnType as ReferenceType;
        var referenceParameters = hir.Parameters.Where(p => TypeTable.IsScoped(p.Type)).ToList();
        if (syntax is not { HasSources: true })
        {
            if (referenceParameters.Count == 0 && OnlyViews(hir.ReturnType))
            {
                return []; // string views of static text (§22.2); the return check still rejects local loans
            }
            if (referenceParameters.Count == 1)
            {
                var only = referenceParameters[0];
                if (hir.ReturnType.IsMutableReference && !only.Type.IsMutableReference)
                {
                    Error(DiagnosticCodes.ReturnSourceMismatch, fn.ReturnType!.Span,
                        $"a 'borrow mut' result cannot be derived from the shared reference parameter '{Name(only.Name)}'");
                }
                return [only.Id];
            }
            Error(DiagnosticCodes.ReturnSourceMismatch, fn.ReturnType!.Span, referenceParameters.Count == 0
                ? "a function without reference parameters cannot return a reference: there is nothing it could borrow from"
                : "this function has several reference parameters: name the ones the result is borrowed from, e.g. 'borrow(a, b) T'");
            return [];
        }

        var sources = ImmutableArray.CreateBuilder<int>();
        foreach (var source in syntax.Sources)
        {
            if (IsRegionName(new NamedType(new QualifiedName([source], source.Span), source.Span)))
            {
                continue; // a region: its parameters were found by name (§25.6)
            }
            var parameter = hir.Parameters.FirstOrDefault(p => p.Name == source.Name);
            if (parameter is null || !TypeTable.IsScoped(parameter.Type))
            {
                Error(DiagnosticCodes.ReturnSourceMismatch, source.Span, parameter is null
                    ? $"'{Name(source.Name)}' is not a parameter of '{Name(fn.Name.Name)}'"
                    : $"'{Name(source.Name)}' is not a reference parameter, so the result cannot borrow from it");
                continue;
            }
            if (hir.ReturnType.IsMutableReference && !parameter.Type.IsMutableReference)
            {
                Error(DiagnosticCodes.ReturnSourceMismatch, source.Span,
                    $"a 'borrow mut' result cannot be derived from the shared reference parameter '{Name(source.Name)}'");
                continue;
            }
            sources.Add(parameter.Id);
        }
        return sources.ToImmutable();
    }

    private void DeclareStruct(StructDecl decl)
    {
        var name = decl.Name.Name;
        var text = Name(name);
        if (IsReservedName(decl.Name))
        {
            return;
        }
        CheckRegionParameters(decl.RegionParameters, decl.TypeParameters);
        if (_types.Lookup(text) is not null)
        {
            Error(DiagnosticCodes.DuplicateItem, decl.Name.Span, $"'{text}' is the name of a built-in type");
            return;
        }
        if (_items.ContainsKey(name))
        {
            Error(DiagnosticCodes.DuplicateItem, decl.Name.Span, $"'{text}' is already defined");
            return;
        }
        if (!decl.TypeParameters.IsEmpty)
        {
            // The generic version: its own type parameters as arguments (§20.3).
            var (parameters, environment) = DeclareTypeParameters(decl.TypeParameters, new Dictionary<Symbol, NovaType>());
            var genericType = TypeTable.CreateStruct($"{text}<{string.Join(", ", parameters)}>", decl, _source);
            genericType.Struct!.TypeArguments = parameters;
            var generic = new StructEntry(decl, _source, genericType) { Environment = environment };
            _items.Add(name, generic);
            _structs.Add(generic);
            return;
        }
        var entry = new StructEntry(decl, _source, TypeTable.CreateStruct(text, decl, _source));
        _items.Add(name, entry);
        _structs.Add(entry);
    }

    /// <summary>Fresh type parameters for a generic declaration (§20.1), added to <paramref name="outer"/>.</summary>
    private (ImmutableArray<NovaType> Parameters, Dictionary<Symbol, NovaType> Environment) DeclareTypeParameters(
        ImmutableArray<TypeParameter> syntax, IReadOnlyDictionary<Symbol, NovaType> outer)
    {
        var environment = new Dictionary<Symbol, NovaType>(outer);
        var parameters = ImmutableArray.CreateBuilder<NovaType>(syntax.Length);
        foreach (var parameter in syntax)
        {
            var text = Name(parameter.Name.Name);
            if (environment.ContainsKey(parameter.Name.Name) || _types.Lookup(text) is not null || _items.ContainsKey(parameter.Name.Name))
            {
                Error(DiagnosticCodes.DuplicateItem, parameter.Name.Span, environment.ContainsKey(parameter.Name.Name)
                    ? $"type parameter '{text}' is declared twice"
                    : $"type parameter '{text}' would shadow the type or item '{text}'");
            }
            var type = TypeTable.CreateTypeParameter(text);
            environment[parameter.Name.Name] = type;
            parameters.Add(type);
        }
        // Bounds may mention any of the parameters (`T: Comparable<T>`), so they are resolved last.
        var saved = _typeEnvironment;
        _typeEnvironment = environment;
        for (var i = 0; i < syntax.Length; i++)
        {
            foreach (var bound in syntax[i].Bounds)
            {
                if (bound is NamedType { Name.Parts: [var copy] } && Name(copy.Name) == "Copy" && !_items.ContainsKey(copy.Name))
                {
                    parameters[i].IsCopyBound = true;
                }
                else if (ResolveInterface(bound) is { } resolved)
                {
                    parameters[i].Bounds.Add(resolved);
                }
            }
        }
        _typeEnvironment = saved;
        return (parameters.MoveToImmutable(), environment);
    }

    private void DeclareInterface(InterfaceDecl decl)
    {
        if (IsReservedName(decl.Name))
        {
            return;
        }
        if (_types.Lookup(Name(decl.Name.Name)) is not null || _items.ContainsKey(decl.Name.Name))
        {
            Error(DiagnosticCodes.DuplicateItem, decl.Name.Span, $"'{Name(decl.Name.Name)}' is already defined");
            return;
        }
        var entry = new InterfaceEntry(decl, _source);
        _items.Add(decl.Name.Name, entry);
        entry.Parameters = DeclareTypeParameters(decl.TypeParameters, new Dictionary<Symbol, NovaType>()).Parameters;
        foreach (var method in decl.Methods.Where(m => m.Receiver is null))
        {
            Error(DiagnosticCodes.ImplMismatch, method.Name.Span, $"interface method '{Name(method.Name.Name)}' needs a receiver: this, borrow this or borrow mut this");
        }
        foreach (var method in decl.Methods.Where(m => m.IsUnsafe))
        {
            Error(DiagnosticCodes.Unsupported, method.Name.Span, "interface methods cannot be 'unsafe' yet (type-system.md §27.4)");
        }
    }

    /// <summary>An interface reference <c>I</c> or <c>I&lt;Args&gt;</c> in a bound or an `impl` (§21); null after an error.</summary>
    private InterfaceBound? ResolveInterface(TypeSyntax syntax)
    {
        var (identifier, argumentSyntax) = syntax switch
        {
            NamedType { Name.Parts: [var only] } => (only, ImmutableArray<TypeSyntax>.Empty),
            GenericType { Name.Parts: [var only] } generic => (only, generic.Arguments),
            _ => (null, []),
        };
        if (identifier is null || _items.GetValueOrDefault(identifier.Name) is not InterfaceEntry entry)
        {
            Error(DiagnosticCodes.UnknownType, syntax.Span, identifier is null
                ? "expected an interface"
                : $"'{Name(identifier.Name)}' is not an interface");
            return null;
        }
        if (argumentSyntax.Length != entry.Parameters.Length)
        {
            Error(DiagnosticCodes.UnknownType, syntax.Span,
                $"interface '{Name(identifier.Name)}' takes {entry.Parameters.Length} type argument(s), but {argumentSyntax.Length} were given");
            return null;
        }
        var arguments = argumentSyntax.Select(a => ResolveType(a, allowVoid: false)).ToImmutableArray();
        return arguments.Any(a => a.IsBottom) ? null : new InterfaceBound(entry, Name(identifier.Name), arguments);
    }

    private static string ImplementationKey(NovaType type, InterfaceBound bound) =>
        $"{type.Id}|{RuntimeHelpers.GetHashCode(bound.Interface)}|{TypeKey(bound.Arguments)}";

    /// <summary>
    /// Every `impl I for T` (§21.1): resolves both sides, declares the methods as methods of T, and checks
    /// them against the interface (N0267). Then runs the bound checks that waited for them.
    /// </summary>
    private void ProcessImpls()
    {
        foreach (var (decl, source) in _impls)
        {
            _source = source;
            ProcessImpl(decl);
        }
        _implsReady = true;
        foreach (var check in _pendingBoundChecks)
        {
            check();
        }
        _pendingBoundChecks.Clear();
    }

    private void ProcessImpl(ImplDecl decl)
    {
        var forType = ResolveType(decl.ForType, allowVoid: false);
        var bound = ResolveInterface(decl.Interface);
        if (forType.IsBottom || bound is null)
        {
            return;
        }
        if (forType.IsGeneric || TypeTable.IsScoped(forType))
        {
            Error(DiagnosticCodes.ImplMismatch, decl.ForType.Span,
                $"'impl … for {forType}': v0 implements interfaces only for concrete, unscoped types");
            return;
        }
        if (!_implementations.Add(ImplementationKey(forType, bound)))
        {
            Error(DiagnosticCodes.ImplMismatch, decl.Span, $"'{forType}' already implements '{bound}'");
            return;
        }
        var interfaceEntry = (InterfaceEntry)bound.Interface;
        var environment = interfaceEntry.Decl.TypeParameters.Zip(bound.Arguments).ToDictionary(p => p.First.Name.Name, p => p.Second);
        if (!_implMethods.TryGetValue(forType, out var methods))
        {
            methods = [];
            _implMethods.Add(forType, methods);
        }
        var implemented = new Dictionary<Symbol, HirFunction>();
        foreach (var function in decl.Functions)
        {
            var signature = interfaceEntry.Decl.Methods.FirstOrDefault(m => m.Name.Name == function.Name.Name);
            if (signature is null)
            {
                Error(DiagnosticCodes.ImplMismatch, function.Name.Span,
                    $"'{bound}' has no method '{Name(function.Name.Name)}'; an impl block holds only the interface's methods");
                continue;
            }
            var hir = DeclareFunction(function, forType);
            if (!methods.TryAdd(function.Name.Name, hir) || !implemented.TryAdd(function.Name.Name, hir))
            {
                Error(DiagnosticCodes.DuplicateMember, function.Name.Span, $"'{forType}' already has a method named '{Name(function.Name.Name)}'");
                continue;
            }
            var expected = DeclareFunction(signature, forType, environment, register: false);
            if (SignatureMismatch(expected, hir) is { } problem)
            {
                Error(DiagnosticCodes.ImplMismatch, function.Name.Span,
                    $"'{Name(function.Name.Name)}' does not match '{bound}': {problem}");
            }
        }
        foreach (var signature in interfaceEntry.Decl.Methods.Where(m => !implemented.ContainsKey(m.Name.Name)))
        {
            Error(DiagnosticCodes.ImplMismatch, decl.Interface.Span,
                $"'impl {bound} for {forType}' is missing method '{Name(signature.Name.Name)}'");
        }
    }

    /// <summary>Null if <paramref name="actual"/> has the interface's signature <paramref name="expected"/>; else what differs.</summary>
    private static string? SignatureMismatch(HirFunction expected, HirFunction actual)
    {
        if (expected.Syntax.IsUnsafe != actual.Syntax.IsUnsafe)
        {
            return actual.Syntax.IsUnsafe ? "it must not be 'unsafe'" : "it must be 'unsafe'";
        }
        if (expected.ReceiverKind != actual.ReceiverKind)
        {
            return $"the receiver must be {ReceiverText(expected.ReceiverKind)}, not {ReceiverText(actual.ReceiverKind)}";
        }
        if (expected.Parameters.Length != actual.Parameters.Length)
        {
            return $"it must take {expected.Parameters.Length - 1} parameter(s) besides the receiver";
        }
        for (var i = 1; i < expected.Parameters.Length; i++)
        {
            if (!ReferenceEquals(expected.Parameters[i].Type, actual.Parameters[i].Type))
            {
                return $"parameter {i} must have type '{expected.Parameters[i].Type}', not '{actual.Parameters[i].Type}'";
            }
        }
        return ReferenceEquals(expected.ReturnType, actual.ReturnType)
            ? null
            : $"it must return '{expected.ReturnType}', not '{actual.ReturnType}'";
    }

    private static string ReceiverText(ReceiverKind kind) => kind switch
    {
        ReceiverKind.Borrow => "'borrow this'",
        ReceiverKind.BorrowMut => "'borrow mut this'",
        _ => "'this'",
    };

    /// <summary>
    /// Do <paramref name="arguments"/> satisfy the bounds of <paramref name="parameters"/> (§21.2)? Reported
    /// as N0268 at <paramref name="span"/>; deferred until every `impl` is known.
    /// </summary>
    private void CheckBounds(ImmutableArray<NovaType> parameters, ImmutableArray<NovaType> arguments, TextSpan span)
    {
        if (!parameters.Any(p => p.IsCopyBound || p.Bounds.Count > 0))
        {
            return;
        }
        if (!_implsReady)
        {
            var source = _source;
            _pendingBoundChecks.Add(() =>
            {
                var saved = _source;
                _source = source;
                CheckBounds(parameters, arguments, span);
                _source = saved;
            });
            return;
        }
        var map = parameters.Zip(arguments).ToDictionary(p => p.First, p => p.Second);
        foreach (var (parameter, argument) in parameters.Zip(arguments))
        {
            if (argument.IsBottom)
            {
                continue;
            }
            if (parameter.IsCopyBound && !TypeTable.IsCopy(argument))
            {
                Error(DiagnosticCodes.BoundNotSatisfied, span, $"'{argument}' is not Copy, but '{parameter}' must be ('{parameter}: Copy')");
            }
            foreach (var bound in parameter.Bounds)
            {
                var required = bound with { Arguments = [.. bound.Arguments.Select(a => Substitute(a, map))] };
                if (!Implements(argument, required))
                {
                    Error(DiagnosticCodes.BoundNotSatisfied, span, $"'{argument}' does not implement '{required}', which '{parameter}' requires");
                }
            }
        }
    }

    /// <summary>Does <paramref name="type"/> implement <paramref name="bound"/>: an `impl`, or for a type parameter, its own bounds?</summary>
    private bool Implements(NovaType type, InterfaceBound bound) => type.Kind == TypeKind.TypeParameter
        ? type.Bounds.Any(b => b.Interface == bound.Interface && b.Arguments.SequenceEqual(bound.Arguments))
        : _implementations.Contains(ImplementationKey(type, bound));

    /// <summary>A type with type parameters replaced by <paramref name="map"/> (for bounds such as <c>Comparable&lt;T&gt;</c>).</summary>
    private NovaType Substitute(NovaType type, IReadOnlyDictionary<NovaType, NovaType> map)
    {
        if (map.TryGetValue(type, out var mapped))
        {
            return mapped;
        }
        if (!type.IsGeneric)
        {
            return type;
        }
        switch (type.Kind)
        {
            case TypeKind.Reference:
                return _types.ReferenceTo(Substitute(type.Pointee!, map), type.IsMutableReference);
            case TypeKind.RawPointer:
                return _types.PointerTo(Substitute(type.Pointee!, map), type.IsMutablePointer);
            case TypeKind.Array:
                return _types.ArrayOf(Substitute(type.Element!, map), type.ArrayLength);
            case TypeKind.Box:
                return BoxOf(Substitute(type.Pointee!, map), type.Allocator!);
            case TypeKind.Enum when type.Enum!.ResultTypes is var (ok, err):
                return ResultOf(Substitute(ok, map), Substitute(err, map));
            case TypeKind.Enum when type.Enum!.OptionType is { } value:
                return OptionOf(Substitute(value, map));
            case TypeKind.Vector:
                return VectorType(type.IsString ? null : Substitute(type.Pointee!.Element!, map), type.Allocator!);
            case TypeKind.StableVector:
                return ContainerType(TypeKind.StableVector, Substitute(type.Pointee!.Element!, map), type.Allocator!);
            case TypeKind.SlotMap:
                return ContainerType(TypeKind.SlotMap, Substitute(type.Element!, map), type.Allocator!);
            case TypeKind.Handle:
                return _types.HandleOf(Substitute(type.Element!, map));
            case TypeKind.Struct when _items.GetValueOrDefault(type.Struct!.Syntax.Name.Name) is StructEntry template:
                return InstantiateStruct(template, [.. type.Struct.TypeArguments.Select(a => Substitute(a, map))]).Type;
            case TypeKind.Enum when _items.GetValueOrDefault(type.Enum!.Syntax!.Name.Name) is EnumEntry enumTemplate:
                return InstantiateEnum(enumTemplate, [.. type.Enum.TypeArguments.Select(a => Substitute(a, map))]).Type;
            default:
                return type;
        }
    }

    /// <summary>
    /// The methods a value of <paramref name="type"/> has: its own, those of its `impl` blocks, or for a
    /// type parameter, its bounds' (§21). Null if there is none of that name; reports an ambiguous bound.
    /// </summary>
    private HirFunction? FindMethod(NovaType type, Symbol name, TextSpan span)
    {
        if (type.Struct?.Functions.TryGetValue(name, out var own) == true)
        {
            return own;
        }
        if (_implMethods.TryGetValue(type, out var methods) && methods.TryGetValue(name, out var implemented))
        {
            return implemented;
        }
        if (type.Kind != TypeKind.TypeParameter)
        {
            return null;
        }
        var candidates = type.Bounds
            .Select(b => (Bound: b, Signature: ((InterfaceEntry)b.Interface).Decl.Methods.FirstOrDefault(m => m.Name.Name == name)))
            .Where(c => c.Signature is not null).ToList();
        if (candidates.Count > 1)
        {
            Error(DiagnosticCodes.ImplMismatch, span,
                $"'{Name(name)}' is ambiguous: both '{candidates[0].Bound}' and '{candidates[1].Bound}' declare it");
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        var (bound, signature) = candidates[0];
        var key = $"{type.Id}|{ImplementationKey(type, bound)}|{Name(name)}";
        if (!_boundMethods.TryGetValue(key, out var function))
        {
            var entry = (InterfaceEntry)bound.Interface;
            var environment = entry.Decl.TypeParameters.Zip(bound.Arguments).ToDictionary(p => p.First.Name.Name, p => p.Second);
            var savedSource = _source;
            _source = entry.Source;
            function = DeclareFunction(signature!, type, environment, register: false);
            _source = savedSource;
            _boundMethods.Add(key, function);
        }
        return function;
    }

    /// <summary>A stable key for a list of types (identity-based: distinct types never share a key).</summary>
    private static string TypeKey(ImmutableArray<NovaType> types) => string.Join(",", types.Select(t => t.Id));

    /// <summary>
    /// The instantiation of generic struct <paramref name="template"/> with <paramref name="arguments"/>
    /// (§20.4). Made once; its members are resolved at once if the declaration phase is over.
    /// </summary>
    private StructEntry InstantiateStruct(StructEntry template, ImmutableArray<NovaType> arguments, TextSpan span = default)
    {
        if (arguments.SequenceEqual(template.Type.Struct!.TypeArguments))
        {
            return template;
        }
        CheckBounds(template.Type.Struct.TypeArguments, arguments, span);
        var key = TypeKey(arguments);
        if (template.Instances.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var text = Name(template.Decl.Name.Name);
        var type = TypeTable.CreateStruct($"{text}<{string.Join(", ", arguments)}>", template.Decl, template.Source);
        type.Struct!.TypeArguments = arguments;
        var environment = template.Decl.TypeParameters.Zip(arguments).ToDictionary(p => p.First.Name.Name, p => p.Second);
        var entry = new StructEntry(template.Decl, template.Source, type) { Environment = environment, Template = template };
        template.Instances.Add(key, entry);
        _structs.Add(entry);
        if (_membersResolved)
        {
            _instantiating++;
            CheckInstance(() => { ResolveStructMembers(entry); return 0; }, arguments, text, _source, span); // checked in the generic version
            _instantiating--;
            FinishLateTypes();
        }
        return entry;
    }

    /// <summary>As <see cref="InstantiateStruct"/>, for enums.</summary>
    private EnumEntry InstantiateEnum(EnumEntry template, ImmutableArray<NovaType> arguments, TextSpan span = default)
    {
        if (arguments.SequenceEqual(template.Type.Enum!.TypeArguments))
        {
            return template;
        }
        CheckBounds(template.Type.Enum.TypeArguments, arguments, span);
        var key = TypeKey(arguments);
        if (template.Instances.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var text = Name(template.Decl.Name.Name);
        var type = TypeTable.CreateEnum($"{text}<{string.Join(", ", arguments)}>", template.Decl, template.Source);
        type.Enum!.TypeArguments = arguments;
        var environment = template.Decl.TypeParameters.Zip(arguments).ToDictionary(p => p.First.Name.Name, p => p.Second);
        var entry = new EnumEntry(template.Decl, template.Source, type) { Environment = environment, Template = template };
        template.Instances.Add(key, entry);
        _enums.Add(entry);
        if (_membersResolved)
        {
            _instantiating++;
            CheckInstance(() => { ResolveEnumVariants(entry); return 0; }, arguments, text, _source, span); // checked in the generic version
            _instantiating--;
            FinishLateTypes();
        }
        return entry;
    }

    /// <summary>Nesting of instantiations whose members are being resolved (nested ones are finished with the outermost).</summary>
    private int _instantiating;

    /// <summary>
    /// Recursion, Copy and drop for types instantiated after the declaration phase. Only once the
    /// outermost instantiation is complete: an outer instance has no fields yet while an inner one is made.
    /// </summary>
    private void FinishLateTypes()
    {
        if (_copyKnown && _instantiating == 0)
        {
            CheckStructRecursion();
            ComputeCopyAndDrop();
        }
    }

    /// <summary>
    /// The instantiation of generic function <paramref name="entry"/> with <paramref name="arguments"/>
    /// (§20.4); null after N0266 (instantiation too deep).
    /// </summary>
    private HirFunction? InstantiateFunction(GenericFunctionEntry entry, ImmutableArray<NovaType> arguments, TextSpan span)
    {
        if (arguments.SequenceEqual(entry.Parameters))
        {
            return entry.Generic;
        }
        CheckBounds(entry.Parameters, arguments, span);
        var key = TypeKey(arguments);
        if (entry.Instances.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var depth = (_function?.InstantiationDepth ?? 0) + 1;
        if (depth > 64)
        {
            Error(DiagnosticCodes.InstantiationTooDeep, span,
                $"instantiating '{Name(entry.Decl.Name.Name)}' here makes more than 64 nested instantiations (polymorphic recursion?)");
            return null;
        }
        var environment = entry.Decl.TypeParameters.Zip(arguments).ToDictionary(p => p.First.Name.Name, p => p.Second);
        var savedSource = _source;
        _source = entry.Source;
        // The generic version's signature was checked; the instantiation's would only repeat it (§20.3).
        var instance = CheckInstance(() => DeclareFunction(entry.Decl, owner: null, environment), arguments,
            Name(entry.Decl.Name.Name), savedSource, span);
        _source = savedSource;
        instance.TypeArguments = arguments;
        instance.InstantiationDepth = depth;
        instance.InstantiationSite = (savedSource, span);
        entry.Instances.Add(key, instance);
        return instance;
    }

    private void DeclareEnum(EnumDecl decl)
    {
        var name = decl.Name.Name;
        var text = Name(name);
        if (IsReservedName(decl.Name))
        {
            return;
        }
        CheckRegionParameters(decl.RegionParameters, decl.TypeParameters);
        if (_types.Lookup(text) is not null || _items.ContainsKey(name))
        {
            Error(DiagnosticCodes.DuplicateItem, decl.Name.Span, _types.Lookup(text) is not null
                ? $"'{text}' is the name of a built-in type"
                : $"'{text}' is already defined");
            return;
        }
        if (!decl.TypeParameters.IsEmpty)
        {
            var (parameters, environment) = DeclareTypeParameters(decl.TypeParameters, new Dictionary<Symbol, NovaType>());
            var genericType = TypeTable.CreateEnum($"{text}<{string.Join(", ", parameters)}>", decl, _source);
            genericType.Enum!.TypeArguments = parameters;
            var generic = new EnumEntry(decl, _source, genericType) { Environment = environment };
            _items.Add(name, generic);
            _enums.Add(generic);
            return;
        }
        var entry = new EnumEntry(decl, _source, TypeTable.CreateEnum(text, decl, _source));
        _items.Add(name, entry);
        _enums.Add(entry);
    }

    private void ResolveEnumVariants(EnumEntry entry)
    {
        var (savedSource, savedEnvironment, savedRegions) = (_source, _typeEnvironment, _regions);
        _typeEnvironment = entry.Environment;
        _regions = entry.Decl.RegionParameters.Select(r => r.Name.Name).ToHashSet();
        try
        {
            ResolveEnumVariantsCore(entry);
        }
        finally
        {
            (_source, _typeEnvironment, _regions) = (savedSource, savedEnvironment, savedRegions);
        }
    }

    private void ResolveEnumVariantsCore(EnumEntry entry)
    {
        _source = entry.Source;
        var definition = entry.Type.Enum!;
        foreach (var variantDecl in entry.Decl.Variants)
        {
            if (definition.FindVariant(variantDecl.Name.Name) is not null)
            {
                Error(DiagnosticCodes.DuplicateMember, variantDecl.Name.Span,
                    $"'{entry.Type}' already has a variant named '{Name(variantDecl.Name.Name)}'");
                continue;
            }
            var variant = new EnumVariant(variantDecl.Name.Name, definition.Variants.Count, variantDecl.Name.Span);
            for (var i = 0; i < variantDecl.Payload.Length; i++)
            {
                var type = ResolveType(variantDecl.Payload[i], allowVoid: false);
                if (TypeTable.HoldsRegionValue(type))
                {
                    Error(DiagnosticCodes.ReferenceNotAllowed, variantDecl.Payload[i].Span,
                        $"an enum payload cannot hold '{type}': region values and pool boxes stay in locals (type-system.md §25.4)");
                    type = _types.Error;
                }
                else if (TypeTable.IsScoped(type) && entry.Decl.RegionParameters.IsEmpty && entry.Template is null)
                {
                    Error(DiagnosticCodes.ScopedFieldNeedsRegion, variantDecl.Payload[i].Span,
                        $"a payload of scoped type '{type}' needs a region parameter on the enum: 'enum {Name(entry.Decl.Name.Name)}<region r>' (type-system.md §25.4)");
                    type = _types.Error;
                }
                variant.Payload.Add(new StructField(_symbols.Intern($"_{i}"), type, i, variantDecl.Payload[i].Span));
            }
            definition.Variants.Add(variant);
        }
    }

    private void ResolveStructMembers(StructEntry entry)
    {
        var (savedSource, savedEnvironment, savedRegions) = (_source, _typeEnvironment, _regions);
        _typeEnvironment = entry.Environment;
        _regions = entry.Decl.RegionParameters.Select(r => r.Name.Name).ToHashSet();
        try
        {
            ResolveStructMembersCore(entry);
        }
        finally
        {
            (_source, _typeEnvironment, _regions) = (savedSource, savedEnvironment, savedRegions);
        }
    }

    private void ResolveStructMembersCore(StructEntry entry)
    {
        _source = entry.Source;
        var definition = entry.Type.Struct!;
        var memberNames = new HashSet<Symbol>();
        foreach (var field in entry.Decl.Fields)
        {
            var type = ResolveType(field.Type, allowVoid: false);
            if (TypeTable.HoldsRegionValue(type))
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, field.Type.Span,
                    $"a struct field cannot hold '{type}': region values and pool boxes stay in locals (type-system.md §25.4)");
                type = _types.Error;
            }
            else if (TypeTable.IsScoped(type) && entry.Decl.RegionParameters.IsEmpty && entry.Template is null)
            {
                Error(DiagnosticCodes.ScopedFieldNeedsRegion, field.Type.Span,
                    $"a field of scoped type '{type}' needs a region parameter on the struct: 'struct {Name(entry.Decl.Name.Name)}<region r>' (type-system.md §25.4)");
                type = _types.Error;
            }
            if (!memberNames.Add(field.Name.Name))
            {
                Error(DiagnosticCodes.DuplicateMember, field.Name.Span,
                    $"'{entry.Type}' already has a member named '{Name(field.Name.Name)}'");
                continue;
            }
            definition.AddField(field.Name.Name, type, field.Name.Span);
        }
        if (entry.Decl.Drop is { } drop)
        {
            // The drop block is checked as a `borrow mut this` method that user code cannot call.
            var syntax = new FunctionDecl(Visibility.Private, new Identifier(_symbols.Intern("drop"), drop.Span),
                new Receiver(ReceiverKind.BorrowMut, drop.Span), [], null, drop.Body, null, drop.Span);
            definition.DropFunction = DeclareFunction(syntax, entry.Type, entry.Environment);
        }
        foreach (var function in entry.Decl.Functions)
        {
            if (!memberNames.Add(function.Name.Name)
                || (_implMethods.TryGetValue(entry.Type, out var implemented) && implemented.ContainsKey(function.Name.Name)))
            {
                Error(DiagnosticCodes.DuplicateMember, function.Name.Span,
                    $"'{entry.Type}' already has a member named '{Name(function.Name.Name)}'");
                continue;
            }
            definition.Functions.Add(function.Name.Name, DeclareFunction(function, entry.Type, entry.Environment));
        }
    }

    /// <summary>The types an aggregate contains by value, with the declaring span (struct fields, enum payloads).</summary>
    /// <remarks>
    /// Arrays and results contain their element, <c>T</c> and <c>E</c> by value, so they are looked
    /// through: only user structs and enums are nodes of the containment graph.
    /// </remarks>
    private static IEnumerable<(NovaType Type, TextSpan Span, SourceText Source)> Contained(NovaType type) => type.Kind switch
    {
        TypeKind.Struct => type.Struct!.Fields.SelectMany(f => Flatten(f.Type).Select(t => (t, f.Span, type.Struct.Source))),
        TypeKind.Enum when type.Enum!.Source is { } source =>
            type.Enum.Variants.SelectMany(v => v.Payload).SelectMany(f => Flatten(f.Type).Select(t => (t, f.Span, source))),
        _ => [],
    };

    /// <summary>The types a value of <paramref name="type"/> holds by value, looking through arrays and results.</summary>
    private static IEnumerable<NovaType> Flatten(NovaType type) => type switch
    {
        { Kind: TypeKind.Array } => Flatten(type.Element!),
        { Enum.ResultTypes: var (ok, err) } => Flatten(ok).Concat(Flatten(err)),
        { Enum.OptionType: { } value } => Flatten(value),
        _ => [type],
    };

    private static bool IsAggregate(NovaType type) => type.Kind is TypeKind.Struct or TypeKind.Enum;

    /// <summary>An aggregate may not contain itself by value, directly or indirectly (N0227).</summary>
    // Kept across calls: instantiations made later are checked incrementally (§20.4).
    private readonly Dictionary<NovaType, int> _recursionState = []; // 1 = on the DFS stack, 2 = finished
    private readonly HashSet<NovaType> _copyDone = [];

    private void CheckStructRecursion()
    {
        var state = _recursionState;
        var stack = new List<NovaType>();

        void Visit(NovaType type)
        {
            state[type] = 1;
            stack.Add(type);
            foreach (var (inner, span, source) in Contained(type).Where(c => IsAggregate(c.Type)))
            {
                switch (state.GetValueOrDefault(inner))
                {
                    case 0:
                        Visit(inner);
                        break;
                    case 1:
                        // Every type on the cycle is recursive; report at the one that closes it.
                        foreach (var member in stack.Skip(stack.IndexOf(inner)))
                        {
                            if (member.Struct is { } s)
                            {
                                s.IsRecursive = true;
                            }
                            else
                            {
                                member.Enum!.IsRecursive = true;
                            }
                        }
                        _source = source;
                        Error(DiagnosticCodes.RecursiveStruct, span,
                            $"'{type}' contains '{inner}' by value, which contains '{type}': the type would have infinite size");
                        break;
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[type] = 2;
        }

        foreach (var type in _structs.Select(s => s.Type).Concat(_enums.Select(e => e.Type)))
        {
            if (state.GetValueOrDefault(type) == 0)
            {
                Visit(type);
            }
        }
    }

    /// <summary>Copy and needs-drop for every struct and enum (type-system.md §14.1, §15.1); contained types first.</summary>
    private void ComputeCopyAndDrop()
    {
        var done = _copyDone;
        void Visit(NovaType type)
        {
            if (!done.Add(type) || type.Struct is { IsRecursive: true } || type.Enum is { IsRecursive: true })
            {
                return;
            }
            var contained = Contained(type).Select(c => c.Type).ToList();
            foreach (var inner in contained.Where(IsAggregate))
            {
                Visit(inner);
            }
            if (type.Struct is { } definition)
            {
                definition.NeedsDrop = definition.DropFunction is not null || contained.Any(TypeTable.NeedsDrop);
                // `@noncopy` opts out (OD-7; type-system.md §29.2); instances inherit it from their template's declaration.
                definition.IsCopy = definition.DropFunction is null && contained.All(TypeTable.IsCopy) && !IsNonCopy(definition);
            }
            else if (type.Enum is { } enumDefinition)
            {
                enumDefinition.NeedsDrop = contained.Any(TypeTable.NeedsDrop);
                enumDefinition.IsCopy = contained.All(TypeTable.IsCopy);
            }
        }
        foreach (var type in _structs.Select(s => s.Type).Concat(_enums.Select(e => e.Type)))
        {
            Visit(type);
        }
    }

    private NovaType ResolveType(TypeSyntax syntax, bool allowVoid, bool allowSources = false, bool allowCChar = false)
    {
        var type = ResolveTypeCore(syntax, allowVoid, allowSources, allowCChar);
        if (_profile == Profile.Freestanding && IsHostedOnly(type))
        {
            HostedOnly(syntax.Span, $"'{type}'");
            return _types.Error;
        }
        return type;
    }

    private NovaType ResolveTypeCore(TypeSyntax syntax, bool allowVoid, bool allowSources, bool allowCChar)
    {
        if (syntax is OptionalType optional)
        {
            var value = ResolveType(optional.Inner, allowVoid: false, allowSources);
            return value.IsBottom ? _types.Error : OptionOf(value);
        }
        if (syntax is ReferenceType reference)
        {
            if (reference.HasSources && !allowSources)
            {
                // Outside a return type, the list names regions (§25.2), which are erased.
                if (_regions.Count == 0 && reference.Sources.All(s => Name(s.Name) != "static"))
                {
                    Error(DiagnosticCodes.ReferenceNotAllowed, reference.Span,
                        "a source list 'borrow(...)' is only allowed in a function's return type, or naming regions (type-system.md §25.2)");
                }
                else
                {
                    foreach (var source in reference.Sources)
                    {
                        CheckRegionArgument(new NamedType(new QualifiedName([source], source.Span), source.Span));
                    }
                }
            }
            var pointee = ResolveType(reference.Pointee, allowVoid: false);
            if (pointee.IsReference)
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, reference.Span, "references to references are not supported");
                return _types.Error;
            }
            return pointee.IsBottom ? _types.Error : _types.ReferenceTo(pointee, reference.IsMutable);
        }
        if (syntax is FunctionPointerType function)
        {
            return ResolveFunctionPointerType(function);
        }
        if (syntax is PointerType pointer)
        {
            var pointee = ResolveType(pointer.Pointee, allowVoid: true, allowCChar: true);
            if (pointee.IsBottom)
            {
                return _types.Error;
            }
            if (TypeTable.IsScoped(pointee) || pointee.IsUnsized)
            {
                Error(DiagnosticCodes.InvalidPointee, pointer.Pointee.Span,
                    $"a raw pointer cannot point to '{pointee}', which holds loans; point to the referenced type instead (type-system.md §27.1)");
                return _types.Error;
            }
            return _types.PointerTo(pointee, pointer.IsMutable);
        }
        if (syntax is ArrayType arrayType)
        {
            var element = ResolveType(arrayType.Element, allowVoid: false);
            if (arrayType.Length is not IntLiteralExpr { Suffix: IntSuffix.None or IntSuffix.USize } literal
                || literal.Value == 0 || literal.Value > MaxArrayLength)
            {
                Error(DiagnosticCodes.InvalidArray, arrayType.Length.Span,
                    $"an array length must be an integer literal from 1 to {MaxArrayLength}");
                return _types.Error;
            }
            return ArrayType(element, (long)literal.Value, arrayType.Element.Span);
        }
        if (syntax is GenericType { Name.Parts.Length: 1 } userGeneric
            && _items.GetValueOrDefault(userGeneric.Name.Parts[0].Name) is StructEntry or EnumEntry)
        {
            return ResolveGenericItemType(userGeneric);
        }
        if (syntax is GenericType generic)
        {
            var genericName = generic.Name.Parts.Length == 1 ? Name(generic.Name.Parts[0].Name) : "";
            if (genericName == "StringView" && generic.Arguments.Length == 1)
            {
                CheckRegionArgument(generic.Arguments[0]);
                return _types.StringView; // regions are erased (§25.5)
            }
            if (genericName is "Slice" or "SliceMut" && generic.Arguments.Length == 2 && IsRegionName(generic.Arguments[0]))
            {
                CheckRegionArgument(generic.Arguments[0]);
                var viewed = ResolveType(generic.Arguments[1], allowVoid: false);
                return CheckElement(viewed, -1, generic.Arguments[1].Span) ? _types.SliceOf(viewed, genericName == "SliceMut") : _types.Error;
            }
            if (genericName == "Box" && generic.Arguments.Length is 1 or 2)
            {
                var contents = ResolveType(generic.Arguments[0], allowVoid: false);
                if (TypeTable.IsScoped(contents))
                {
                    Error(DiagnosticCodes.ReferenceNotAllowed, generic.Arguments[0].Span, "a 'Box' cannot hold a reference or slice in v0");
                    return _types.Error;
                }
                var allocator = generic.Arguments.Length == 2 ? ResolveType(generic.Arguments[1], allowVoid: false) : Used(_systemHeap).Type;
                if (!allocator.IsBottom && !IsAllocatorType(allocator))
                {
                    Error(DiagnosticCodes.NotAnAllocator, generic.Arguments[1].Span,
                        $"'{allocator}' is not an allocator type; use 'SystemHeap' or 'FailingAllocator'");
                    return _types.Error;
                }
                return contents.IsBottom || allocator.IsBottom ? _types.Error : BoxOf(contents, allocator);
            }
            if (genericName is "Pool" or "PoolBox" && generic.Arguments.Length == 1)
            {
                var pooled = ResolveType(generic.Arguments[0], allowVoid: false);
                if (pooled.IsBottom || !CheckPoolElement(pooled, generic.Arguments[0].Span))
                {
                    return _types.Error;
                }
                return genericName == "Pool" ? _types.PoolOf(pooled) : _types.PoolBoxOf(pooled);
            }
            if ((genericName == "Vector" && generic.Arguments.Length is 1 or 2) || (genericName == "String" && generic.Arguments.Length == 1))
            {
                return ResolveVectorType(genericName == "String", generic.Arguments, generic.Span);
            }
            if (genericName is "SlotMap" or "StableVector" && generic.Arguments.Length is 1 or 2)
            {
                return ResolveContainerType(genericName == "SlotMap" ? TypeKind.SlotMap : TypeKind.StableVector, generic.Arguments);
            }
            if (genericName == "Handle" && generic.Arguments.Length == 1)
            {
                var handled = ResolveType(generic.Arguments[0], allowVoid: false);
                if (handled.IsBottom)
                {
                    return _types.Error;
                }
                return _types.HandleOf(handled); // a handle holds no loan, whatever it names (§25.3)
            }
            if (genericName == "Result" && generic.Arguments.Length == 2)
            {
                var ok = ResolveType(generic.Arguments[0], allowVoid: true);
                var err = ResolveType(generic.Arguments[1], allowVoid: false);
                // A result holding a reference or slice is itself scoped (§19.1).
                return ok.IsBottom || err.IsBottom ? _types.Error : ResultOf(ok, err);
            }
            if (genericName is not ("Slice" or "SliceMut") || generic.Arguments.Length != 1)
            {
                Error(DiagnosticCodes.UnknownType, generic.Span, genericName is "Slice" or "SliceMut"
                    ? $"'{genericName}' takes one type argument: '{genericName}<T>'"
                    : genericName == "Result" ? "'Result' takes two type arguments: 'Result<T, E>'"
                    : genericName == "Box" ? "'Box' takes one or two type arguments: 'Box<T>' or 'Box<T, A>'"
                    : genericName is "Pool" or "PoolBox" ? $"'{genericName}' takes one type argument: '{genericName}<T>'"
                    : genericName == "Vector" ? "'Vector' takes one or two type arguments: 'Vector<T>' or 'Vector<T, A>'"
                    : genericName == "String" ? "'String' takes at most one type argument: 'String' or 'String<A>'"
                    : genericName is "SlotMap" or "StableVector" ? $"'{genericName}' takes one or two type arguments: '{genericName}<T>' or '{genericName}<T, A>'"
                    : genericName == "Handle" ? "'Handle' takes one type argument: 'Handle<T>'"
                    : "only 'Slice<T>', 'SliceMut<T>', 'Result<T, E>', 'Box<T, A>', 'Pool<T>', 'PoolBox<T>', 'Vector<T, A>' and 'String<A>' take type arguments");
                return _types.Error;
            }
            var element = ResolveType(generic.Arguments[0], allowVoid: false);
            return CheckElement(element, -1, generic.Arguments[0].Span)
                ? _types.SliceOf(element, genericName == "SliceMut")
                : _types.Error;
        }
        if (syntax is not NamedType named)
        {
            return _types.Error;
        }
        if (named.Name.Parts.Length > 1)
        {
            Error(DiagnosticCodes.Unsupported, named.Span, "qualified type names are not supported yet");
            return _types.Error;
        }

        if (_typeEnvironment.TryGetValue(named.Name.Parts[0].Name, out var parameter))
        {
            return parameter; // a type parameter in scope, or what it is instantiated with (§20)
        }
        var text = Name(named.Name.Parts[0].Name);
        if (text is "String" or "Vector" or "SlotMap" or "StableVector" or "Handle" && _items.GetValueOrDefault(named.Name.Parts[0].Name) is null)
        {
            if (text != "String")
            {
                Error(DiagnosticCodes.UnknownType, named.Span, $"'{text}' needs its element type: '{text}<T>'");
                return _types.Error;
            }
            return ResolveVectorType(isString: true, [], named.Span);
        }
        if (_items.GetValueOrDefault(named.Name.Parts[0].Name) is StructEntry { Decl.TypeParameters.IsEmpty: false }
            or EnumEntry { Decl.TypeParameters.IsEmpty: false })
        {
            Error(DiagnosticCodes.UnknownType, named.Span, $"'{text}' is generic: give its type arguments, e.g. '{text}<i32>'");
            return _types.Error;
        }
        var type = _types.Lookup(text) ?? _items.GetValueOrDefault(named.Name.Parts[0].Name) switch
        {
            StructEntry s => Used(s).Type,
            EnumEntry e => Used(e).Type,
            _ => null,
        };
        if (type is null)
        {
            Error(DiagnosticCodes.UnknownType, named.Span, $"unknown type '{text}'");
            return _types.Error;
        }
        if (type.Kind == TypeKind.Void && !allowVoid)
        {
            Error(DiagnosticCodes.VoidNotAllowed, named.Span, "'void' can only be used as a function return type");
            return _types.Error;
        }
        if (type.Kind == TypeKind.CChar && !allowCChar)
        {
            Error(DiagnosticCodes.InvalidPointee, named.Span, "'CChar' is C's 'char' and can only be pointed to: use '*CChar' (type-system.md §28.3)");
            return _types.Error;
        }
        return type;
    }

    private T Used<T>(T entry)
        where T : notnull
    {
        if (_builtinItems.Contains(entry))
        {
            _usedBuiltins.Add(entry);
        }
        return entry;
    }

    /// <summary><c>Vector&lt;T, A&gt;</c> (<c>[T]</c> or <c>[T, A]</c>) or <c>String&lt;A&gt;</c> (<c>[]</c> or <c>[A]</c>) from type syntax (§23).</summary>
    private NovaType ResolveVectorType(bool isString, ImmutableArray<TypeSyntax> arguments, TextSpan span)
    {
        var element = isString ? null : ResolveType(arguments[0], allowVoid: false);
        var allocatorIndex = isString ? 0 : 1;
        var allocator = arguments.Length > allocatorIndex ? ResolveType(arguments[allocatorIndex], allowVoid: false) : Used(_systemHeap).Type;
        if (element is { IsBottom: true } || allocator.IsBottom)
        {
            return _types.Error;
        }
        if (!IsAllocatorType(allocator))
        {
            Error(DiagnosticCodes.NotAnAllocator, arguments[allocatorIndex].Span,
                $"'{allocator}' is not an allocator type; use 'SystemHeap' or 'FailingAllocator'");
            return _types.Error;
        }
        if (element is not null && TypeTable.HoldsRegionValue(element))
        {
            Error(DiagnosticCodes.ReferenceNotAllowed, arguments[0].Span,
                $"a 'Vector' cannot hold '{element}': region values and pool boxes stay in locals (type-system.md §25.4)");
            return _types.Error;
        }
        return VectorType(element, allocator); // scoped elements make a scoped vector (§25.3, §25.4)
    }

    /// <summary><c>SlotMap&lt;T, A&gt;</c> or <c>StableVector&lt;T, A&gt;</c> from type syntax (§24).</summary>
    private NovaType ResolveContainerType(TypeKind kind, ImmutableArray<TypeSyntax> arguments)
    {
        var element = ResolveType(arguments[0], allowVoid: false);
        var allocator = arguments.Length > 1 ? ResolveType(arguments[1], allowVoid: false) : Used(_systemHeap).Type;
        if (element.IsBottom || allocator.IsBottom)
        {
            return _types.Error;
        }
        var name = kind == TypeKind.SlotMap ? "SlotMap" : "StableVector";
        if (!IsAllocatorType(allocator))
        {
            Error(DiagnosticCodes.NotAnAllocator, arguments[1].Span, $"'{allocator}' is not an allocator type; use 'SystemHeap' or 'FailingAllocator'");
            return _types.Error;
        }
        if (TypeTable.HoldsRegionValue(element))
        {
            Error(DiagnosticCodes.ReferenceNotAllowed, arguments[0].Span,
                $"a '{name}' cannot hold '{element}': region values and pool boxes stay in locals (type-system.md §25.4)");
            return _types.Error;
        }
        return ContainerType(kind, element, allocator); // scoped elements make a scoped container (§25.3, §25.4)
    }

    /// <summary>The slot map or stable vector, with the types its built-in operations return (§24).</summary>
    private NovaType ContainerType(TypeKind kind, NovaType element, NovaType allocator)
    {
        var allocError = Used(_allocError).Type;
        if (kind == TypeKind.SlotMap)
        {
            ResultOf(_types.HandleOf(element), allocError);
            OptionOf(_types.ReferenceTo(element, isMutable: false));
            OptionOf(_types.ReferenceTo(element, isMutable: true));
            OptionOf(element);
        }
        else
        {
            ResultOf(_types.USize, allocError);
        }
        return _types.ContainerOf(kind, element, allocator, allocator == _systemHeap.Type);
    }

    /// <summary>
    /// The vector or string type, with the types its built-in operations return: <c>Result&lt;void, AllocError&gt;</c>
    /// and, for a vector, <c>T?</c> (§23).
    /// </summary>
    private NovaType VectorType(NovaType? element, NovaType allocator)
    {
        ResultOf(_types.Void, Used(_allocError).Type);
        if (element is not null)
        {
            OptionOf(element);
        }
        return _types.VectorOf(element, allocator, allocator == _systemHeap.Type);
    }

    private NovaType BoxOf(NovaType contents, NovaType allocator) =>
        _types.BoxOf(contents, allocator, allocator == _systemHeap.Type);

    /// <summary>
    /// <c>Box.create(v)</c> / <c>Box.createIn(a, v)</c> (type-system.md §18.2): a <c>Result&lt;Box&lt;T, A&gt;, AllocError&gt;</c>.
    /// The expected type, if it is such a result, gives the type hint for <c>v</c>.
    /// </summary>
    private HirExpr CheckBoxCreate(CallExpr call, MemberExpr member, NovaType? expected)
    {
        var text = Name(member.Member.Name);
        var withAllocator = text == "createIn";
        if (text is not ("create" or "createIn"))
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'Box' has no function '{text}'; use 'Box.create(v)' or 'Box.createIn(allocator, v)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (call.Arguments.Length != (withAllocator ? 2 : 1))
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, withAllocator
                ? "'Box.createIn' takes an allocator and a value: 'Box.createIn(allocator, v)'"
                : "'Box.create' takes one value: 'Box.create(v)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var arguments = ImmutableArray.CreateBuilder<HirExpr>();
        var allocator = Used(_systemHeap).Type;
        if (withAllocator)
        {
            var allocatorValue = AutoDeref(Synth(call.Arguments[0], null));
            if (allocatorValue.Type.IsBottom)
            {
                Synth(call.Arguments[1], null);
                return ErrorResult(call.Span);
            }
            if (!IsAllocatorType(allocatorValue.Type))
            {
                Error(DiagnosticCodes.NotAnAllocator, call.Arguments[0].Span,
                    $"'{allocatorValue.Type}' is not an allocator type; use 'SystemHeap {{}}' or 'FailingAllocator {{}}'");
                Synth(call.Arguments[1], null);
                return ErrorResult(call.Span);
            }
            allocator = allocatorValue.Type;
            arguments.Add(allocatorValue);
        }
        var hint = expected?.Enum?.ResultTypes is ({ IsBox: true } box, _) ? box.Pointee : null;
        var valueSyntax = call.Arguments[^1];
        var value = hint is not null ? CheckExpr(valueSyntax, hint) : Synth(valueSyntax, null);
        if (hint is null)
        {
            value = Coerce(value, value.Type, valueSyntax.Span); // a `borrow mut` variable is reborrowed
        }
        if (value.Type.IsBottom)
        {
            return ErrorResult(call.Span);
        }
        if (value.Type.Kind is TypeKind.Void or TypeKind.Never || TypeTable.IsScoped(value.Type))
        {
            Error(TypeTable.IsScoped(value.Type) ? DiagnosticCodes.ReferenceNotAllowed : DiagnosticCodes.VoidValue, valueSyntax.Span, TypeTable.IsScoped(value.Type)
                ? "a 'Box' cannot hold a reference or slice in v0"
                : $"this expression has no value (its type is '{value.Type}')");
            return ErrorResult(call.Span);
        }
        arguments.Add(value);
        var result = ResultOf(BoxOf(value.Type, allocator), Used(_allocError).Type);
        return new HirBuiltinCall(BuiltinFunction.BoxCreate, arguments.ToImmutable(), result, call.Span);
    }

    /// <summary><c>Arena.create(allocator, chunkSize)</c> (type-system.md §19.2): cannot fail.</summary>
    private HirExpr CheckArenaCreate(CallExpr call, MemberExpr member)
    {
        if (Name(member.Member.Name) != "create" || call.Arguments.Length != 2)
        {
            Error(Name(member.Member.Name) != "create" ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, call.Span,
                "an arena is made with 'Arena.create(allocator, chunkSize)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var allocator = AutoDeref(Synth(call.Arguments[0], null));
        var chunkSize = CheckExpr(call.Arguments[1], _types.U64);
        if (allocator.Type.IsBottom)
        {
            return ErrorResult(call.Span);
        }
        if (!IsAllocatorType(allocator.Type))
        {
            Error(DiagnosticCodes.NotAnAllocator, call.Arguments[0].Span,
                $"'{allocator.Type}' is not an allocator type; use 'SystemHeap {{}}' or 'FailingAllocator {{}}'");
            return ErrorResult(call.Span);
        }
        return new HirBuiltinCall(BuiltinFunction.ArenaCreate, [allocator, chunkSize], _types.Arena, call.Span);
    }

    /// <summary>
    /// A reference to the receiver of a region method (§19.2): shared for allocation, mutable for
    /// <c>reset</c>. Null after reporting an error.
    /// </summary>
    private HirExpr? RegionReceiver(HirExpr receiver, NovaType regionType, bool isMutable, MemberExpr member)
    {
        var referenceType = _types.ReferenceTo(regionType, isMutable);
        if (receiver.Type.IsReference)
        {
            if (isMutable && !receiver.Type.IsMutableReference)
            {
                BorrowMutThroughShared(member);
                return null;
            }
            return Coerce(receiver, referenceType, member.Target.Span);
        }
        if (isMutable)
        {
            var borrowed = BorrowPlace(receiver, isMutable: true, member.Target.Span);
            return borrowed.Type.IsBottom ? null : borrowed;
        }
        return ToPlace(receiver) is { } place
            ? new HirBorrow(false, place, referenceType, member.Target.Span)
            : new HirBorrowTemp(false, receiver, referenceType, member.Target.Span);
    }

    /// <summary>A pool holds values one by one, so they may need drop; but nothing scoped (E3, N0264).</summary>
    private bool CheckPoolElement(NovaType element, TextSpan span)
    {
        if (element.Kind is TypeKind.Void or TypeKind.Never || TypeTable.IsScoped(element))
        {
            Error(DiagnosticCodes.NotRegionContent, span, $"a pool cannot hold a '{element}': {(TypeTable.IsScoped(element)
                ? "it is scoped (a reference or slice), and pool memory cannot hold loans (E3)" : "it has no value")}");
            return false;
        }
        return true;
    }

    /// <summary><c>Pool.create(allocator, capacity)</c> (§19.6): <c>T</c> comes from the expected <c>Pool&lt;T&gt;</c>.</summary>
    private HirExpr CheckPoolCreate(CallExpr call, MemberExpr member, NovaType? expected)
    {
        if (Name(member.Member.Name) != "create" || call.Arguments.Length != 2)
        {
            Error(Name(member.Member.Name) != "create" ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, call.Span,
                "a pool is made with 'Pool.create(allocator, capacity)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var allocator = AutoDeref(Synth(call.Arguments[0], null));
        var capacity = CheckExpr(call.Arguments[1], _types.U32);
        if (expected is not { Region: RegionKind.Pool })
        {
            if (expected is null || !expected.IsBottom)
            {
                Error(DiagnosticCodes.ResultTypeUnknown, call.Span, expected is null
                    ? "the element type of 'Pool.create(...)' is unknown here; give the variable a type: 'let pool: Pool<T> = …'"
                    : $"'Pool.create(...)' makes a pool, but '{expected}' is expected here");
            }
            return ErrorResult(call.Span);
        }
        if (allocator.Type.IsBottom)
        {
            return ErrorResult(call.Span);
        }
        if (!IsAllocatorType(allocator.Type))
        {
            Error(DiagnosticCodes.NotAnAllocator, call.Arguments[0].Span,
                $"'{allocator.Type}' is not an allocator type; use 'SystemHeap {{}}' or 'FailingAllocator {{}}'");
            return ErrorResult(call.Span);
        }
        return new HirBuiltinCall(BuiltinFunction.PoolCreate, [allocator, capacity], expected, call.Span);
    }

    /// <summary><c>pool.take(v)</c> (§19.6): a <c>PoolBox&lt;T&gt;</c> carrying a shared loan of the pool.</summary>
    private HirExpr CheckPoolTake(CallExpr call, MemberExpr member, HirExpr receiver, NovaType pool)
    {
        if (Name(member.Member.Name) != "take" || call.Arguments.Length != 1)
        {
            Error(Name(member.Member.Name) != "take" ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, member.Member.Span,
                $"'{pool}' has one method: 'take(v)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var reference = RegionReceiver(receiver, pool, isMutable: false, member);
        if (reference is null)
        {
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var value = CheckExpr(call.Arguments[0], pool.Element!);
        var result = ResultOf(_types.PoolBoxOf(pool.Element!), Used(_allocError).Type);
        return new HirBuiltinCall(BuiltinFunction.PoolTake, [reference, value], result, call.Span);
    }

    /// <summary>R2 (ownership.md): region memory holds no type with drop and nothing scoped (N0264).</summary>
    private bool CheckRegionContent(NovaType type, bool needCopy, TextSpan span)
    {
        if (type.IsBottom)
        {
            return false;
        }
        var problem = type.Kind is TypeKind.Void or TypeKind.Never ? "it has no value"
            : TypeTable.IsScoped(type) && needCopy ? "it is scoped, and arrays cannot hold scoped elements (type-system.md §25.4)"
            : type.IsReference ? "it is a reference, and region memory holds values, not bare references (type-system.md §25.4)"
            : TypeTable.HoldsRegionValue(type) ? "region values and pool boxes stay in locals (type-system.md §25.4)"
            : TypeTable.NeedsDrop(type) ? "it needs drop, and a region releases its memory without destroying values (R2)"
            : needCopy && !TypeTable.IsCopy(type) ? "an array allocation copies its fill value, so the type must be Copy"
            : null;
        if (problem is not null)
        {
            Error(DiagnosticCodes.NotRegionContent, span, $"a region cannot hold a '{type}': {problem}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// <c>FixedBufferAllocator.over(mut storage)</c> (§19.4) and <c>Scratch.enter()</c> (§19.5).
    /// </summary>
    private HirExpr CheckRegionConstructor(CallExpr call, MemberExpr member, Builtin owner)
    {
        var text = Name(member.Member.Name);
        if (owner == Builtin.Scratch)
        {
            if (text != "enter" || call.Arguments.Length != 0)
            {
                Error(text != "enter" ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, call.Span,
                    "a root scratch frame is made with 'Scratch.enter()'");
                CheckArgumentsLoosely(call);
                return ErrorResult(call.Span);
            }
            return new HirBuiltinCall(BuiltinFunction.ScratchEnter, [], _types.ScratchFrame, call.Span);
        }
        if (text != "over" || call.Arguments.Length != 1 || call.Arguments[0] is not MutArgExpr storageArgument)
        {
            Error(text != "over" ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, call.Span,
                "a fixed-buffer allocator is made with 'FixedBufferAllocator.over(mut storage)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var storage = Synth(storageArgument.Operand, null);
        if (storage.Type.IsBottom)
        {
            return ErrorResult(call.Span);
        }
        var bytes = _types.SliceOf(_types.U8, isMutable: true);
        HirExpr? slice = null;
        if (ReferenceEquals(storage.Type, bytes))
        {
            slice = Coerce(storage, bytes, storageArgument.Span); // a reborrow of the caller's slice
        }
        else if (AutoDeref(storage) is { Type: { Kind: TypeKind.Array, IsUnsized: false } array } && ReferenceEquals(array.Element, _types.U8))
        {
            var deref = AutoDeref(storage);
            if (ToPlace(deref) is not { } place)
            {
                Error(DiagnosticCodes.BorrowOfNonPlace, storageArgument.Span, "the storage must be a variable or field; bind the value to a local first");
                return ErrorResult(call.Span);
            }
            if (MutabilityError(place) is { } reason)
            {
                Error(DiagnosticCodes.BorrowMutOfImmutable, storageArgument.Span, $"cannot borrow {DescribePlace(place)} as mutable: {reason}");
                return ErrorResult(call.Span);
            }
            slice = new HirSlice(true, place, null, null, bytes, storageArgument.Span);
        }
        else
        {
            Error(DiagnosticCodes.TypeMismatch, storageArgument.Operand.Span,
                $"the storage of a fixed-buffer allocator is a '[u8; N]' or a 'SliceMut<u8>'; found '{storage.Type}'");
            return ErrorResult(call.Span);
        }
        return new HirBuiltinCall(BuiltinFunction.FixedBufferOver, [slice], _types.FixedBufferAllocator, call.Span);
    }

    /// <summary>
    /// Region methods (type-system.md §19.2, §19.4, §19.5): <c>alloc(v)</c> and <c>allocArray(n, fill)</c> on
    /// every region, <c>reset()</c> on an arena, <c>enter()</c> on a scratch frame.
    /// </summary>
    private HirExpr CheckArenaMethod(CallExpr call, MemberExpr member, HirExpr receiver)
    {
        var regionType = (receiver.Type.Pointee ?? receiver.Type) is { Kind: TypeKind.Region } region ? region : _types.Arena;
        var text = Name(member.Member.Name);
        var special = regionType.Region switch { RegionKind.Arena => "reset", RegionKind.ScratchFrame => "enter", _ => null };
        var arity = text switch { "alloc" => 1, "allocArray" => 2, _ when text == special => 0, _ => -1 };
        if (arity < 0 || call.Arguments.Length != arity)
        {
            Error(arity < 0 ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, member.Member.Span, arity < 0
                ? $"'{regionType}' has no method '{text}'; it has 'alloc(v)', 'allocArray(n, fill)'{(special is null ? "" : $" and '{special}()'")}"
                : $"'{regionType}.{text}' takes {arity} argument(s)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        HirExpr? arena;
        HirExpr? allocationRight = null;
        if (regionType.Region == RegionKind.ScratchFrame)
        {
            // Allocations borrow the frame's memory; allocating and nesting use its allocation right (§19.5).
            if (ToPlace(AutoDeref(receiver)) is not { } frame)
            {
                Error(DiagnosticCodes.BorrowOfNonPlace, member.Target.Span, "a scratch frame must be bound to a local first: 'using frame = …'");
                CheckArgumentsLoosely(call);
                return ErrorResult(call.Span);
            }
            var right = Project(frame, new Projection(_frameAllocation));
            if (text == "enter")
            {
                if (MutabilityError(right) is { } reason)
                {
                    Error(DiagnosticCodes.BorrowMutOfImmutable, member.Target.Span, $"cannot nest in {DescribePlace(frame)}: {reason}");
                    CheckArgumentsLoosely(call);
                    return ErrorResult(call.Span);
                }
                arena = new HirBorrow(true, right, _types.ReferenceTo(regionType, isMutable: true), member.Target.Span);
            }
            else
            {
                arena = new HirBorrow(false, Project(frame, new Projection(_frameMemory)), _types.ReferenceTo(regionType, isMutable: false), member.Target.Span);
                allocationRight = new HirBorrow(false, right, _types.ReferenceTo(regionType, isMutable: false), member.Target.Span);
            }
        }
        else
        {
            arena = RegionReceiver(receiver, regionType, isMutable: text == special, member);
        }
        if (arena is null)
        {
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (text == "reset")
        {
            return new HirBuiltinCall(BuiltinFunction.ArenaReset, [arena], _types.Void, call.Span);
        }
        if (text == "enter")
        {
            return new HirBuiltinCall(BuiltinFunction.ScratchNest, [arena], _types.ScratchFrame, call.Span);
        }
        var count = text == "allocArray" ? CheckExpr(call.Arguments[0], _types.USize) : null;
        var valueSyntax = call.Arguments[^1];
        var value = Synth(valueSyntax, null);
        value = Coerce(value, value.Type, valueSyntax.Span);
        if (!CheckRegionContent(value.Type, needCopy: count is not null, valueSyntax.Span))
        {
            return ErrorResult(call.Span);
        }
        var allocError = Used(_allocError).Type;
        // A frame's allocation right comes last: the result borrows only from the first argument.
        ImmutableArray<HirExpr> extra = allocationRight is null ? [] : [allocationRight];
        return count is null
            ? new HirBuiltinCall(BuiltinFunction.ArenaAlloc, [arena, value, .. extra],
                ResultOf(_types.ReferenceTo(value.Type, isMutable: true), allocError), call.Span)
            : new HirBuiltinCall(BuiltinFunction.ArenaAllocArray, [arena, count, value, .. extra],
                ResultOf(_types.SliceOf(value.Type, isMutable: true), allocError), call.Span);
    }

    private NovaType OptionOf(NovaType value) =>
        _types.OptionOf(value, _symbols.Intern("null"), _symbols.Intern("Some"), _symbols.Intern("_0"));

    /// <summary><c>null</c>: needs an expected optional type (§22.1, N0260).</summary>
    private HirExpr CheckNull(NullLiteralExpr literal, NovaType? expected)
    {
        if (expected?.Enum?.OptionType is not null)
        {
            return new HirEnumValue(expected.Enum.Variants[0], [], expected, literal.Span);
        }
        if (expected is null || !expected.IsBottom)
        {
            Error(DiagnosticCodes.ResultTypeUnknown, literal.Span, expected is null
                ? "the optional type of 'null' is unknown here; give the variable or parameter a type 'T?'"
                : $"'null' is an absent optional, but '{expected}' is expected here");
        }
        return ErrorResult(literal.Span);
    }

    /// <summary><c>Some(e)</c> (§22.1): against the expected <c>T?</c>, or of <c>e</c>'s own type.</summary>
    private HirExpr CheckSomeValue(CallExpr call, NovaType? expected)
    {
        if (call.Arguments.Length != 1)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, "'Some' takes one value: 'Some(v)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (expected?.Enum?.OptionType is { } wrapped)
        {
            var payload = CheckExpr(call.Arguments[0], wrapped);
            return payload.Type.IsBottom ? payload : new HirEnumValue(expected.Enum.Variants[1], [payload], expected, call.Span);
        }
        var value = Synth(call.Arguments[0], null);
        value = Coerce(value, value.Type, call.Arguments[0].Span); // a `borrow mut` variable is reborrowed
        if (value.Type.IsBottom)
        {
            return value;
        }
        if (value.Type.Kind == TypeKind.Void)
        {
            Error(DiagnosticCodes.VoidNotAllowed, call.Arguments[0].Span, "'Some' needs a value; 'void' has none");
            return ErrorResult(call.Span);
        }
        var option = OptionOf(value.Type);
        return new HirEnumValue(option.Enum!.Variants[1], [value], option, call.Span);
    }

    /// <summary>
    /// <c>o == null</c> / <c>o != null</c> (§22.1): reads the tag through a shared borrow, so the optional
    /// is not consumed.
    /// </summary>
    private HirExpr SynthNullTest(BinaryExpr binary)
    {
        var isNull = binary.Op == BinaryOp.Equal;
        var operandSyntax = binary.Left is NullLiteralExpr ? binary.Right : binary.Left;
        if (operandSyntax is NullLiteralExpr)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, binary.Span, "comparing 'null' with 'null' is always true; compare an optional value with 'null'");
            return ErrorResult(binary.Span);
        }
        var value = Synth(operandSyntax, null);
        while (value.Type.IsReference && value.Type.Pointee!.IsReference)
        {
            value = new HirDeref(value, operandSyntax.Span);
        }
        if (value.Type.IsBottom)
        {
            return ErrorResult(binary.Span);
        }
        var option = value.Type.IsReference ? value.Type.Pointee! : value.Type;
        if (option.Enum?.OptionType is null)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, binary.Span,
                $"only an optional value can be compared with 'null', found '{value.Type}'");
            return ErrorResult(binary.Span);
        }
        var reference = _types.ReferenceTo(option, isMutable: false);
        HirExpr borrowed = value.Type.IsReference
            ? new HirReborrow(false, value, reference, operandSyntax.Span)
            : ToPlace(value) is { } place
                ? new HirBorrow(false, place, reference, operandSyntax.Span)
                : new HirBorrowTemp(false, value, reference, operandSyntax.Span);
        return new HirIsNull(borrowed, isNull, _types.Bool, binary.Span);
    }

    private NovaType ResultOf(NovaType ok, NovaType err) =>
        _types.ResultOf(ok, err, _symbols.Intern("Ok"), _symbols.Intern("Err"), _symbols.Intern("_0"));

    /// <summary>The variant of a built-in <c>Result</c> named by <paramref name="name"/> ("Ok" or "Err"), if it is one.</summary>
    private static EnumVariant? ResultVariant(NovaType result, Symbol name) =>
        result.Enum is { ResultTypes: not null } definition ? definition.FindVariant(name) : null;

    /// <summary>
    /// <c>Ok(e)</c>, <c>Ok()</c> or <c>Err(e)</c> against the expected <c>Result</c> type (type-system.md §17.2).
    /// </summary>
    private HirExpr CheckResultValue(CallExpr call, NameExpr name, NovaType? expected)
    {
        if (expected?.Enum is not { ResultTypes: not null } || ResultVariant(expected, name.Name.Name) is not { } variant)
        {
            if (expected is null || !expected.IsBottom)
            {
                Error(DiagnosticCodes.ResultTypeUnknown, call.Span, expected is null
                    ? $"the 'Result' type of '{Name(name.Name.Name)}(...)' is unknown here; give the variable or parameter a 'Result<T, E>' type"
                    : $"'{Name(name.Name.Name)}(...)' makes a 'Result', but '{expected}' is expected here");
            }
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (call.Arguments.Length != variant.Payload.Count)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, variant.Payload.Count == 0
                ? $"'{Name(variant.Name)}' of '{expected}' takes no value: write '{Name(variant.Name)}()'"
                : $"'{Name(variant.Name)}' of '{expected}' takes one value");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var payload = call.Arguments.Length == 0 ? [] : ImmutableArray.Create(CheckExpr(call.Arguments[0], variant.Payload[0].Type));
        return new HirEnumValue(variant, payload, expected, call.Span);
    }

    /// <summary>The operand of <c>e?</c> or <c>e!</c>: a <c>Result</c> or optional value (N0262).</summary>
    private HirExpr? CheckResultOperand(Expr operand, string text)
    {
        var value = Synth(operand, null);
        if (value.Type.IsBottom)
        {
            return null;
        }
        if (value.Type.Enum is not ({ ResultTypes: not null } or { OptionType: not null }))
        {
            Error(DiagnosticCodes.NotAResult, operand.Span, value.Type.Pointee?.Enum is { ResultTypes: not null } or { OptionType: not null }
                ? $"'{text}' needs a 'Result' or optional value, but this is a reference to one; '{text}' consumes the value"
                : $"'{text}' needs a 'Result<T, E>' or optional value, found '{value.Type}'");
            return null;
        }
        return value;
    }

    private HirExpr CheckTry(TryExpr expr)
    {
        var operand = CheckResultOperand(expr.Operand, "?");
        if (operand is null)
        {
            return ErrorResult(expr.Span);
        }
        var returnType = _function?.ReturnType;
        if (operand.Type.Enum!.OptionType is { } value)
        {
            if (_deferDepth > 0)
            {
                Error(DiagnosticCodes.JumpInDefer, expr.Span, "a deferred statement cannot contain '?': it would return from the function");
            }
            else if (returnType?.Enum?.OptionType is null && (returnType is null || !returnType.IsBottom))
            {
                Error(DiagnosticCodes.TryReturnMismatch, expr.Span,
                    $"'?' on an optional returns 'null' from the function, so '{(_function is null ? "this" : Name(_function.Name))}' must return an optional 'T?'");
            }
            return new HirTry(operand, value, expr.Span);
        }
        var (ok, err) = operand.Type.Enum!.ResultTypes!.Value;
        if (_deferDepth > 0)
        {
            Error(DiagnosticCodes.JumpInDefer, expr.Span, "a deferred statement cannot contain '?': it would return from the function");
        }
        else if (returnType?.Enum is not { ResultTypes: var (_, returnErr) } || !ReferenceEquals(returnErr, err))
        {
            if (returnType is null || !returnType.IsBottom)
            {
                Error(DiagnosticCodes.TryReturnMismatch, expr.Span, returnType?.Enum is { ResultTypes: var (_, other) }
                    ? $"'?' would return 'Err' of type '{err}', but '{Name(_function!.Name)}' returns errors of type '{other}' (the types must be the same in v0)"
                    : $"'?' returns 'Err' from the function, so '{(_function is null ? "this" : Name(_function.Name))}' must return 'Result<_, {err}>'");
            }
        }
        return new HirTry(operand, ok, expr.Span);
    }

    /// <summary><c>Pair&lt;i32&gt;</c> for a user generic struct or enum (§20.2): resolve the arguments, instantiate.</summary>
    private NovaType ResolveGenericItemType(GenericType syntax)
    {
        var item = _items[syntax.Name.Parts[0].Name];
        var parameters = item is StructEntry s ? s.Decl.TypeParameters : ((EnumEntry)item).Decl.TypeParameters;
        var regions = item is StructEntry r ? r.Decl.RegionParameters : ((EnumEntry)item).Decl.RegionParameters;
        var text = Name(syntax.Name.Parts[0].Name);
        var typeArguments = syntax.Arguments;
        if (!regions.IsEmpty && syntax.Arguments.Length == regions.Length + parameters.Length)
        {
            // Region arguments come first and are erased (§25.2, §25.5).
            foreach (var region in syntax.Arguments[..regions.Length])
            {
                CheckRegionArgument(region);
            }
            typeArguments = syntax.Arguments[regions.Length..];
        }
        else if (regions.IsEmpty && syntax.Arguments.Any(IsRegionName))
        {
            Error(DiagnosticCodes.UnknownRegion, syntax.Span, $"'{text}' has no region parameters");
            return _types.Error;
        }
        if (typeArguments.IsEmpty && parameters.IsEmpty)
        {
            return item is StructEntry plainStruct ? Used(plainStruct).Type : Used((EnumEntry)item).Type;
        }
        syntax = syntax with { Arguments = typeArguments };
        if (parameters.Length != syntax.Arguments.Length)
        {
            Error(DiagnosticCodes.UnknownType, syntax.Span, parameters.IsEmpty
                ? $"'{text}' is not generic; write '{text}' without type arguments"
                : $"'{text}' takes {parameters.Length} type argument(s), but {syntax.Arguments.Length} were given");
            return _types.Error;
        }
        var arguments = ImmutableArray.CreateBuilder<NovaType>(parameters.Length);
        foreach (var argument in syntax.Arguments)
        {
            var type = ResolveType(argument, allowVoid: false);
            if (type.IsBottom)
            {
                return _types.Error;
            }
            if (TypeTable.HoldsRegionValue(type))
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, argument.Span,
                    $"'{type}' cannot be a type argument of a user type: region values and pool boxes stay in locals (type-system.md §25.4)");
                return _types.Error;
            }
            arguments.Add(type); // scoped arguments make a scoped instance (§25.3)
        }
        return item is StructEntry structEntry
            ? Used(InstantiateStruct(structEntry, arguments.MoveToImmutable(), syntax.Span)).Type
            : Used(InstantiateEnum((EnumEntry)item, arguments.MoveToImmutable(), syntax.Span)).Type;
    }

    /// <summary>
    /// Matches a declared type that mentions <paramref name="parameters"/> against an actual type, recording
    /// what each parameter must be (§20.2). Mismatches are not reported here: the real check follows.
    /// </summary>
    private static void Unify(NovaType declared, NovaType actual, IReadOnlySet<NovaType> parameters, Dictionary<NovaType, NovaType> found)
    {
        if (actual.IsBottom)
        {
            return;
        }
        if (parameters.Contains(declared))
        {
            found.TryAdd(declared, actual);
            return;
        }
        if (declared.IsReference)
        {
            // An argument for a `borrow T` parameter is borrowed implicitly.
            Unify(declared.Pointee!, actual.IsReference ? actual.Pointee! : actual, parameters, found);
            return;
        }
        if (actual.IsBox && !declared.IsBox)
        {
            Unify(declared, actual.Pointee!, parameters, found); // a box converts to its contents (§18.2)
            return;
        }
        if (declared.Kind != actual.Kind)
        {
            return;
        }
        switch (declared.Kind)
        {
            case TypeKind.Struct when declared.Struct!.Syntax == actual.Struct!.Syntax:
                foreach (var (d, a) in declared.Struct.TypeArguments.Zip(actual.Struct.TypeArguments))
                {
                    Unify(d, a, parameters, found);
                }
                break;
            case TypeKind.Enum when declared.Enum!.ResultTypes is var (ok, err) && actual.Enum!.ResultTypes is var (actualOk, actualErr):
                Unify(ok, actualOk, parameters, found);
                Unify(err, actualErr, parameters, found);
                break;
            case TypeKind.Enum when declared.Enum!.OptionType is { } value && actual.Enum!.OptionType is { } actualValue:
                Unify(value, actualValue, parameters, found);
                break;
            case TypeKind.Vector when declared.IsString == actual.IsString:
            case TypeKind.StableVector:
                Unify(declared.Pointee!.Element!, actual.Pointee!.Element!, parameters, found);
                break;
            case TypeKind.SlotMap or TypeKind.Handle:
                Unify(declared.Element!, actual.Element!, parameters, found);
                break;
            case TypeKind.Enum when declared.Enum!.Syntax is not null && declared.Enum.Syntax == actual.Enum!.Syntax:
                foreach (var (d, a) in declared.Enum.TypeArguments.Zip(actual.Enum.TypeArguments))
                {
                    Unify(d, a, parameters, found);
                }
                break;
            case TypeKind.Array or TypeKind.Box:
                Unify(declared.Element ?? declared.Pointee!, actual.Element ?? actual.Pointee!, parameters, found);
                break;
        }
    }

    /// <summary>Runs <paramref name="trial"/> without reporting its diagnostics (for inferring type arguments).</summary>
    /// <summary>
    /// Checks an instance quietly (§20.3). If that fails for an instance with a scoped type argument, the generic
    /// needs an unscoped type there (it boxes `T`, …), which is reported where the instance is written (N0274).
    /// </summary>
    private T CheckInstance<T>(Func<T> trial, ImmutableArray<NovaType> arguments, string generic, SourceText source, TextSpan span)
    {
        var saved = _diagnostics;
        _diagnostics = new DiagnosticBag();
        try
        {
            return trial();
        }
        finally
        {
            var failed = _diagnostics.HasErrors;
            _diagnostics = saved;
            if (failed && arguments.FirstOrDefault(TypeTable.IsScoped) is { } scoped && _scopedInstanceReports.Add((source, span)))
            {
                _diagnostics.Error(DiagnosticCodes.ScopedTypeArgument, source, span,
                    $"'{generic}' cannot be used with the scoped type '{scoped}': it needs an unscoped type there (for example, it puts the value in a 'Box' or an array; type-system.md §25.4)");
            }
        }
    }

    private readonly HashSet<(SourceText, TextSpan)> _scopedInstanceReports = [];

    private T Quietly<T>(Func<T> trial)
    {
        var saved = _diagnostics;
        _diagnostics = new DiagnosticBag();
        try
        {
            return trial();
        }
        finally
        {
            _diagnostics = saved;
        }
    }

    /// <summary>
    /// Type arguments for <paramref name="parameters"/>: from the explicit list, else from the expected
    /// instance, else inferred from <paramref name="pairs"/> (declared type, expression). Default after N0265.
    /// </summary>
    private ImmutableArray<NovaType> TypeArgumentsFor(string what, ImmutableArray<NovaType> parameters,
        ImmutableArray<TypeSyntax> explicitArguments, ImmutableArray<NovaType>? expectedArguments,
        IEnumerable<(NovaType Declared, Expr Value)> pairs, TextSpan span)
    {
        if (!explicitArguments.IsEmpty)
        {
            if (explicitArguments.Length != parameters.Length)
            {
                Error(DiagnosticCodes.ArgumentCount, span, $"'{what}' takes {parameters.Length} type argument(s), but {explicitArguments.Length} were given");
                return default;
            }
            var resolved = explicitArguments.Select(a => ResolveType(a, allowVoid: false)).ToImmutableArray();
            return resolved.Any(t => t.IsBottom) ? default : resolved;
        }
        if (expectedArguments is { } fromExpected)
        {
            return fromExpected;
        }
        var parameterSet = parameters.ToHashSet();
        var found = new Dictionary<NovaType, NovaType>();
        foreach (var (declared, value) in pairs)
        {
            var actual = Quietly(() => Synth(value is MutArgExpr m ? m.Operand : value, null));
            Unify(declared, actual.Type, parameterSet, found);
        }
        var missing = parameters.Where(p => !found.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            Error(DiagnosticCodes.CannotInferTypeArguments, span,
                $"cannot infer {string.Join(", ", missing.Select(p => $"'{p}'"))} for '{what}' here; write the type arguments: '{what}<…>'");
            return default;
        }
        return [.. parameters.Select(p => found[p])];
    }

    /// <summary>Lengths fit in 48 bits, like every size in memory-model.md §7.</summary>
    private const long MaxArrayLength = (1L << 48) - 1;

    /// <summary><c>[element; length]</c> after checking the element type, or the error type.</summary>
    private NovaType ArrayType(NovaType element, long length, TextSpan elementSpan) =>
        CheckElement(element, length, elementSpan) ? _types.ArrayOf(element, length) : _types.Error;

    /// <summary>
    /// Can <paramref name="element"/> be an array or slice element (type-system.md §16.1)? References
    /// never; non-Copy types are reported once Copy-ness is known (N0259).
    /// </summary>
    private bool CheckElement(NovaType element, long length, TextSpan span)
    {
        if (element.IsBottom)
        {
            return false;
        }
        if (TypeTable.IsScoped(element))
        {
            Error(DiagnosticCodes.ReferenceNotAllowed, span, "array and slice elements cannot be references in v0");
            return false;
        }
        if (_copyKnown)
        {
            CheckElementCopy(element, length, span);
        }
        else
        {
            _pendingElements.Add((element, length, span, _source));
        }
        return true;
    }

    private void CheckElementCopy(NovaType element, long length, TextSpan span)
    {
        // Arrays own their elements and v0 tracks no per-element moves; slices only view them (§21.3).
        if (length >= 0 && !TypeTable.IsCopy(element))
        {
            Error(DiagnosticCodes.InvalidArray, span,
                $"array and slice elements must be Copy types in v0; '{element}' is not Copy (it has a drop block or contains a move type)");
            return;
        }
        if (length > 0 && HasLayout(element) && TypeTable.LayoutOf(element).Size * (Int128)length > int.MaxValue)
        {
            Error(DiagnosticCodes.InvalidArray, span, $"an array of {length} '{element}' values is larger than {int.MaxValue} bytes");
        }
    }

    /// <summary>False if the type contains a recursive aggregate, which has no layout (N0227 was reported).</summary>
    private static bool HasLayout(NovaType type) => !type.IsGeneric && Flatten(type).All(inner =>
        inner.Struct is not { IsRecursive: true } && inner.Enum is not { IsRecursive: true }
        && Contained(inner).All(c => HasLayout(c.Type)));

    private void CheckMain()
    {
        if (_items.GetValueOrDefault(_symbols.Intern("main")) is GenericFunctionEntry genericMain)
        {
            _source = genericMain.Source;
            Error(DiagnosticCodes.InvalidMain, genericMain.Decl.Name.Span, "'main' cannot be generic");
            return;
        }
        if (_items.GetValueOrDefault(_symbols.Intern("main")) is not HirFunction main)
        {
            return;
        }
        if (_profile == Profile.Freestanding)
        {
            _source = main.Source;
            Error(DiagnosticCodes.HostedOnly, main.Syntax.Name.Span,
                "a freestanding package has no 'main': nothing calls it; enter it through @export functions (cosmoos/profiles.md §4.1)");
            return;
        }
        var okReturn = ReferenceEquals(main.ReturnType, _types.I32) || ReferenceEquals(main.ReturnType, _types.Void);
        if (!main.Parameters.IsEmpty || !okReturn)
        {
            _source = main.Source;
            Error(DiagnosticCodes.InvalidMain, main.Syntax.Name.Span,
                "'main' must take no parameters and return 'i32' or 'void'");
        }
        else if (main.Syntax.IsUnsafe)
        {
            _source = main.Source;
            Error(DiagnosticCodes.InvalidMain, main.Syntax.Name.Span, "'main' cannot be unsafe; put the unsafe code in an 'unsafe { ... }' block");
        }
    }

    // ---- C function pointers (type-system.md §29.5) ----

    private NovaType ResolveFunctionPointerType(FunctionPointerType syntax)
    {
        if (syntax.Abi != "C")
        {
            Error(DiagnosticCodes.InvalidAttribute, syntax.Span, $"unsupported ABI \"{syntax.Abi}\": only extern \"C\" function pointers exist (type-system.md §29.5)");
            return _types.Error;
        }
        var ok = true;
        var parameters = ImmutableArray.CreateBuilder<NovaType>(syntax.Parameters.Length);
        foreach (var parameterSyntax in syntax.Parameters)
        {
            var parameter = ResolveType(parameterSyntax, allowVoid: false);
            ok &= CheckFunctionPointerPart(parameter, parameterSyntax.Span, isResult: false);
            parameters.Add(parameter);
        }
        var result = syntax.Result is null ? _types.Void : ResolveType(syntax.Result, allowVoid: true);
        ok &= CheckFunctionPointerPart(result, syntax.Result?.Span ?? syntax.Span, isResult: true);
        return ok ? _types.FunctionPointerTo(parameters.MoveToImmutable(), result) : _types.Error;
    }

    private bool CheckFunctionPointerPart(NovaType type, TextSpan span, bool isResult)
    {
        if (type.IsBottom)
        {
            return false;
        }
        if (type.Kind == TypeKind.Enum || !IsFfiSafe(type, isResult))
        {
            Error(DiagnosticCodes.NotFfiSafe, span, type.Kind == TypeKind.Enum
                ? $"a C function pointer cannot take or return the @repr enum '{type}': a value from C could not be checked (type-system.md §29.5)"
                : $"a C function pointer cannot take or return a '{type}' (type-system.md §28.3)");
            return false;
        }
        return true;
    }

    /// <summary>The C address of an extern or exported function, where a function pointer type is expected (§29.5).</summary>
    private HirExpr FunctionAddress(NameExpr name, HirFunction function, NovaType expected)
    {
        if (!function.IsExtern && function.ExportName is null)
        {
            Error(DiagnosticCodes.NotFfiSafe, name.Span,
                $"function '{Name(function.Name)}' has no C address: only extern \"C\" and @export functions can be C function pointers (type-system.md §29.5)");
            return ErrorResult(name.Span);
        }
        var actual = _types.FunctionPointerTo([.. function.Parameters.Select(p => p.Type)], function.ReturnType);
        if (!ReferenceEquals(actual, expected))
        {
            Error(DiagnosticCodes.TypeMismatch, name.Span, $"expected '{expected}', found '{actual}'");
            return ErrorResult(name.Span);
        }
        return new HirFunctionAddress(function, expected, name.Span);
    }

    /// <summary>A call through a C function pointer: unsafe (§29.5).</summary>
    private HirExpr CheckPointerCall(CallExpr call, HirExpr callee)
    {
        var type = callee.Type;
        var unsafeOk = RequireUnsafe(call.Span, "calling through a C function pointer");
        if (call.Arguments.Length != type.ParameterTypes.Length)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, $"'{type}' takes {type.ParameterTypes.Length} argument(s) but {call.Arguments.Length} were supplied");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var arguments = ImmutableArray.CreateBuilder<HirExpr>(call.Arguments.Length + 1);
        arguments.Add(callee);
        for (var i = 0; i < call.Arguments.Length; i++)
        {
            arguments.Add(CheckArgument(call.Arguments[i], type.ParameterTypes[i]));
        }
        return !unsafeOk || arguments.Any(a => a.Type.IsBottom)
            ? ErrorResult(call.Span)
            : new HirBuiltinCall(BuiltinFunction.CallPointer, arguments.MoveToImmutable(), type.ResultType!, call.Span);
    }

    // ---- Profiles (cosmoos/profiles.md §4) ----

    /// <summary>A type that needs the hosted runtime (§4.1): a heap allocator, or anything allocating from one; I/O.</summary>
    private bool IsHostedOnly(NovaType type) =>
        type.IsBox || type.Kind is TypeKind.Vector or TypeKind.StableVector or TypeKind.SlotMap or TypeKind.Resource
        || type.Region is RegionKind.Arena or RegionKind.Pool or RegionKind.ScratchFrame
        || IsAllocatorType(type) || type == _ioError.Type;

    /// <summary>Built-in names whose every use needs the hosted runtime (§4.1).</summary>
    private static bool IsHostedOnly(Builtin builtin) => builtin is not (Builtin.Truncate or Builtin.FixedBufferAllocator
        or Builtin.Ptr or Builtin.Slice or Builtin.SliceMut or Builtin.Cpu or Builtin.Target);

    /// <summary>Reports N0293 once per place: <paramref name="what"/> is not available in the freestanding profile.</summary>
    private void HostedOnly(TextSpan span, string what)
    {
        if (_profileReports.Add((_source, span)))
        {
            Error(DiagnosticCodes.HostedOnly, span, $"{what} needs the hosted runtime and is not available with --profile freestanding (cosmoos/profiles.md §4.1)");
        }
    }

    // ---- C interoperability (type-system.md §28) ----

    private static readonly HashSet<string> s_cKeywords =
    [
        "auto", "break", "case", "char", "const", "continue", "default", "do", "double", "else", "enum", "extern", "float", "for",
        "goto", "if", "inline", "int", "long", "register", "restrict", "return", "short", "signed", "sizeof", "static", "struct",
        "switch", "typedef", "union", "unsigned", "void", "volatile", "while", "bool", "true", "false", "main", "NULL",
        "alignas", "alignof", "noreturn", "static_assert", "thread_local",
    ];

    /// <summary>A C name Nova may define or declare (§28.2): an identifier, no keyword, not reserved by C or by Nova's C (c-backend §2.1).</summary>
    private bool CheckCName(string name, TextSpan span, string what)
    {
        var valid = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        var reason = !valid ? "is not a C identifier"
            : s_cKeywords.Contains(name) ? "is a C keyword or a name C programs depend on"
            : name.StartsWith("__", StringComparison.Ordinal) || (name.Length > 1 && name[0] == '_' && char.IsAsciiLetterUpper(name[1])) ? "is reserved by the C standard"
            : name.StartsWith("nova_", StringComparison.Ordinal) || name.StartsWith("nv_", StringComparison.Ordinal) || name.StartsWith("NV_", StringComparison.Ordinal)
              || (name.Length > 2 && name.StartsWith("nv", StringComparison.Ordinal) && char.IsAsciiLetterUpper(name[2]))
                ? "is reserved for the Nova runtime and generated code (c-backend.md §2.1)"
            : null;
        if (reason is not null)
        {
            Error(DiagnosticCodes.InvalidCName, span, $"{what} '{name}' {reason} (type-system.md §28.2)");
            return false;
        }
        return true;
    }

    /// <summary>The type of an integer named in <c>@repr(…)</c>; null if the name is not one.</summary>
    private NovaType? ReprInteger(string name) => name switch
    {
        "u8" => _types.U8, "u16" => _types.U16, "u32" => _types.U32, "u64" => _types.U64,
        "i8" => _types.I8, "i16" => _types.I16, "i32" => _types.I32, "i64" => _types.I64,
        _ => null,
    };

    private void AttributeError(AttributeSyntax attribute, string message) =>
        Error(DiagnosticCodes.InvalidAttribute, attribute.Span, $"{message} (type-system.md §28.1)");

    /// <summary>Reports repeated attributes, and returns those named <paramref name="known"/>; the rest are N0290.</summary>
    private List<AttributeSyntax> KnownAttributes(ImmutableArray<AttributeSyntax> attributes, string where, params string[] known)
    {
        var seen = new HashSet<string>();
        var result = new List<AttributeSyntax>();
        foreach (var attribute in attributes)
        {
            var name = Name(attribute.Name.Name);
            if (!seen.Add(name))
            {
                AttributeError(attribute, $"'@{name}' is given twice");
            }
            else if (!known.Contains(name))
            {
                AttributeError(attribute, name is "export" or "repr" or "effects"
                    ? $"'@{name}' cannot be used on {where}"
                    : $"unknown attribute '@{name}'");
            }
            else
            {
                result.Add(attribute);
            }
        }
        return result;
    }

    /// <summary>`@repr` on structs and enums, before any layout is computed (§28.5).</summary>
    private void ProcessTypeAttributes()
    {
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items)
            {
                switch (item)
                {
                    case StructDecl s:
                        var structAttributes = KnownAttributes(s.Attributes, "a struct", "repr", "align", "noncopy");
                        foreach (var noncopy in structAttributes.Where(a => Name(a.Name.Name) == "noncopy"))
                        {
                            if (noncopy.HasParentheses)
                            {
                                AttributeError(noncopy, "'@noncopy' takes no arguments");
                            }
                            else if (_items.GetValueOrDefault(s.Name.Name) is StructEntry entry && entry.Decl == s)
                            {
                                entry.Type.Struct!.IsNonCopy = true;
                            }
                        }
                        foreach (var align in structAttributes.Where(a => Name(a.Name.Name) == "align"))
                        {
                            if (align.Arguments is not [IntLiteralExpr { Value: var n and >= 1 and <= 4096 }] || (n & (n - 1)) != 0)
                            {
                                AttributeError(align, "'@align(n)' takes a power of two from 1 to 4096");
                            }
                            else if (!s.TypeParameters.IsEmpty || !s.RegionParameters.IsEmpty)
                            {
                                AttributeError(align, "a generic struct cannot have '@align'");
                            }
                            else if (_items.GetValueOrDefault(s.Name.Name) is StructEntry entry && entry.Decl == s)
                            {
                                entry.Type.Struct!.ExplicitAlign = (int)n;
                            }
                        }
                        foreach (var repr in s.Attributes.Where(a => Name(a.Name.Name) == "repr"))
                        {
                            if (repr.Arguments is not [NameExpr { Name: var c }] || Name(c.Name) != "C")
                            {
                                AttributeError(repr, "a struct takes '@repr(C)'");
                            }
                            else if (!s.TypeParameters.IsEmpty || !s.RegionParameters.IsEmpty)
                            {
                                AttributeError(repr, "a generic struct cannot be '@repr(C)'");
                            }
                            else if (_items.GetValueOrDefault(s.Name.Name) is StructEntry entry && entry.Decl == s)
                            {
                                entry.Type.Struct!.IsReprC = true;
                            }
                        }
                        break;
                    case EnumDecl e:
                        foreach (var repr in KnownAttributes(e.Attributes, "an enum", "repr"))
                        {
                            var integer = repr.Arguments is [NameExpr { Name: var n }] ? ReprInteger(Name(n.Name)) : null;
                            if (integer is null)
                            {
                                AttributeError(repr, "an enum takes '@repr(u8)', '@repr(u16)', '@repr(u32)', '@repr(u64)' or a signed 'i' form");
                            }
                            else if (!e.TypeParameters.IsEmpty || !e.RegionParameters.IsEmpty)
                            {
                                AttributeError(repr, "a generic enum cannot have '@repr'");
                            }
                            else if (e.Variants.Any(v => !v.Payload.IsEmpty))
                            {
                                AttributeError(repr, "only an enum without payloads can have '@repr'");
                            }
                            else if (!integer.Fits(e.Variants.Length - 1))
                            {
                                AttributeError(repr, $"'{integer}' cannot hold the index of each of the {e.Variants.Length} variants");
                            }
                            else if (_items.GetValueOrDefault(e.Name.Name) is EnumEntry entry && entry.Decl == e)
                            {
                                entry.Type.Enum!.Repr = integer;
                            }
                        }
                        break;
                    case FunctionDecl or ExternBlock:
                        break; // after they are declared
                    default:
                        KnownAttributes(item.Attributes, "this declaration");
                        break;
                }
            }
        }
    }

    /// <summary>`extern "C" { … }`: each function is declared like a top-level one, without a body (§28.2).</summary>
    private void DeclareExternBlock(ExternBlock block)
    {
        KnownAttributes(block.Attributes, "an 'extern' block");
        if (block.Abi != "C")
        {
            Error(DiagnosticCodes.InvalidAttribute, block.AbiSpan, $"unsupported ABI \"{block.Abi}\": only extern \"C\" exists (type-system.md §28.2)");
            return;
        }
        foreach (var fn in block.Functions)
        {
            KnownAttributes(fn.Attributes, "a C function", "effects");
            var problem = fn.Body is not null || fn.ExpressionBody is not null ? "cannot have a body; it is defined in C"
                : fn.Receiver is not null ? "cannot have a 'this' parameter"
                : !fn.TypeParameters.IsEmpty || !fn.RegionParameters.IsEmpty ? "cannot be generic"
                : fn.IsUnsafe ? "is unsafe to call already; leave out 'unsafe'"
                : null;
            if (problem is not null)
            {
                Error(DiagnosticCodes.InvalidAttribute, fn.Name.Span, $"C function '{Name(fn.Name.Name)}' {problem} (type-system.md §28.2)");
                continue;
            }
            Declare(fn);
        }
    }

    /// <summary>`@export` and `@effects` on functions (§28.1, §28.4).</summary>
    private void ProcessFunctionAttributes()
    {
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var fn in unit.Items.OfType<FunctionDecl>())
            {
                foreach (var export in KnownAttributes(fn.Attributes, "a function", "export", "effects").Where(a => Name(a.Name.Name) == "export"))
                {
                    var symbol = export.Arguments switch
                    {
                        [] => Name(fn.Name.Name),
                        [StringLiteralExpr text] => text.Value,
                        _ => null,
                    };
                    if (symbol is null)
                    {
                        AttributeError(export, "'@export' takes nothing or the C symbol as a string: '@export(\"name\")'");
                    }
                    else if (Name(fn.Name.Name) == "main")
                    {
                        AttributeError(export, "'main' cannot be exported; the generated C 'main' calls it");
                    }
                    else if (_items.GetValueOrDefault(fn.Name.Name) is GenericFunctionEntry)
                    {
                        AttributeError(export, "a generic function cannot be exported");
                    }
                    else if (_items.GetValueOrDefault(fn.Name.Name) is HirFunction function && function.Syntax == fn)
                    {
                        function.ExportName = symbol;
                    }
                }
            }
            foreach (var s in unit.Items.OfType<StructDecl>())
            {
                foreach (var member in s.Functions)
                {
                    KnownAttributes(member.Attributes, "a method");
                }
            }
        }
    }

    /// <summary>Is this struct (or the generic struct it instantiates) declared `@noncopy`?</summary>
    private bool IsNonCopy(StructDefinition definition) =>
        definition.IsNonCopy || (_items.GetValueOrDefault(definition.Syntax.Name.Name) is StructEntry template
            && template.Decl == definition.Syntax && template.Type.Struct!.IsNonCopy);

    /// <summary>`Volatile<T>` from lib/core (§29.2): its one field has `T`'s layout, so it is C-compatible where `T` is.</summary>
    private static bool IsVolatileCell(NovaType type) =>
        type.Struct is { TypeArguments.Length: 1 } definition && CoreLibrary.Contains(definition.Source)
        && type.Name.StartsWith("Volatile<", StringComparison.Ordinal);

    /// <summary>Can a value of this type cross the C boundary? (§28.3) A `@repr` enum only by value, not inside a struct.</summary>
    private static bool IsFfiSafe(NovaType type, bool isResult, bool inStruct = false) => type.Kind switch
    {
        TypeKind.Integer or TypeKind.Bool or TypeKind.Error or TypeKind.FunctionPointer => true,
        TypeKind.RawPointer => IsCPointee(type.Pointee!),
        TypeKind.Void => isResult,
        TypeKind.Struct when inStruct && IsVolatileCell(type) => IsFfiSafe(type.Struct!.Fields[0].Type, isResult: false, inStruct: true),
        TypeKind.Struct => type.Struct!.IsReprC,
        TypeKind.Enum => type.Enum!.Repr is not null && !inStruct,
        _ => false,
    };

    /// <summary>A pointee C can name: an integer, bool, void, CChar, a @repr(C) struct, or such a pointer (§28.3).</summary>
    private static bool IsCPointee(NovaType type) => type.Kind switch
    {
        TypeKind.Integer or TypeKind.Bool or TypeKind.Void or TypeKind.CChar or TypeKind.Error => true,
        TypeKind.Struct => type.Struct!.IsReprC,
        TypeKind.RawPointer => IsCPointee(type.Pointee!),
        _ => false,
    };

    private void CheckSignatureIsFfiSafe(HirFunction function, string what)
    {
        var syntax = function.Syntax;
        for (var i = 0; i < syntax.Parameters.Length; i++)
        {
            var type = function.Parameters[i].Type;
            if (!IsFfiSafe(type, isResult: false))
            {
                Error(DiagnosticCodes.NotFfiSafe, syntax.Parameters[i].Type.Span,
                    $"{what} cannot take a '{type}': only integers, bool, raw pointers, @repr(C) structs and @repr enums cross to C (type-system.md §28.3)");
            }
        }
        if (!IsFfiSafe(function.ReturnType, isResult: true))
        {
            Error(DiagnosticCodes.NotFfiSafe, syntax.ReturnType?.Span ?? syntax.Name.Span,
                $"{what} cannot return a '{function.ReturnType}': only integers, bool, raw pointers, @repr(C) structs, @repr enums and void cross to C (type-system.md §28.3)");
        }
    }

    /// <summary>Once every type is resolved: FFI-safe signatures and fields, and valid, unique C names (§28.2–§28.5).</summary>
    private void CheckForeignInterfaces()
    {
        var cNames = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        void Claim(string name, TextSpan span, string what)
        {
            if (!cNames.TryAdd(name, span))
            {
                Error(DiagnosticCodes.DuplicateItem, span, $"the C name '{name}' of {what} is already used by another C function or export (type-system.md §28.2)");
            }
        }
        foreach (var entry in _structs.Where(s => s.Type.Struct!.IsReprC))
        {
            _source = entry.Source;
            CheckCName(Name(entry.Decl.Name.Name), entry.Decl.Name.Span, "the C struct name");
            foreach (var (field, syntax) in entry.Type.Struct!.Fields.Zip(entry.Decl.Fields))
            {
                field.HasNovaCName = true;
                CheckCName(Name(field.Name), syntax.Name.Span, "the C member name");
                if (!IsFfiSafe(field.Type, isResult: false, inStruct: true))
                {
                    Error(DiagnosticCodes.NotFfiSafe, syntax.Type.Span,
                        $"a @repr(C) struct cannot hold a '{field.Type}': only integers, bool, raw pointers to C types and @repr(C) structs have C layouts (type-system.md §28.5)");
                }
            }
        }
        foreach (var entry in _enums.Where(e => e.Type.Enum!.Repr is not null))
        {
            _source = entry.Source;
            CheckCName(Name(entry.Decl.Name.Name), entry.Decl.Name.Span, "the C type name");
        }
        foreach (var function in _functions.Where(f => f.IsExtern || f.ExportName is not null))
        {
            _source = function.Source;
            var name = function.ExportName ?? Name(function.Name);
            var what = function.IsExtern ? $"C function '{name}'" : $"exported function '{Name(function.Name)}'";
            var span = function.IsExtern ? function.Syntax.Name.Span : function.Syntax.Attributes.First(a => Name(a.Name.Name) == "export").Span;
            // `_` and a lowercase letter is allowed: platforms' entry symbols (`_start`, §29.6) and C library names (`_write`).
            if (CheckCName(name, span, function.IsExtern ? "the C function name" : "the exported symbol"))
            {
                Claim(name, span, what);
            }
            CheckSignatureIsFfiSafe(function, what);
        }
    }

    // ---- Constants ----

    private HirExpr EvaluateConstant(ConstEntry entry)
    {
        switch (entry.State)
        {
            case ConstState.Done:
                return entry.Hir.Value!;
            case ConstState.InProgress:
            {
                var saved = _source;
                _source = entry.Source;
                Error(DiagnosticCodes.CyclicConstant, entry.Decl.Name.Span,
                    $"constant '{Name(entry.Hir.Name)}' depends on itself");
                _source = saved;
                return ErrorResult(entry.Decl.Name.Span);
            }
        }

        entry.State = ConstState.InProgress;
        var (savedSource, savedFunction, savedScopes, savedLoopDepth) = (_source, _function, _scopes, _loopDepth);
        (_source, _function, _scopes, _loopDepth) = (entry.Source, null, [], 0);

        var checkedValue = CheckExpr(entry.Decl.Value, entry.Hir.Type);
        // A constant whose type was rejected is not folded: that would only add a cascading error.
        var value = ReferenceEquals(entry.Hir.Type, _types.Error) ? ErrorResult(entry.Decl.Value.Span) : Fold(checkedValue);

        (_source, _function, _scopes, _loopDepth) = (savedSource, savedFunction, savedScopes, savedLoopDepth);
        // A cycle may already have stored an error value for this constant; keep the first result.
        entry.Hir.Value ??= value;
        entry.State = ConstState.Done;
        return entry.Hir.Value;
    }

    /// <summary>Evaluates a constant expression to a literal (type-system.md §10).</summary>
    private HirExpr Fold(HirExpr expr)
    {
        switch (expr)
        {
            case HirIntConst or HirBoolConst or HirError:
                return expr;
            case HirUnary unary:
            {
                var operand = Fold(unary.Operand);
                if (operand is HirBoolConst b)
                {
                    return new HirBoolConst(!b.Value, expr.Type, expr.Span);
                }
                if (operand is not HirIntConst i)
                {
                    return operand;
                }
                var negated = -(BigInteger)i.Value;
                if (!expr.Type.Fits(negated))
                {
                    Error(DiagnosticCodes.NotConstant, expr.Span, $"arithmetic overflow: -({i.Value}) does not fit in '{expr.Type}'");
                    return ErrorResult(expr.Span);
                }
                return new HirIntConst((Int128)negated, expr.Type, expr.Span);
            }
            case HirBinary binary:
                return FoldBinary(binary);
            case HirCast cast:
                return Fold(cast.Operand) switch
                {
                    HirIntConst i => new HirIntConst(i.Value, expr.Type, expr.Span),
                    HirBoolConst b => new HirIntConst(b.Value ? 1 : 0, expr.Type, expr.Span),
                    var other => other,
                };
            case HirTruncate truncate:
                return Fold(truncate.Operand) switch
                {
                    HirIntConst i => new HirIntConst((Int128)IntegerSemantics.Wrap(i.Value, expr.Type), expr.Type, expr.Span),
                    var other => other,
                };
            default:
                Error(DiagnosticCodes.NotConstant, expr.Span, expr is HirCall or HirBuiltinCall
                    ? "constant expressions cannot contain calls"
                    : "constant expressions can only contain literals, operators, casts and other constants");
                return ErrorResult(expr.Span);
        }
    }

    private HirExpr FoldBinary(HirBinary binary)
    {
        var left = Fold(binary.Left);
        if (left is HirError)
        {
            return left;
        }

        // Short-circuit operators evaluate the right side only when needed, as at runtime.
        if (binary.Op is BinaryOp.LogicalAnd or BinaryOp.LogicalOr && left is HirBoolConst lb)
        {
            if (lb.Value == (binary.Op == BinaryOp.LogicalOr))
            {
                return new HirBoolConst(lb.Value, binary.Type, binary.Span);
            }
            var rightValue = Fold(binary.Right);
            return rightValue is HirBoolConst rb ? new HirBoolConst(rb.Value, binary.Type, binary.Span) : rightValue;
        }

        var right = Fold(binary.Right);
        switch (left, right)
        {
            case (HirBoolConst a, HirBoolConst b):
                return new HirBoolConst(binary.Op == BinaryOp.Equal ? a.Value == b.Value : a.Value != b.Value, binary.Type, binary.Span);
            case (HirIntConst a, HirIntConst b) when binary.Type.Kind == TypeKind.Bool:
                return new HirBoolConst(IntegerSemantics.Compare(binary.Op, a.Value, b.Value), binary.Type, binary.Span);
            case (HirIntConst a, HirIntConst b):
                if (IntegerSemantics.TryEvaluate(binary.Op, a.Value, b.Value, binary.Type, out var result, out var error))
                {
                    return new HirIntConst((Int128)result, binary.Type, binary.Span);
                }
                Error(DiagnosticCodes.NotConstant, binary.Span, $"constant evaluation failed: {error}");
                return ErrorResult(binary.Span);
            default:
                return right is HirError ? right : ErrorResult(binary.Span);
        }
    }

    // ---- Function bodies ----

    private void CheckFunctionBody(HirFunction function)
    {
        _source = function.Source;
        _function = function;
        _typeEnvironment = function.Environment;
        _regions = RegionScope(function.Syntax, function.Owner);
        _loopDepth = 0;
        _unsafeDepth = function.Syntax.IsUnsafe ? 1 : 0;
        _usedUnsafe = false;
        _scopes = [[]];
        foreach (var parameter in function.Parameters)
        {
            _scopes[0].TryAdd(parameter.Name, parameter);
        }

        var syntax = function.Syntax;
        if (syntax.Body is not null)
        {
            function.Body = CheckBlock(syntax.Body);
        }
        else
        {
            var expr = syntax.ExpressionBody!;
            HirStmt statement;
            if (function.ReturnType.Kind == TypeKind.Void)
            {
                statement = new HirExprStmt(CheckExpressionStatement(expr), expr.Span);
            }
            else
            {
                statement = new HirReturn(CheckExpr(expr, function.ReturnType), expr.Span);
            }
            function.Body = new HirBlock([statement], expr.Span);
        }

        if ((syntax.IsUnsafe || _usedUnsafe) && !HasSafetyComment(syntax.Doc))
        {
            Error(DiagnosticCodes.MissingSafetyComment, syntax.Name.Span, syntax.IsUnsafe
                ? $"unsafe function '{Name(syntax.Name.Name)}' needs a '/// Safety:' doc comment stating what callers must guarantee (type-system.md §27.4)"
                : $"function '{Name(syntax.Name.Name)}' uses 'unsafe' and needs a '/// Safety:' doc comment saying why it is sound (type-system.md §27.4)");
        }
        _unsafeDepth = 0;

        FlowAnalysis.Analyze(function, _symbols, _types, _diagnostics);
        _function = null;
        _typeEnvironment = new Dictionary<Symbol, NovaType>();
        _regions = [];
    }

    /// <summary>Does a doc comment have a line starting with <c>Safety:</c>? (§27.4)</summary>
    private static bool HasSafetyComment(string? doc) =>
        doc is not null && doc.Split('\n').Any(line => line.StartsWith("Safety:", StringComparison.Ordinal));

    /// <summary>Reports N0280 unless the code being checked is in an unsafe context (U1, type-system.md §27.4).</summary>
    private bool RequireUnsafe(TextSpan span, string what)
    {
        if (_unsafeDepth > 0)
        {
            return true;
        }
        Error(DiagnosticCodes.UnsafeOutsideUnsafe, span, $"{what} is unsafe: use it inside 'unsafe {{ ... }}' or an 'unsafe fn' (type-system.md §27.3)");
        return false;
    }

    private HirBlock CheckBlock(BlockStmt block)
    {
        _scopes.Add([]);
        var statements = ImmutableArray.CreateBuilder<HirStmt>(block.Statements.Length);
        foreach (var statement in block.Statements)
        {
            statements.Add(CheckStmt(statement));
        }
        _scopes.RemoveAt(_scopes.Count - 1);
        return new HirBlock(statements.MoveToImmutable(), block.Span);
    }

    private HirStmt CheckStmt(Stmt stmt)
    {
        switch (stmt)
        {
            case BlockStmt block:
                return CheckBlock(block);
            case LocalDeclStmt local:
                return CheckLocalDecl(local);
            case AssignStmt assign:
                return CheckAssign(assign);
            case IfStmt ifStmt:
                return CheckIf(ifStmt);
            case WhileStmt whileStmt:
            {
                var condition = CheckExpr(whileStmt.Condition, _types.Bool);
                _loopDepth++;
                var body = CheckBlock(whileStmt.Body);
                _loopDepth--;
                return new HirWhile(condition, body, stmt.Span);
            }
            case LoopStmt loop:
            {
                _loopDepth++;
                var body = CheckBlock(loop.Body);
                _loopDepth--;
                return new HirLoop(body, stmt.Span);
            }
            case ForStmt forStmt:
                return CheckFor(forStmt);
            case BreakStmt:
                CheckInLoop(stmt.Span, "break");
                return new HirBreak(stmt.Span);
            case ContinueStmt:
                CheckInLoop(stmt.Span, "continue");
                return new HirContinue(stmt.Span);
            case ReturnStmt ret:
                return CheckReturn(ret);
            case ExprStmt exprStmt:
                return new HirExprStmt(CheckExpressionStatement(exprStmt.Expression), stmt.Span);
            case MatchStmt match:
                return CheckMatch(match);
            case UnsafeStmt unsafeStmt:
            {
                _usedUnsafe = true;
                _unsafeDepth++;
                var body = CheckBlock(unsafeStmt.Body);
                _unsafeDepth--;
                return body;
            }
            case DeferStmt defer:
            {
                _deferDepth++;
                var body = CheckStmt(defer.Body);
                _deferDepth--;
                if (FindJump(defer.Body) is { } jump)
                {
                    Error(DiagnosticCodes.JumpInDefer, jump.Span,
                        "a deferred statement cannot contain 'return', 'break', 'continue' or 'defer'");
                }
                return new HirDefer(body, stmt.Span);
            }
            default:
                throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
        }
    }

    /// <summary>`for` over an integer range or the elements of an array or slice (type-system.md §16.4).</summary>
    private HirFor CheckFor(ForStmt forStmt)
    {
        HirExpr start;
        HirExpr? end = null;
        NovaType variableType;
        if (forStmt.End is null)
        {
            var sequence = Synth(forStmt.Start, null);
            var vector = sequence;
            while (vector.Type.IsReference)
            {
                vector = new HirDeref(vector, vector.Span);
            }
            if (vector.Type.IsStableVector)
            {
                Error(DiagnosticCodes.TypeMismatch, forStmt.Start.Span,
                    "'for' does not iterate over a 'StableVector' (its elements are not contiguous); use 'for (i in 0..sv.length)'");
                start = ErrorResult(forStmt.Start.Span);
            }
            else if (vector.Type.IsVector && !vector.Type.IsString)
            {
                // Over a view, which keeps the vector borrowed for the whole loop (§23.1).
                var view = BorrowPlace(new HirDeref(vector, vector.Span), isMutable: false, forStmt.Start.Span);
                start = view.Type.IsBottom ? view : new HirDeref(view, view.Span);
            }
            else
            {
                start = AutoDeref(sequence);
            }
            variableType = start.Type.Kind == TypeKind.Array && !start.Type.IsStr ? start.Type.Element! : _types.Error;
            if (start.Type.IsStr)
            {
                Error(DiagnosticCodes.TypeMismatch, forStmt.Start.Span,
                    "'for' does not iterate over a 'StringView'; iterate over its bytes with 's.bytes()'");
            }
            else if (!start.Type.IsBottom && start.Type.Kind != TypeKind.Array)
            {
                Error(DiagnosticCodes.TypeMismatch, forStmt.Start.Span,
                    $"'for' iterates over an array, a slice or a range 'a..b'; found '{start.Type}'");
            }
        }
        else
        {
            // The bounds have one integer type; a flexible literal takes the other bound's type.
            if (IsFlexible(forStmt.Start) && !IsFlexible(forStmt.End))
            {
                end = AutoDeref(Synth(forStmt.End, null));
                start = CheckExpr(forStmt.Start, end.Type.IsInteger ? end.Type : null);
            }
            else
            {
                start = AutoDeref(Synth(forStmt.Start, null));
                end = CheckExpr(forStmt.End, start.Type.IsInteger ? start.Type : null);
            }
            variableType = start.Type.IsInteger ? start.Type : _types.Error;
            if (!start.Type.IsBottom && !start.Type.IsInteger)
            {
                Error(DiagnosticCodes.TypeMismatch, forStmt.Start.Span, $"a range in 'for' needs integer bounds; found '{start.Type}'");
            }
        }

        if (LookupLocal(forStmt.Variable.Name) is not null)
        {
            Error(DiagnosticCodes.LocalRedeclared, forStmt.Variable.Span,
                $"'{Name(forStmt.Variable.Name)}' is already declared in this function; locals cannot be shadowed");
        }
        _scopes.Add([]);
        var variable = _function!.AddLocal(forStmt.Variable.Name, variableType, isMutable: false, isParameter: false, forStmt.Variable.Span);
        _scopes[^1][forStmt.Variable.Name] = variable;
        _loopDepth++;
        var body = CheckBlock(forStmt.Body);
        _loopDepth--;
        _scopes.RemoveAt(_scopes.Count - 1);
        return new HirFor(variable, start, end, body, forStmt.Span);
    }

    /// <summary>A `match` statement: patterns, bindings, exhaustiveness (type-system.md §15.2).</summary>
    private HirStmt CheckMatch(MatchStmt match)
    {
        var scrutinee = Synth(match.Scrutinee, null);
        var reference = scrutinee.Type.IsReference ? scrutinee.Type : null;
        var type = reference?.Pointee ?? scrutinee.Type;
        var ok = !type.IsBottom;
        if (ok && type.Kind is not (TypeKind.Enum or TypeKind.Integer or TypeKind.Bool))
        {
            Error(DiagnosticCodes.InvalidOperandTypes, match.Scrutinee.Span, $"cannot match on a value of type '{type}'");
            ok = false;
        }

        var arms = ImmutableArray.CreateBuilder<HirArm>();
        var seenVariants = new HashSet<EnumVariant>();
        var seenValues = new HashSet<Int128>();
        var wildcard = false;
        var patternFailed = false; // an invalid pattern already reported: exhaustiveness would only add noise
        foreach (var arm in match.Arms)
        {
            if (wildcard)
            {
                Error(DiagnosticCodes.UnreachableArm, arm.Pattern.Span, "this arm can never match: an earlier '_' matches everything");
            }
            var kind = HirPatternKind.Wildcard;
            EnumVariant? variant = null;
            Int128 value = 0;
            var bindings = ImmutableArray.CreateBuilder<(HirLocal, int)>();
            _scopes.Add([]);
            switch (arm.Pattern)
            {
                case WildcardPattern:
                    wildcard = true;
                    break;
                case LiteralPattern literal when ok:
                {
                    var hir = CheckExpr(literal.Literal, type);
                    switch (hir)
                    {
                        case HirIntConst i:
                            kind = HirPatternKind.IntegerLiteral;
                            value = i.Value;
                            break;
                        case HirBoolConst b:
                            kind = HirPatternKind.Bool;
                            value = b.Value ? 1 : 0;
                            break;
                    }
                    if (hir is (HirIntConst or HirBoolConst) && !seenValues.Add(value))
                    {
                        Error(DiagnosticCodes.UnreachableArm, arm.Pattern.Span, "this value is already matched by an earlier arm");
                    }
                    break;
                }
                case NullPattern nullPattern when ok:
                    if (type.Enum?.OptionType is null)
                    {
                        Error(DiagnosticCodes.TypeMismatch, nullPattern.Span, $"a 'null' pattern needs an optional, but the value has type '{type}'");
                        patternFailed = true;
                        break;
                    }
                    kind = HirPatternKind.Variant;
                    variant = type.Enum.Variants[0];
                    if (!seenVariants.Add(variant))
                    {
                        Error(DiagnosticCodes.UnreachableArm, nullPattern.Span, "'null' is already matched by an earlier arm");
                    }
                    break;
                case VariantPattern pattern when ok:
                    kind = HirPatternKind.Variant;
                    variant = CheckVariantPattern(pattern, type, reference, bindings);
                    patternFailed |= variant is null;
                    if (variant is not null && !seenVariants.Add(variant))
                    {
                        Error(DiagnosticCodes.UnreachableArm, pattern.Span, $"variant '{Name(variant.Name)}' is already matched by an earlier arm");
                    }
                    break;
            }
            var body = CheckBlock(arm.Body);
            _scopes.RemoveAt(_scopes.Count - 1);
            arms.Add(new HirArm(kind, variant, value, bindings.ToImmutable(), body, arm.Span)
            {
                ListsPayloads = arm.Pattern is VariantPattern { Bindings.IsDefault: false },
            });
        }

        if (ok && !wildcard && !patternFailed)
        {
            var missing = type.Kind switch
            {
                TypeKind.Enum => type.Enum!.Variants.Where(v => !seenVariants.Contains(v))
                    .Select(v => type.Enum.Syntax is null ? Name(v.Name) : $"{type}.{Name(v.Name)}").ToList(),
                TypeKind.Bool => s_boolValues.Where(b => !seenValues.Contains(b ? 1 : 0)).Select(b => b ? "true" : "false").ToList(),
                _ => ["_"],
            };
            if (missing.Count > 0)
            {
                Error(DiagnosticCodes.NonExhaustiveMatch, match.Scrutinee.Span,
                    $"this match does not cover {string.Join(", ", missing)}; add the missing arms or a '_' arm");
            }
        }
        // A value scrutinee with a non-Copy payload bound by name is consumed (§18.4).
        var consumes = reference is null && arms.Any(a => a.Bindings.Any(b => !TypeTable.IsCopy(b.Local.Type)));
        return ok
            ? new HirMatch(scrutinee, arms.ToImmutable(), match.Span) { Consumes = consumes }
            : new HirExprStmt(ErrorResult(match.Span), match.Span);
    }

    private EnumVariant? CheckVariantPattern(VariantPattern pattern, NovaType type, NovaType? reference,
        ImmutableArray<(HirLocal, int)>.Builder bindings)
    {
        if (type.Kind != TypeKind.Enum)
        {
            Error(DiagnosticCodes.TypeMismatch, pattern.Span, $"a variant pattern needs an enum, but the value has type '{type}'");
            return null;
        }
        var parts = pattern.Path.Parts;
        EnumVariant variant;
        if (type.Enum!.ResultTypes is not null)
        {
            // Result patterns have no type prefix: Ok(x), Err(e) (type-system.md §17.3).
            if (parts.Length != 1 || ResultVariant(type, parts[0].Name) is not { } resultVariant)
            {
                Error(DiagnosticCodes.TypeMismatch, pattern.Path.Span, $"expected 'Ok(…)' or 'Err(…)' for a value of type '{type}'");
                return null;
            }
            variant = resultVariant;
        }
        else if (type.Enum.OptionType is not null)
        {
            // Optional patterns: Some(x) and null (§22.1).
            if (parts.Length != 1 || Name(parts[0].Name) != "Some")
            {
                Error(DiagnosticCodes.TypeMismatch, pattern.Path.Span, $"expected 'Some(…)' or 'null' for a value of type '{type}'");
                return null;
            }
            variant = type.Enum.Variants[1];
        }
        else if (parts.Length != 2 || parts[0].Name != type.Enum.Syntax!.Name.Name)
        {
            Error(DiagnosticCodes.TypeMismatch, pattern.Path.Span, $"expected a variant of '{type}', written '{type}.Variant'");
            return null;
        }
        else if (type.Enum.FindVariant(parts[1].Name) is { } found)
        {
            variant = found;
        }
        else
        {
            Error(DiagnosticCodes.UndefinedName, parts[1].Span, $"'{type}' has no variant named '{Name(parts[1].Name)}'");
            return null;
        }
        if (pattern.Bindings.IsDefault)
        {
            return variant; // `E.V` ignores the payload
        }
        if (pattern.Bindings.Length != variant.Payload.Count)
        {
            Error(DiagnosticCodes.ArgumentCount, pattern.Span,
                $"'{type}.{Name(variant.Name)}' has {variant.Payload.Count} payload value(s), but the pattern binds {pattern.Bindings.Length}");
            return variant;
        }
        for (var i = 0; i < pattern.Bindings.Length; i++)
        {
            if (pattern.Bindings[i] is not { } name)
            {
                continue;
            }
            var payloadType = variant.Payload[i].Type;
            var isMutable = pattern.IsMutable(i);
            if (isMutable && reference is not null)
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, name.Span,
                    $"'{Name(name.Name)}' is bound by reference (the match is on a reference), so it cannot be 'var'");
                isMutable = false;
            }
            if (LookupLocal(name.Name) is not null)
            {
                Error(DiagnosticCodes.LocalRedeclared, name.Span,
                    $"'{Name(name.Name)}' is already declared in this function; locals cannot be shadowed");
            }
            var bindingType = reference is null ? payloadType : _types.ReferenceTo(payloadType, reference.IsMutableReference);
            var local = _function!.AddLocal(name.Name, bindingType, isMutable, isParameter: false, name.Span);
            _scopes[^1][name.Name] = local;
            bindings.Add((local, i));
        }
        return variant;
    }

    /// <summary>The first `return`, `break`, `continue` or `defer` inside a statement (N0254).</summary>
    private static Stmt? FindJump(Stmt stmt) => stmt switch
    {
        ReturnStmt or BreakStmt or ContinueStmt or DeferStmt => stmt,
        BlockStmt block => block.Statements.Select(FindJump).FirstOrDefault(s => s is not null),
        IfStmt ifStmt => FindJump(ifStmt.Then) ?? (ifStmt.Else is null ? null : FindJump(ifStmt.Else)),
        WhileStmt w => FindJump(w.Body),
        LoopStmt l => FindJump(l.Body),
        _ => null,
    };

    private void CheckInLoop(TextSpan span, string keyword)
    {
        if (_loopDepth == 0)
        {
            Error(DiagnosticCodes.LoopControlOutsideLoop, span, $"'{keyword}' can only be used inside 'while' or 'loop'");
        }
    }

    private HirExpr CheckExpressionStatement(Expr expr)
    {
        if (expr is not (CallExpr or TryExpr { Operand: CallExpr } or UnwrapExpr { Operand: CallExpr }))
        {
            Error(DiagnosticCodes.StatementNotCall, expr.Span, "only calls can be used as statements");
            return ErrorResult(expr.Span);
        }
        return Synth(expr, null);
    }

    private HirLocalDecl CheckLocalDecl(LocalDeclStmt decl)
    {
        var declared = decl.Type is null ? null : ResolveType(decl.Type, allowVoid: false);
        if (!decl.IsMutable && decl.Initializer is null)
        {
            Error(DiagnosticCodes.LetWithoutInitializer, decl.Name.Span,
                $"'let {Name(decl.Name.Name)}' needs an initializer; use 'var' to assign later");
        }
        else if (decl.Initializer is null && declared is null)
        {
            Error(DiagnosticCodes.LocalNeedsType, decl.Name.Span,
                $"'{Name(decl.Name.Name)}' needs a type annotation or an initializer");
        }

        HirExpr? initializer = null;
        var type = declared ?? _types.Error;
        if (decl.Initializer is not null)
        {
            initializer = CheckExpr(decl.Initializer, declared);
            if (declared is null)
            {
                // A copy of a `borrow mut` variable is a reborrow (type-system.md §13.3).
                initializer = Coerce(initializer, initializer.Type, decl.Initializer.Span);
                type = initializer.Type;
                if (type.Kind == TypeKind.Void)
                {
                    Error(DiagnosticCodes.VoidValue, decl.Initializer.Span, "this expression has no value (its type is 'void')");
                    type = _types.Error;
                }
                else if (type.Kind == TypeKind.Never)
                {
                    type = _types.Error;
                }
            }
        }

        if (type.IsReference && decl.IsMutable) // other scoped values may be `var` (§19.1, M4c)
        {
            Error(DiagnosticCodes.ReferenceNotAllowed, decl.Name.Span,
                "a reference variable must be declared with 'let': references cannot be re-pointed (assigning to one writes through it)");
            type = _types.Error;
        }

        if (LookupLocal(decl.Name.Name) is not null)
        {
            Error(DiagnosticCodes.LocalRedeclared, decl.Name.Span,
                $"'{Name(decl.Name.Name)}' is already declared in this function; locals cannot be shadowed");
        }

        // Declared after the initializer is checked: `let x = x;` does not see the new `x`.
        // A `using` binding is pinned, not immutable: it may be mutated and mutably borrowed (§14.6).
        var local = _function!.AddLocal(decl.Name.Name, type, decl.IsMutable || decl.IsUsing, isParameter: false, decl.Name.Span);
        local.IsUsing = decl.IsUsing;
        _scopes[^1][decl.Name.Name] = local;
        return new HirLocalDecl(local, initializer, decl.Span);
    }

    private HirStmt CheckAssign(AssignStmt assign)
    {
        var op = assign.Op switch
        {
            AssignOp.Assign => (BinaryOp?)null,
            AssignOp.Add => BinaryOp.Add,
            AssignOp.Subtract => BinaryOp.Subtract,
            AssignOp.Multiply => BinaryOp.Multiply,
            AssignOp.Divide => BinaryOp.Divide,
            AssignOp.Remainder => BinaryOp.Remainder,
            AssignOp.BitAnd => BinaryOp.BitAnd,
            AssignOp.BitOr => BinaryOp.BitOr,
            AssignOp.BitXor => BinaryOp.BitXor,
            AssignOp.ShiftLeft => BinaryOp.ShiftLeft,
            _ => BinaryOp.ShiftRight,
        };

        var target = ResolveAssignmentTarget(assign.Target);
        if (target is null)
        {
            Synth(assign.Value, null); // still report errors inside the value
            return new HirExprStmt(ErrorResult(assign.Span), assign.Span);
        }

        HirExpr value;
        if (op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight)
        {
            value = CheckShiftAmount(assign.Value);
        }
        else
        {
            value = CheckExpr(assign.Value, target.Type);
        }
        if (op is { } binaryOp && !target.Type.IsBottom)
        {
            CheckOperatorApplies(binaryOp, target.Type, assign.Span);
        }
        return new HirAssign(target, op, value, assign.Span);
    }

    /// <summary>
    /// Resolves an assignment target to a place (type-system.md §12, §13.3). Assigning to a reference
    /// writes through it: the place gets a final Deref.
    /// </summary>
    private HirPlace? ResolveAssignmentTarget(Expr target)
    {
        if (target is NameExpr name && Lookup(name.Name.Name) is not HirLocal)
        {
            if (Lookup(name.Name.Name) is null)
            {
                Error(DiagnosticCodes.UndefinedName, name.Span, $"cannot find '{Name(name.Name.Name)}' in this scope");
            }
            else
            {
                Error(DiagnosticCodes.AssignToImmutable, target.Span, $"cannot assign to '{Name(name.Name.Name)}': it is not a variable");
            }
            return null;
        }
        if (target is not (NameExpr or MemberExpr or ThisExpr or ParenExpr or IndexExpr))
        {
            Error(DiagnosticCodes.Unsupported, target.Span, "only variables, their fields, and places reached through references can be assigned");
            return null;
        }

        var value = Synth(target, null);
        if (value.Type.IsBottom)
        {
            return null;
        }
        if (ToPlace(value) is not { } place)
        {
            Error(DiagnosticCodes.Unsupported, target.Span, "only variables, their fields, and places reached through references can be assigned");
            return null;
        }
        if (place.IsLocal && place.Root.IsUsing && !place.Type.IsReference)
        {
            Error(DiagnosticCodes.AssignToImmutable, target.Span,
                $"cannot assign to '{Name(place.Root.Name)}': a 'using' binding stays the same value until the end of its block");
            return null;
        }
        // Assigning to a reference local writes through it (§13.3); assigning to a reference-typed field or
        // element replaces the reference stored there (§25.4).
        if (place.Type.IsReference && place.IsLocal)
        {
            place = Project(place, Projection.Deref);
        }
        if (MutabilityError(place) is { } reason)
        {
            Error(DiagnosticCodes.AssignToImmutable, target.Span, $"cannot assign to {DescribePlace(place)}: {reason}");
            return null;
        }
        return place;
    }

    /// <summary>The place an expression denotes, if it is one: a local, `this`, fields, and derefs (type-system.md §13.2).</summary>
    private static HirPlace? ToPlace(HirExpr expr) => expr switch
    {
        HirLocalRef local => new HirPlace(local.Local, [], local.Type),
        HirFieldAccess field when ToPlace(field.Target) is { } target => Project(target, new Projection(field.Field)),
        HirDeref deref when ToPlace(deref.Reference) is { } target => Project(target, Projection.Deref),
        HirIndex index when ToPlace(index.Target) is { } target => Project(target, Projection.At(index.Index)),
        _ => null,
    };

    /// <summary>
    /// Is <paramref name="expr"/> a place reached through a reference that is not a place itself (a call's result)?
    /// For a mutable borrow every reference on the way must be mutable (§13.2).
    /// </summary>
    private static bool IsPathThroughReference(HirExpr expr, bool isMutable) => expr switch
    {
        HirFieldAccess field => IsPathThroughReference(field.Target, isMutable),
        HirIndex index => !index.Target.Type.IsUnsized && IsPathThroughReference(index.Target, isMutable),
        HirDeref { Reference.Type.IsReference: true } deref when (!isMutable || deref.Reference.Type.IsMutableReference) =>
            ToPlace(deref.Reference) is null ? deref.Reference is not (HirDeref or HirFieldAccess or HirIndex) : IsPathThroughReference(deref.Reference, isMutable),
        _ => false,
    };

    private static HirPlace Project(HirPlace place, Projection projection) => new(
        place.Root,
        place.Projections.Add(projection),
        projection.Apply(place.Type));

    /// <summary>Null if the place may be written or mutably borrowed; otherwise the reason it may not.</summary>
    private string? MutabilityError(HirPlace place)
    {
        var mutable = place.Root.IsMutable;
        var type = place.Root.Type;
        var throughShared = false;
        foreach (var projection in place.Projections)
        {
            if (projection.IsDeref && type.IsString)
            {
                return "the bytes of a 'String' cannot be modified: they must stay valid UTF-8 (add text with 'append')";
            }
            if (projection.IsDeref && !type.IsBox && !type.IsVector && !type.IsStableVector) // a box's or vector's contents are as mutable as it is (§18.2, §23.1)
            {
                mutable = type.IsMutableReference;
                throughShared = !mutable;
            }
            type = projection.Apply(type);
        }
        if (mutable)
        {
            return null;
        }
        var root = Name(place.Root.Name);
        if (!throughShared && !place.Root.IsParameter && TypeTable.IsScoped(place.Root.Type) && !place.Root.Type.IsReference)
        {
            return $"'{root}' is not mutable; bind it with 'using' (a scoped value cannot be a 'var')";
        }
        return throughShared ? "it is behind a shared reference ('borrow'); a 'borrow mut' reference is needed to modify it"
            : place.Root.Name == _thisSymbol ? "'this' is immutable in a by-value method; use 'borrow mut this'"
            : place.Root.IsParameter ? "parameters are immutable"
            : $"'{root}' was declared with 'let'; use 'var' for a mutable variable";
    }

    private string DescribePlace(HirPlace place)
    {
        var text = Name(place.Root.Name);
        var hasField = false;
        foreach (var projection in place.Projections.Where(p => p.IsField))
        {
            text += "." + Name(projection.Field!.Name);
            hasField = true;
        }
        if (place.Projections.Any(p => p.IsDeref) && place.Root.Type.IsBox && !hasField)
        {
            return $"the contents of '{text}'";
        }
        return place.Projections.Any(p => p.Kind == ProjectionKind.Index) ? $"an element of '{text}'"
            : hasField ? $"'{text}'" : place.Projections.IsEmpty ? $"'{text}'" : $"the value '{text}' refers to";
    }

    /// <summary>
    /// Reads through references and boxes until the value is neither (auto-deref, type-system.md §13.3, §18.2).
    /// A slice is not read through: its unsized <c>[T]</c> is only reached by indexing.
    /// </summary>
    private static HirExpr AutoDeref(HirExpr expr)
    {
        while (expr.Type.IsReference || expr.Type.IsBox || expr.Type.IsVector || expr.Type.IsStableVector)
        {
            expr = new HirDeref(expr, expr.Span); // a vector reaches its elements (§23.1)
        }
        return expr;
    }

    /// <summary>A `borrow p` / `borrow mut p` of the place a checked expression denotes (N0247, N0248).</summary>
    private HirExpr BorrowPlace(HirExpr value, bool isMutable, TextSpan span)
    {
        if (ToPlace(value) is null && IsPathThroughReference(value, isMutable))
        {
            return new HirBorrowPath(isMutable, value, _types.ReferenceTo(value.Type, isMutable), span);
        }
        if (ToPlace(value) is not { } place)
        {
            Error(DiagnosticCodes.BorrowOfNonPlace, span,
                "only a variable, a field, or a place reached through a reference can be borrowed; bind the value to a local first");
            return ErrorResult(span);
        }
        if (isMutable && MutabilityError(place) is { } reason)
        {
            Error(DiagnosticCodes.BorrowMutOfImmutable, span, $"cannot borrow {DescribePlace(place)} as mutable: {reason}");
            return ErrorResult(span);
        }
        return new HirBorrow(isMutable, place, _types.ReferenceTo(place.Type, isMutable), span);
    }

    /// <summary>
    /// Converts a checked expression to an expected type: auto-deref to the pointee, reborrowing each
    /// use of a `borrow mut` variable, and `borrow mut T` to `borrow T` (type-system.md §13.3).
    /// </summary>
    private HirExpr Coerce(HirExpr hir, NovaType? expected, TextSpan span)
    {
        if (expected is null || expected.Kind == TypeKind.Error || hir.Type.IsBottom)
        {
            return hir;
        }
        if (ReferenceEquals(hir.Type, expected))
        {
            return expected.IsMutableReference && hir is HirLocalRef ? new HirReborrow(true, hir, expected, span) : hir;
        }
        if (expected.IsReference && hir.Type.IsMutableReference && !expected.IsMutableReference
            && ReferenceEquals(hir.Type.Pointee, expected.Pointee))
        {
            return new HirReborrow(false, hir, expected, span);
        }
        if (expected.IsRawPointer && hir.Type.IsMutablePointer && !expected.IsMutablePointer && ReferenceEquals(hir.Type.Pointee, expected.Pointee))
        {
            return new HirCast(hir, expected, span); // `*mut T` to `*T` (§27.1)
        }
        // `borrow Box<T>` to `borrow T` (and mut to mut or shared): a reborrow of the box's contents (§18.2).
        if (expected.IsReference && hir.Type.Pointee is { IsBox: true } box && ReferenceEquals(box.Pointee, expected.Pointee)
            && (hir.Type.IsMutableReference || !expected.IsMutableReference))
        {
            return new HirReborrow(expected.IsMutableReference, new HirDeref(hir, span), expected, span);
        }
        if (!expected.IsReference && (hir.Type.IsReference || hir.Type.IsBox))
        {
            // Read through references and boxes (any number of boxes) down to the expected type.
            var read = hir;
            while ((read.Type.IsReference || read.Type.IsBox) && !ReferenceEquals(read.Type, expected))
            {
                read = new HirDeref(read, span);
            }
            if (ReferenceEquals(read.Type, expected))
            {
                return read;
            }
        }
        if (expected.Enum?.OptionType is { } wrapped)
        {
            // A `T` where a `T?` is expected is `Some(value)` (§22.1).
            var inner = Quietly(() => Coerce(hir, wrapped, span));
            if (!inner.Type.IsBottom)
            {
                return new HirEnumValue(expected.Enum.Variants[1], [inner], expected, span);
            }
        }
        Error(DiagnosticCodes.TypeMismatch, span, $"expected '{expected}', found '{hir.Type}'");
        return ErrorResult(span);
    }

    /// <summary>An argument for a parameter of type <paramref name="parameterType"/> (type-system.md §13.2, call sites).</summary>
    private HirExpr CheckArgument(Expr argument, NovaType? parameterType)
    {
        if (parameterType is null)
        {
            Synth(argument is MutArgExpr extra ? extra.Operand : argument, null);
            return ErrorResult(argument.Span);
        }

        if (argument is MutArgExpr mutArgument)
        {
            if (!parameterType.IsMutableReference)
            {
                Error(DiagnosticCodes.MutArgumentMismatch, mutArgument.Span,
                    "'mut' is only for arguments passed to a 'borrow mut' parameter");
                Synth(mutArgument.Operand, null);
                return ErrorResult(argument.Span);
            }
            if (mutArgument.Operand is SliceExpr mutSlice && parameterType.IsSlice)
            {
                return Coerce(SynthSlice(mutSlice, isMutable: true, argument.Span), parameterType, argument.Span);
            }
            var target = Synth(mutArgument.Operand, IntegerHint(parameterType.Pointee));
            if ((target.Type.IsBox || target.Type.IsVector) && ReferenceEquals(target.Type.Pointee, parameterType.Pointee))
            {
                target = new HirDeref(target, target.Span); // `mut b` lends the box's contents; `mut v` the vector's elements
            }
            if (target.Type.IsBottom || target.Type.IsReference)
            {
                return Coerce(target, parameterType, argument.Span);
            }
            if (!ReferenceEquals(target.Type, parameterType.Pointee))
            {
                Error(DiagnosticCodes.TypeMismatch, mutArgument.Operand.Span, $"expected '{parameterType.Pointee}', found '{target.Type}'");
                return ErrorResult(argument.Span);
            }
            return BorrowPlace(target, isMutable: true, argument.Span);
        }

        if (!parameterType.IsReference)
        {
            return CheckExpr(argument, parameterType);
        }

        var value = Synth(argument, IntegerHint(parameterType.Pointee));
        if ((value.Type.IsBox || value.Type.IsVector) && ReferenceEquals(value.Type.Pointee, parameterType.Pointee))
        {
            // A box argument for `borrow T` borrows its contents; a vector or string for a slice or view, its elements.
            value = new HirDeref(value, value.Span);
        }
        if (value.Type.IsBottom || value.Type.IsReference)
        {
            return Coerce(value, parameterType, argument.Span);
        }
        if (!ReferenceEquals(value.Type, parameterType.Pointee))
        {
            Error(DiagnosticCodes.TypeMismatch, argument.Span, $"expected '{parameterType}', found '{value.Type}'");
            return ErrorResult(argument.Span);
        }
        if (parameterType.IsMutableReference)
        {
            Error(DiagnosticCodes.MutArgumentMismatch, argument.Span,
                "this parameter is 'borrow mut': write 'mut' before the argument to let the call modify it");
            return ErrorResult(argument.Span);
        }
        // Implicit shared borrow: of the place itself, or of a temporary holding the value (lifetimes.md §6).
        return ToPlace(value) is { } place
            ? new HirBorrow(false, place, parameterType, argument.Span)
            : new HirBorrowTemp(false, value, parameterType, argument.Span);
    }

    /// <summary>The field <paramref name="name"/> of a value of <paramref name="type"/>, or null after reporting N0230/N0234.</summary>
    private StructField? ResolveField(NovaType type, Identifier name)
    {
        if (type.IsBottom)
        {
            return null;
        }
        if (type.Struct is not { } definition)
        {
            Error(DiagnosticCodes.NotAStruct, name.Span, $"type '{type}' has no fields; '{Name(name.Name)}' cannot be accessed");
            return null;
        }
        if (definition.FindField(name.Name) is { } field)
        {
            return field;
        }
        Error(DiagnosticCodes.UnknownField, name.Span, definition.Functions.ContainsKey(name.Name)
            ? $"'{type}.{Name(name.Name)}' is a function; call it with '(...)'"
            : $"'{type}' has no field named '{Name(name.Name)}'");
        return null;
    }

    private HirIf CheckIf(IfStmt ifStmt)
    {
        var condition = CheckExpr(ifStmt.Condition, _types.Bool);
        var then = CheckBlock(ifStmt.Then);
        HirStmt? elseBranch = ifStmt.Else switch
        {
            null => null,
            IfStmt nested => CheckIf(nested),
            BlockStmt block => CheckBlock(block),
            _ => throw new InvalidOperationException("else branch must be a block or if"),
        };
        return new HirIf(condition, then, elseBranch, ifStmt.Span);
    }

    private HirReturn CheckReturn(ReturnStmt ret)
    {
        var returnType = _function!.ReturnType;
        if (ret.Value is null)
        {
            if (returnType.Kind != TypeKind.Void && !returnType.IsBottom)
            {
                Error(DiagnosticCodes.ReturnValueMismatch, ret.Span,
                    $"missing return value: '{Name(_function.Name)}' returns '{returnType}'");
            }
            return new HirReturn(null, ret.Span);
        }
        if (returnType.Kind == TypeKind.Void)
        {
            Error(DiagnosticCodes.ReturnValueMismatch, ret.Value.Span,
                $"'{Name(_function.Name)}' returns 'void', so 'return' cannot have a value");
            Synth(ret.Value, null);
            return new HirReturn(null, ret.Span);
        }
        return new HirReturn(CheckExpr(ret.Value, returnType), ret.Span);
    }

    // ---- Names ----

    private HirLocal? LookupLocal(Symbol name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var local))
            {
                return local;
            }
        }
        return null;
    }

    /// <summary>Locals (innermost first), then package items, then built-ins (type-system.md §2).</summary>
    private object? Lookup(Symbol name) =>
        (object?)LookupLocal(name) ?? _items.GetValueOrDefault(name) ?? (_builtins.TryGetValue(name, out var b) ? b : null);

    // ---- Expressions ----

    /// <summary>Checks <paramref name="expr"/> against an optional expected type (type-system.md §5).</summary>
    private HirExpr CheckExpr(Expr expr, NovaType? expected) =>
        Coerce(Synth(expr, expected is { IsVector: true } or { IsStableVector: true } ? expected : expected?.Pointee ?? expected), expected, expr.Span);

    /// <summary>
    /// The <c>StringView</c> an auto-dereferenced value reads, if it is the text of one: auto-deref goes
    /// down to the unsized <c>str</c>, but views are used as values (§22.2).
    /// </summary>
    private static HirExpr ViewOf(HirExpr value) => value is HirDeref { Type.IsStr: true } text ? text.Reference : value;

    /// <summary><see cref="ViewOf"/>, and a <c>String</c> place as a view of its text (§23.2).</summary>
    private HirExpr PrintableView(HirExpr value) =>
        value is HirDeref { Type.IsStr: true, Reference.Type.IsString: true } && ToPlace(value) is { } text
            ? new HirBorrow(false, text, _types.StringView, value.Span)
            : ViewOf(value);

    /// <summary>The integer type a literal takes from <paramref name="expected"/>, looking through an optional (§22.1).</summary>
    private static NovaType? IntegerHint(NovaType? expected) =>
        (expected?.Enum?.OptionType ?? expected) is { IsInteger: true } integer ? integer : null;

    private HirExpr Synth(Expr expr, NovaType? expected)
    {
        switch (expr)
        {
            case CallExpr { Callee: MemberExpr { Target: NameExpr hostedName } } hostedCall when _profile == Profile.Freestanding
                && Lookup(hostedName.Name.Name) is Builtin hosted && IsHostedOnly(hosted):
                // Box.create, Vector<T>.create, Console.write, … (cosmoos/profiles.md §4.1).
                HostedOnly(hostedName.Span, $"'{Name(hostedName.Name.Name)}'");
                CheckArgumentsLoosely(hostedCall);
                return ErrorResult(hostedCall.Span);
            case IntLiteralExpr literal:
                return CheckIntLiteral(literal, negative: false, expected, literal.Span);
            case BoolLiteralExpr b:
                return new HirBoolConst(b.Value, _types.Bool, b.Span);
            case NullLiteralExpr nullLiteral:
                return CheckNull(nullLiteral, expected);
            case FloatLiteralExpr:
                Error(DiagnosticCodes.Unsupported, expr.Span, "floating-point numbers are not supported in M1");
                return ErrorResult(expr.Span);
            case CharLiteralExpr:
                Error(DiagnosticCodes.Unsupported, expr.Span, "'char' values are not supported in M1");
                return ErrorResult(expr.Span);
            case StringLiteralExpr text:
                return new HirStringConst(text.Value, _types.StringView, text.Span); // static bytes: no loans (§22.2)
            case ParenExpr paren:
                return Synth(paren.Inner, expected) with { Span = paren.Span };
            case UnsafeExpr unsafeExpr:
            {
                _usedUnsafe = true;
                _unsafeDepth++;
                var inner = Synth(unsafeExpr.Inner, expected);
                _unsafeDepth--;
                return inner with { Span = unsafeExpr.Span };
            }
            case ThisExpr:
                if (LookupLocal(_thisSymbol) is { } receiver)
                {
                    return new HirLocalRef(receiver, expr.Span);
                }
                Error(DiagnosticCodes.ThisOutsideMethod, expr.Span, "'this' can only be used inside a method");
                return ErrorResult(expr.Span);
            case StructLiteralExpr literal:
                return SynthStructLiteral(literal, expected);
            case BorrowExpr { Operand: SliceExpr slice } borrow:
                return SynthSlice(slice, borrow.IsMutable, borrow.Span);
            case BorrowExpr borrow:
            {
                var operand = Synth(borrow.Operand, null);
                if (operand.Type.IsBottom)
                {
                    return operand;
                }
                if (operand.Type.IsReference)
                {
                    Error(DiagnosticCodes.ReferenceNotAllowed, borrow.Span,
                        "this is already a reference; use it directly (references to references are not supported)");
                    return ErrorResult(borrow.Span);
                }
                return BorrowPlace(operand, borrow.IsMutable, borrow.Span);
            }
            case SliceExpr slice:
                return SynthSlice(slice, isMutable: false, slice.Span);
            case IndexExpr index:
            {
                var target = AutoDeref(Synth(index.Target, null));
                var position = CheckExpr(index.Index, _types.USize);
                if (target.Type.IsBottom)
                {
                    return target;
                }
                if (target.Type.Kind != TypeKind.Array)
                {
                    Error(DiagnosticCodes.TypeMismatch, index.Target.Span, $"only arrays and slices can be indexed; found '{target.Type}'");
                    return ErrorResult(index.Span);
                }
                return new HirIndex(target, position, target.Type.Element!, index.Span);
            }
            case ArrayLiteralExpr literal:
            {
                var elementExpected = expected is { Kind: TypeKind.Array, IsUnsized: false } ? expected.Element : null;
                var first = ArrayElement(literal.Elements[0], elementExpected);
                var elementType = elementExpected ?? first.Type;
                var elements = ImmutableArray.CreateBuilder<HirExpr>(literal.Elements.Length);
                elements.Add(first);
                foreach (var element in literal.Elements.Skip(1))
                {
                    elements.Add(CheckExpr(element, elementType));
                }
                var arrayType = ArrayType(elementType, literal.Elements.Length, literal.Elements[0].Span);
                return arrayType.IsBottom ? ErrorResult(literal.Span) : new HirArrayLiteral(elements.MoveToImmutable(), arrayType, literal.Span);
            }
            case ArrayRepeatExpr repeat:
            {
                var elementExpected = expected is { Kind: TypeKind.Array, IsUnsized: false } ? expected.Element : null;
                var value = ArrayElement(repeat.Value, elementExpected);
                if (repeat.Count is not IntLiteralExpr { Suffix: IntSuffix.None or IntSuffix.USize } count
                    || count.Value == 0 || count.Value > MaxArrayLength)
                {
                    Error(DiagnosticCodes.InvalidArray, repeat.Count.Span, $"an array length must be an integer literal from 1 to {MaxArrayLength}");
                    return ErrorResult(repeat.Span);
                }
                var arrayType = ArrayType(elementExpected ?? value.Type, (long)count.Value, repeat.Value.Span);
                return arrayType.IsBottom ? ErrorResult(repeat.Span) : new HirArrayRepeat(value, arrayType, repeat.Span);
            }
            case MoveExpr move:
            {
                var operand = Synth(move.Operand, null);
                if (operand.Type.IsBottom)
                {
                    return operand;
                }
                if (ToPlace(operand) is not { } place)
                {
                    Error(DiagnosticCodes.BorrowOfNonPlace, move.Span, "only a variable or a field can be moved out of with 'move'");
                    return ErrorResult(move.Span);
                }
                return new HirMove(place, move.Span);
            }
            case MutArgExpr mutArgument:
                Error(DiagnosticCodes.MutArgumentMismatch, mutArgument.Span,
                    "'mut' marks an argument for a 'borrow mut' parameter; it is not an expression");
                return ErrorResult(expr.Span);
            case NameExpr name when expected is { IsFunctionPointer: true } && name.TypeArguments.IsEmpty
                && Lookup(name.Name.Name) is HirFunction function:
                return FunctionAddress(name, function, expected);
            case NameExpr name:
                return SynthName(name);
            case UnaryExpr unary:
                return SynthUnary(unary, expected);
            case BinaryExpr binary:
                return SynthBinary(binary, expected);
            case CastExpr cast:
                return SynthCast(cast);
            case CallExpr { Callee: NameExpr { TypeArguments.IsEmpty: true } constructor } call
                when Name(constructor.Name.Name) is "Ok" or "Err" && Lookup(constructor.Name.Name) is null:
                return CheckResultValue(call, constructor, expected);
            case CallExpr { Callee: NameExpr { TypeArguments.IsEmpty: true } some } someCall
                when Name(some.Name.Name) == "Some" && Lookup(some.Name.Name) is null:
                return CheckSomeValue(someCall, expected);
            case CallExpr { Callee: MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } boxName } boxMember } boxCall
                when Lookup(boxName.Name.Name) is Builtin.Box:
                return CheckBoxCreate(boxCall, boxMember, expected);
            case CallExpr { Callee: MemberExpr { Target: NameExpr vectorName } vectorMember } vectorCall
                when Lookup(vectorName.Name.Name) is Builtin.Vector or Builtin.String:
                return CheckVectorCreate(vectorCall, vectorMember, vectorName, Lookup(vectorName.Name.Name) is Builtin.String, expected);
            case CallExpr { Callee: MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } resourceName } resourceMember } resourceCall
                when Lookup(resourceName.Name.Name) is Builtin.File or Builtin.TcpListener or Builtin.TcpStream:
                return CheckResourceFunction(resourceCall, resourceMember, (Builtin)Lookup(resourceName.Name.Name)!);
            case CallExpr { Callee: MemberExpr { Target: NameExpr containerName } containerMember } containerCall
                when Lookup(containerName.Name.Name) is Builtin.SlotMap or Builtin.StableVector:
                return CheckContainerCreate(containerCall, containerMember, containerName,
                    Lookup(containerName.Name.Name) is Builtin.SlotMap ? TypeKind.SlotMap : TypeKind.StableVector, expected);
            case CallExpr { Callee: MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } arenaName } arenaMember } arenaCall
                when Lookup(arenaName.Name.Name) is Builtin.Arena:
                return CheckArenaCreate(arenaCall, arenaMember);
            case CallExpr { Callee: MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } poolName } poolMember } poolCall
                when Lookup(poolName.Name.Name) is Builtin.Pool:
                return CheckPoolCreate(poolCall, poolMember, expected);
            case CallExpr { Callee: MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } regionName } regionMember } regionCall
                when Lookup(regionName.Name.Name) is Builtin.FixedBufferAllocator or Builtin.Scratch:
                return CheckRegionConstructor(regionCall, regionMember, (Builtin)Lookup(regionName.Name.Name)!);
            case CallExpr call:
                return SynthCall(call, expected);
            case TryExpr tryExpr:
                return CheckTry(tryExpr);
            case UnwrapExpr unwrap:
            {
                var operand = CheckResultOperand(unwrap.Operand, "!");
                return operand is null
                    ? ErrorResult(unwrap.Span)
                    : new HirUnwrap(operand, operand.Type.Enum!.OptionType ?? operand.Type.Enum.ResultTypes!.Value.Ok, unwrap.Span);
            }
            case MemberExpr member:
                return SynthMember(member, expected);
            case ErrorExpr:
                return ErrorResult(expr.Span);
            default:
                throw new InvalidOperationException($"unexpected expression {expr.GetType().Name}");
        }
    }

    /// <summary>An array element: checked against the expected element type, or its own type (auto-deref'd).</summary>
    private HirExpr ArrayElement(Expr element, NovaType? expected)
    {
        if (expected is not null)
        {
            return CheckExpr(element, expected);
        }
        var value = Synth(element, null);
        while (value.Type.IsReference)
        {
            value = new HirDeref(value, value.Span); // references are read through; boxes are values (§18.2)
        }
        if (value.Type.Kind is TypeKind.Void or TypeKind.Never)
        {
            Error(DiagnosticCodes.VoidValue, element.Span, $"this expression has no value (its type is '{value.Type}')");
            return ErrorResult(element.Span);
        }
        return value;
    }

    /// <summary><c>a[lo..hi]</c> or <c>borrow mut a[lo..hi]</c>: a slice of an array or slice place (type-system.md §16.3).</summary>
    private HirExpr SynthSlice(SliceExpr slice, bool isMutable, TextSpan span)
    {
        var target = AutoDeref(Synth(slice.Target, null));
        var low = slice.Low is null ? null : CheckExpr(slice.Low, _types.USize);
        var high = slice.High is null ? null : CheckExpr(slice.High, _types.USize);
        if (target.Type.IsBottom)
        {
            return target;
        }
        if (target.Type.Kind != TypeKind.Array)
        {
            Error(DiagnosticCodes.TypeMismatch, slice.Target.Span, $"only arrays and slices can be sliced; found '{target.Type}'");
            return ErrorResult(span);
        }
        if (ToPlace(target) is not { } place)
        {
            Error(DiagnosticCodes.BorrowOfNonPlace, span,
                "only a variable, a field, or a place reached through a reference can be sliced; bind the value to a local first");
            return ErrorResult(span);
        }
        if (target.Type.IsStr)
        {
            if (isMutable)
            {
                Error(DiagnosticCodes.BorrowMutOfImmutable, span, "a 'StringView' is read-only; it cannot be sliced as mutable");
                return ErrorResult(span);
            }
            return new HirSlice(false, place, low, high, _types.StringView, span); // checks character boundaries too
        }
        if (isMutable && MutabilityError(place) is { } reason)
        {
            Error(DiagnosticCodes.BorrowMutOfImmutable, span, $"cannot borrow {DescribePlace(place)} as mutable: {reason}");
            return ErrorResult(span);
        }
        return new HirSlice(isMutable, place, low, high, _types.SliceOf(target.Type.Element!, isMutable), span);
    }

    private HirExpr SynthMember(MemberExpr member, NovaType? expected = null)
    {
        // `Shape.Empty`: a variant without payload.
        if (member.Target is NameExpr enumName && Lookup(enumName.Name.Name) is EnumEntry entry)
        {
            return CheckEnumValue(entry, member, [], member.Span, expected, enumName.TypeArguments);
        }
        // `Console.writeLine` or `Point.origin` without a call.
        if (member.Target is NameExpr { TypeArguments.IsEmpty: true } target && Lookup(target.Name.Name) is Builtin or StructEntry)
        {
            Error(DiagnosticCodes.NotCallable, member.Span,
                $"'{Name(target.Name.Name)}.{Name(member.Member.Name)}' is not a value; call it with '(...)'");
            return ErrorResult(member.Span);
        }
        var synthesized = Synth(member.Target, null);
        var vector = synthesized;
        while (vector.Type.IsReference)
        {
            vector = new HirDeref(vector, vector.Span);
        }
        if (vector.Type.Kind == TypeKind.SlotMap)
        {
            if (Name(member.Member.Name) != "length")
            {
                Error(DiagnosticCodes.UnknownField, member.Member.Span, $"'{vector.Type}' has no field '{Name(member.Member.Name)}'; it has 'length'");
                return ErrorResult(member.Span);
            }
            var map = BorrowPlace(vector, isMutable: false, member.Target.Span);
            return map.Type.IsBottom ? map : new HirBuiltinCall(BuiltinFunction.SlotMapLength, [map], _types.USize, member.Span);
        }
        if (vector.Type.IsVector && Name(member.Member.Name) == "capacity")
        {
            var borrowed = BorrowPlace(vector, isMutable: false, member.Target.Span);
            return borrowed.Type.IsBottom ? borrowed : new HirBuiltinCall(BuiltinFunction.VectorCapacity, [borrowed], _types.USize, member.Span);
        }
        var value = AutoDeref(synthesized);
        if (value is HirDeref { Type.IsUnsized: true, Reference.Type: { IsReference: true } view } && Name(member.Member.Name) == "ptr")
        {
            // The address of element 0 (§27.2).
            var slice = Coerce(((HirDeref)value).Reference, view, member.Target.Span); // a SliceMut is reborrowed, not moved
            return new HirBuiltinCall(BuiltinFunction.SlicePtr, [slice], _types.PointerTo(value.Type.Element!, view.IsMutableReference), member.Span);
        }
        if (value.Type.Kind == TypeKind.Array)
        {
            if (Name(member.Member.Name) != "length")
            {
                Error(DiagnosticCodes.UnknownField, member.Member.Span, vector.Type.IsVector || vector.Type.IsStableVector
                    ? $"'{vector.Type}' has no field '{Name(member.Member.Name)}'; it has 'length'{(vector.Type.IsVector ? " and 'capacity'" : "")}"
                    : $"'{value.Type}' has no member '{Name(member.Member.Name)}'; arrays and slices only have 'length'");
                return ErrorResult(member.Span);
            }
            if (!value.Type.IsUnsized && ToPlace(value) is null)
            {
                // The constant would skip evaluating the array (and any calls in it).
                Error(DiagnosticCodes.Unsupported, member.Span, "'length' needs an array variable or field; bind the value to a local first");
                return ErrorResult(member.Span);
            }
            return value.Type.IsUnsized
                ? new HirSliceLength(((HirDeref)value).Reference, _types.USize, member.Span)
                : new HirIntConst(value.Type.ArrayLength, _types.USize, member.Span);
        }
        return ResolveField(value.Type, member.Member) is { } field
            ? new HirFieldAccess(value, field, member.Span)
            : ErrorResult(member.Span);
    }

    private HirExpr SynthStructLiteral(StructLiteralExpr literal, NovaType? expected = null)
    {
        var typeName = Name(literal.TypeName.Name);
        if (_items.GetValueOrDefault(literal.TypeName.Name) is not StructEntry entry)
        {
            Error(_types.Lookup(typeName) is null && Lookup(literal.TypeName.Name) is null ? DiagnosticCodes.UnknownType : DiagnosticCodes.NotAStruct,
                literal.TypeName.Span, $"'{typeName}' is not a struct type");
            foreach (var init in literal.Fields)
            {
                Synth(init.Value, null);
            }
            return ErrorResult(literal.Span);
        }
        Used(entry);
        if (_profile == Profile.Freestanding && IsAllocatorType(entry.Type))
        {
            HostedOnly(literal.TypeName.Span, $"'{typeName}'");
            return ErrorResult(literal.Span);
        }
        if (!entry.Decl.TypeParameters.IsEmpty)
        {
            // `Pair { first: 1, … }` infers T from the fields; `Pair<u8> { … }` gives it (§20.2).
            var template = entry.Type.Struct!;
            var found = TypeArgumentsFor(typeName, template.TypeArguments, literal.TypeArguments,
                expected?.Struct is { } expectedStruct && expectedStruct.Syntax == entry.Decl ? expectedStruct.TypeArguments : null,
                literal.Fields.Select(f => (template.FindField(f.Name.Name)?.Type, f.Value)).Where(p => p.Item1 is not null)
                    .Select(p => (p.Item1!, p.Value)),
                literal.Span);
            if (found.IsDefault)
            {
                foreach (var init in literal.Fields)
                {
                    Synth(init.Value, null);
                }
                return ErrorResult(literal.Span);
            }
            entry = InstantiateStruct(entry, found, literal.Span);
        }
        else if (!literal.TypeArguments.IsEmpty)
        {
            Error(DiagnosticCodes.UnknownType, literal.TypeName.Span, $"'{typeName}' is not generic; write it without type arguments");
        }

        var type = entry.Type;
        var definition = type.Struct!;
        var fields = ImmutableArray.CreateBuilder<(StructField, HirExpr)>(literal.Fields.Length);
        var seen = new HashSet<StructField>();
        var ok = true;
        foreach (var init in literal.Fields)
        {
            var field = definition.FindField(init.Name.Name);
            if (field is null)
            {
                Error(DiagnosticCodes.UnknownField, init.Name.Span, $"'{typeName}' has no field named '{Name(init.Name.Name)}'");
                Synth(init.Value, null);
                ok = false;
                continue;
            }
            if (!seen.Add(field))
            {
                Error(DiagnosticCodes.DuplicateFieldInit, init.Name.Span, $"field '{Name(field.Name)}' is initialized more than once");
                ok = false;
                continue;
            }
            fields.Add((field, CheckExpr(init.Value, field.Type)));
        }

        var missing = definition.Fields.Where(f => !seen.Contains(f)).Select(f => $"'{Name(f.Name)}'").ToList();
        if (missing.Count > 0 && ok)
        {
            Error(DiagnosticCodes.MissingField, literal.TypeName.Span,
                $"missing {(missing.Count == 1 ? "field" : "fields")} {string.Join(", ", missing)} in '{typeName}' literal");
            ok = false;
        }
        return ok ? new HirStructLiteral(fields.ToImmutable(), type, literal.Span) : ErrorResult(literal.Span);
    }

    private HirExpr CheckIntLiteral(IntLiteralExpr literal, bool negative, NovaType? expected, TextSpan span)
    {
        var type = _types.ForSuffix(literal.Suffix) ?? IntegerHint(expected) ?? _types.I32;
        if (negative && !type.IsSigned)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, span, $"cannot negate a value of unsigned type '{type}'");
            return ErrorResult(span);
        }
        var value = negative ? -(BigInteger)literal.Value : literal.Value;
        if (!type.Fits(value))
        {
            Error(DiagnosticCodes.LiteralOutOfRange, span,
                $"integer literal {value} does not fit in '{type}' (range {type.Min}..{type.Max})");
            return ErrorResult(span);
        }
        return new HirIntConst((Int128)value, type, span);
    }

    private HirExpr SynthName(NameExpr name)
    {
        var symbol = name.Name.Name;
        if (!name.TypeArguments.IsEmpty)
        {
            Error(DiagnosticCodes.Unsupported, name.Span, "type arguments go on a call ('f<T>(…)'), a type or a static function ('S<T>.f(…)')");
            return ErrorResult(name.Span);
        }

        switch (Lookup(symbol))
        {
            case HirLocal local:
                return new HirLocalRef(local, name.Span);
            case ConstEntry constant:
                return EvaluateConstant(constant) switch
                {
                    HirIntConst i => new HirIntConst(i.Value, constant.Hir.Type, name.Span),
                    HirBoolConst b => new HirBoolConst(b.Value, constant.Hir.Type, name.Span),
                    _ => ErrorResult(name.Span),
                };
            case HirFunction or GenericFunctionEntry:
                Error(DiagnosticCodes.NotCallable, name.Span,
                    $"function '{Name(symbol)}' cannot be used as a value; call it with '(...)'");
                return ErrorResult(name.Span);
            case StructEntry or EnumEntry:
                Error(DiagnosticCodes.NotCallable, name.Span, $"'{Name(symbol)}' is a type, not a value");
                return ErrorResult(name.Span);
            case Builtin:
                Error(DiagnosticCodes.NotCallable, name.Span, $"'{Name(symbol)}' is not a value");
                return ErrorResult(name.Span);
            default:
                Error(DiagnosticCodes.UndefinedName, name.Span, $"cannot find '{Name(symbol)}' in this scope");
                return ErrorResult(name.Span);
        }
    }

    private HirExpr SynthUnary(UnaryExpr unary, NovaType? expected)
    {
        if (unary.Op == UnaryOp.Not)
        {
            var operand = CheckExpr(unary.Operand, _types.Bool);
            return new HirUnary(UnaryOp.Not, operand, _types.Bool, unary.Span);
        }

        // A literal directly under '-' is checked as one negative literal (type-system.md §4).
        if (unary.Operand is IntLiteralExpr literal)
        {
            return CheckIntLiteral(literal, negative: true, expected, unary.Span);
        }

        var value = AutoDeref(Synth(unary.Operand, IntegerHint(expected)));
        if (value.Type.IsBottom)
        {
            return value;
        }
        if (!value.Type.IsInteger || !value.Type.IsSigned)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, unary.Span,
                value.Type.IsInteger
                    ? $"cannot negate a value of unsigned type '{value.Type}'"
                    : $"operator '-' cannot be applied to type '{value.Type}'");
            return ErrorResult(unary.Span);
        }
        return new HirUnary(UnaryOp.Negate, value, value.Type, unary.Span);
    }

    private static bool IsArithmetic(BinaryOp op) => op is BinaryOp.Add or BinaryOp.Subtract or BinaryOp.Multiply
        or BinaryOp.Divide or BinaryOp.Remainder;

    private static bool IsBitwise(BinaryOp op) => op is BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor;

    private static bool IsShift(BinaryOp op) => op is BinaryOp.ShiftLeft or BinaryOp.ShiftRight;

    private static bool IsComparison(BinaryOp op) => op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less
        or BinaryOp.LessEqual or BinaryOp.Greater or BinaryOp.GreaterEqual;

    /// <summary>Only unsuffixed literals combined by -, (), and arithmetic/bitwise/shift operators (type-system.md §5).</summary>
    private static bool IsFlexible(Expr expr) => expr switch
    {
        IntLiteralExpr literal => literal.Suffix == IntSuffix.None,
        ParenExpr paren => IsFlexible(paren.Inner),
        UnaryExpr { Op: UnaryOp.Negate } unary => IsFlexible(unary.Operand),
        BinaryExpr binary when IsArithmetic(binary.Op) || IsBitwise(binary.Op) || IsShift(binary.Op) =>
            IsFlexible(binary.Left) && IsFlexible(binary.Right),
        _ => false,
    };

    private HirExpr SynthBinary(BinaryExpr binary, NovaType? expected)
    {
        var op = binary.Op;
        if (op is BinaryOp.Equal or BinaryOp.NotEqual && (binary.Left is NullLiteralExpr || binary.Right is NullLiteralExpr))
        {
            return SynthNullTest(binary);
        }
        if (op is BinaryOp.LogicalAnd or BinaryOp.LogicalOr)
        {
            var l = CheckExpr(binary.Left, _types.Bool);
            var r = CheckExpr(binary.Right, _types.Bool);
            return new HirBinary(op, l, r, _types.Bool, binary.Span);
        }

        if (IsShift(op))
        {
            var left = AutoDeref(Synth(binary.Left, IntegerHint(expected)));
            var amount = CheckShiftAmount(binary.Right);
            if (left.Type.IsBottom || amount.Type.IsBottom)
            {
                return ErrorResult(binary.Span);
            }
            return CheckOperatorApplies(op, left.Type, binary.Span)
                ? new HirBinary(op, left, amount, left.Type, binary.Span)
                : ErrorResult(binary.Span);
        }

        var hint = (IsArithmetic(op) || IsBitwise(op)) ? IntegerHint(expected) : null;
        HirExpr leftHir, rightHir;
        if (hint is not null)
        {
            leftHir = CheckExpr(binary.Left, hint);
            rightHir = CheckExpr(binary.Right, hint);
        }
        else if (!IsFlexible(binary.Left) && IsFlexible(binary.Right))
        {
            leftHir = AutoDeref(Synth(binary.Left, null));
            rightHir = CheckExpr(binary.Right, IntegerHint(leftHir.Type));
        }
        else if (IsFlexible(binary.Left) && !IsFlexible(binary.Right))
        {
            rightHir = AutoDeref(Synth(binary.Right, null));
            leftHir = CheckExpr(binary.Left, IntegerHint(rightHir.Type));
        }
        else
        {
            leftHir = AutoDeref(Synth(binary.Left, null));
            rightHir = AutoDeref(Synth(binary.Right, null));
        }

        if (leftHir.Type.IsBottom || rightHir.Type.IsBottom)
        {
            return ErrorResult(binary.Span);
        }
        leftHir = ViewOf(leftHir);
        rightHir = ViewOf(rightHir);
        if (leftHir.Type.IsStringView && rightHir.Type.IsStringView)
        {
            if (op is not (BinaryOp.Equal or BinaryOp.NotEqual))
            {
                Error(DiagnosticCodes.InvalidOperandTypes, binary.Span, $"operator '{OperatorText(op)}' cannot be applied to type 'StringView'; views compare with '==' and '!='");
                return ErrorResult(binary.Span);
            }
            return new HirBinary(op, leftHir, rightHir, _types.Bool, binary.Span); // compares the bytes (§22.2)
        }
        if (leftHir.Type.IsRawPointer && rightHir.Type.IsRawPointer && ReferenceEquals(leftHir.Type.Pointee, rightHir.Type.Pointee))
        {
            // `*mut T` coerces to `*T` (§27.1).
            var shared = _types.PointerTo(leftHir.Type.Pointee!, isMutable: false);
            leftHir = leftHir.Type.IsMutablePointer ? new HirCast(leftHir, shared, leftHir.Span) : leftHir;
            rightHir = rightHir.Type.IsMutablePointer ? new HirCast(rightHir, shared, rightHir.Span) : rightHir;
        }
        if (!ReferenceEquals(leftHir.Type, rightHir.Type))
        {
            Error(DiagnosticCodes.InvalidOperandTypes, binary.Span,
                $"operator '{OperatorText(op)}' requires operands of the same type, found '{leftHir.Type}' and '{rightHir.Type}'");
            return ErrorResult(binary.Span);
        }
        if (!CheckOperatorApplies(op, leftHir.Type, binary.Span))
        {
            return ErrorResult(binary.Span);
        }
        var resultType = IsComparison(op) ? _types.Bool : leftHir.Type;
        return new HirBinary(op, leftHir, rightHir, resultType, binary.Span);
    }

    /// <summary>A shift amount: any integer type; a flexible literal is typed as u32 (type-system.md §6).</summary>
    private HirExpr CheckShiftAmount(Expr amount)
    {
        if (IsFlexible(amount))
        {
            return CheckExpr(amount, _types.U32);
        }
        var hir = AutoDeref(Synth(amount, null));
        if (!hir.Type.IsBottom && !hir.Type.IsInteger)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, amount.Span, $"a shift amount must be an integer, found '{hir.Type}'");
            return ErrorResult(amount.Span);
        }
        return hir;
    }

    /// <summary>Reports N0207 unless <paramref name="op"/> accepts operands of <paramref name="type"/> (type-system.md §6).</summary>
    private bool CheckOperatorApplies(BinaryOp op, NovaType type, TextSpan span)
    {
        var ok = op switch
        {
            _ when IsArithmetic(op) => type.IsInteger,
            _ when IsBitwise(op) || IsShift(op) => type.IsUnsignedInteger,
            BinaryOp.Equal or BinaryOp.NotEqual => type.IsInteger || type.Kind is TypeKind.Bool or TypeKind.Handle or TypeKind.RawPointer or TypeKind.FunctionPointer,
            _ when IsComparison(op) => type.IsInteger,
            _ => type.Kind == TypeKind.Bool,
        };
        if (!ok)
        {
            var hint = (IsBitwise(op) || IsShift(op)) && type.IsInteger
                ? "; bitwise operators and shifts require unsigned operands (reinterpret with truncate<uN>(x))"
                : "";
            Error(DiagnosticCodes.InvalidOperandTypes, span, $"operator '{OperatorText(op)}' cannot be applied to type '{type}'{hint}");
        }
        return ok;
    }

    private HirExpr SynthCast(CastExpr cast)
    {
        var target = ResolveType(cast.Type, allowVoid: false);
        if (target.IsBottom)
        {
            Synth(cast.Operand, null);
            return ErrorResult(cast.Span);
        }
        // `0 as *mut T`: a literal address (§27.2, §29.5).
        if ((target.IsRawPointer || target.IsFunctionPointer) && IsFlexible(cast.Operand))
        {
            var address = CheckExpr(cast.Operand, _types.USize);
            return address.Type.IsBottom ? address : new HirCast(address, target, cast.Span);
        }
        // `5 as u64`: a flexible literal simply takes the target type.
        if (IsFlexible(cast.Operand))
        {
            return CheckExpr(cast.Operand, target) with { Span = cast.Span };
        }

        var synthesized = Synth(cast.Operand, null);
        if (target.IsRawPointer || synthesized.Type.IsRawPointer || target.IsFunctionPointer || synthesized.Type.IsFunctionPointer)
        {
            return CheckPointerCast(synthesized, target, cast.Span);
        }
        var operand = AutoDeref(synthesized);
        if (operand.Type.IsBottom)
        {
            return operand;
        }
        if (!TypeTable.IsLosslessCast(operand.Type, target))
        {
            var hint = operand.Type.IsInteger && target.IsInteger ? $"; use truncate<{target}>(...) to convert with wrap-around" : "";
            Error(DiagnosticCodes.InvalidCast, cast.Span,
                $"cannot convert '{operand.Type}' to '{target}' with 'as': 'as' only allows conversions that never lose information{hint}");
            return ErrorResult(cast.Span);
        }
        return ReferenceEquals(operand.Type, target) ? operand with { Span = cast.Span } : new HirCast(operand, target, cast.Span);
    }

    /// <summary>The pointer conversions of type-system.md §27.2; all of them are safe.</summary>
    private HirExpr CheckPointerCast(HirExpr operand, NovaType target, TextSpan span)
    {
        if (operand.Type.IsBottom)
        {
            return operand;
        }
        var from = operand.Type;
        var ok = target.IsFunctionPointer || from.IsFunctionPointer
            // A function pointer converts only to and from usize: it is not a data pointer (§29.5).
            ? (target.IsFunctionPointer && ReferenceEquals(from, _types.USize)) || (from.IsFunctionPointer && ReferenceEquals(target, _types.USize))
            : target.IsRawPointer
            ? from.IsRawPointer
              || ReferenceEquals(from, _types.USize)
              || (from.IsReference && !from.IsSlice && !from.IsStringView && ReferenceEquals(from.Pointee, target.Pointee)
                  && (from.IsMutableReference || !target.IsMutablePointer))
            : from.IsRawPointer && ReferenceEquals(target, _types.USize);
        if (!ok)
        {
            var hint = from.IsReference && target.IsMutablePointer && !from.IsMutableReference
                ? "; a '*mut' pointer needs a 'borrow mut' reference"
                : from.IsSlice || from.IsStringView ? "; use '.ptr' for a slice's address" : "";
            Error(DiagnosticCodes.InvalidCast, span, $"cannot convert '{from}' to '{target}' with 'as'{hint} (type-system.md §27.2)");
            return ErrorResult(span);
        }
        return ReferenceEquals(from, target) ? operand with { Span = span } : new HirCast(operand, target, span);
    }

    /// <summary>A method on a raw pointer (type-system.md §27.2–§27.3).</summary>
    private HirExpr CheckPointerMethod(CallExpr call, MemberExpr member, HirExpr pointer)
    {
        var name = Name(member.Member.Name);
        var type = pointer.Type;
        var pointee = type.Pointee!;
        (BuiltinFunction Function, NovaType? Argument)? signature = name switch
        {
            "read" => (BuiltinFunction.PtrRead, null),
            "write" => (BuiltinFunction.PtrWrite, pointee),
            "readVolatile" => (BuiltinFunction.PtrReadVolatile, null),
            "writeVolatile" => (BuiltinFunction.PtrWriteVolatile, pointee),
            "add" => (BuiltinFunction.PtrAdd, _types.USize),
            "sub" => (BuiltinFunction.PtrSub, _types.USize),
            "offset" => (BuiltinFunction.PtrOffset, _types.ISize),
            "isNull" => (BuiltinFunction.PtrIsNull, null),
            _ => null,
        };
        if (signature is not var (function, argumentType))
        {
            Error(DiagnosticCodes.NotAStruct, member.Member.Span,
                $"'{type}' has no method '{name}'; raw pointers have read, write, readVolatile, writeVolatile, add, sub, offset and isNull (type-system.md §27, §29.1)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var expectedCount = argumentType is null ? 0 : 1;
        if (call.Arguments.Length != expectedCount)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, $"'{name}' takes {expectedCount} argument(s) but {call.Arguments.Length} were supplied");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (function == BuiltinFunction.PtrIsNull)
        {
            return new HirBuiltinCall(function, [pointer], _types.Bool, call.Span);
        }
        var unsafeOk = RequireUnsafe(call.Span, $"'{name}' through a raw pointer");
        if (pointee.Kind is TypeKind.Void or TypeKind.CChar)
        {
            Error(DiagnosticCodes.InvalidPointerOperation, call.Span, $"'{name}' needs a typed pointer, but '{type}' points to '{pointee}'; cast it first (type-system.md §27.3)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (function is BuiltinFunction.PtrRead or BuiltinFunction.PtrWrite or BuiltinFunction.PtrReadVolatile or BuiltinFunction.PtrWriteVolatile
            && !TypeTable.IsCopy(pointee))
        {
            Error(DiagnosticCodes.InvalidPointerOperation, call.Span, $"'{name}' through a raw pointer needs a Copy type, but '{pointee}' is not (type-system.md §27.3)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (function is BuiltinFunction.PtrWrite or BuiltinFunction.PtrWriteVolatile && !type.IsMutablePointer)
        {
            Error(DiagnosticCodes.InvalidPointerOperation, call.Span, $"cannot write through '{type}'; writing needs a '*mut {pointee}' (type-system.md §27.3)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var arguments = argumentType is null ? [pointer] : ImmutableArray.Create(pointer, CheckArgument(call.Arguments[0], argumentType));
        if (!unsafeOk || arguments.Any(a => a.Type.IsBottom))
        {
            return ErrorResult(call.Span);
        }
        var result = function switch
        {
            BuiltinFunction.PtrRead or BuiltinFunction.PtrReadVolatile => pointee,
            BuiltinFunction.PtrWrite or BuiltinFunction.PtrWriteVolatile => _types.Void,
            _ => type,
        };
        return new HirBuiltinCall(function, arguments, result, call.Span);
    }

    /// <summary><c>Ptr.toBorrow(p)</c>, <c>Ptr.toBorrowMut(p)</c>, <c>Slice.fromRaw(p, n)</c>, <c>SliceMut.fromRaw(p, n)</c> (§27.3).</summary>
    private HirExpr CheckRawConstructor(CallExpr call, Builtin owner, MemberExpr member)
    {
        var name = Name(member.Member.Name);
        var known = (owner, name) is (Builtin.Ptr, "toBorrow" or "toBorrowMut") or (Builtin.Slice or Builtin.SliceMut, "fromRaw");
        if (!known)
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, owner == Builtin.Ptr
                ? $"'Ptr' has no function '{name}'; it has 'toBorrow' and 'toBorrowMut' (type-system.md §27.3)"
                : $"'{owner}' has no function '{name}'; it has 'fromRaw' (type-system.md §27.3)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var isSlice = owner != Builtin.Ptr;
        var expectedCount = isSlice ? 2 : 1;
        if (call.Arguments.Length != expectedCount)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, $"'{owner}.{name}' takes {expectedCount} argument(s) but {call.Arguments.Length} were supplied");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var unsafeOk = RequireUnsafe(call.Span, $"'{owner}.{name}'");
        var pointer = Synth(call.Arguments[0], null);
        var length = isSlice ? CheckExpr(call.Arguments[1], _types.USize) : null;
        if (pointer.Type.IsBottom || (length?.Type.IsBottom ?? false))
        {
            return ErrorResult(call.Span);
        }
        if (!pointer.Type.IsRawPointer)
        {
            Error(DiagnosticCodes.TypeMismatch, call.Arguments[0].Span, $"expected a raw pointer, found '{pointer.Type}'");
            return ErrorResult(call.Span);
        }
        var pointee = pointer.Type.Pointee!;
        var isMutable = name == "toBorrowMut" || owner == Builtin.SliceMut;
        if (pointee.Kind is TypeKind.Void or TypeKind.CChar)
        {
            Error(DiagnosticCodes.InvalidPointerOperation, call.Arguments[0].Span, $"'{owner}.{name}' needs a typed pointer, but '{pointer.Type}' points to '{pointee}'; cast it first");
            return ErrorResult(call.Span);
        }
        if (isMutable && !pointer.Type.IsMutablePointer)
        {
            Error(DiagnosticCodes.InvalidPointerOperation, call.Arguments[0].Span, $"'{owner}.{name}' gives mutable access, so it needs a '*mut {pointee}', not '{pointer.Type}'");
            return ErrorResult(call.Span);
        }
        if (isSlice && !CheckElement(pointee, -1, call.Arguments[0].Span))
        {
            return ErrorResult(call.Span);
        }
        if (!unsafeOk)
        {
            return ErrorResult(call.Span);
        }
        return isSlice
            ? new HirBuiltinCall(BuiltinFunction.SliceFromRaw, [pointer, length!], _types.SliceOf(pointee, isMutable), call.Span)
            : new HirCast(pointer, _types.ReferenceTo(pointee, isMutable), call.Span);
    }

    // ---- Calls ----

    private HirExpr SynthCall(CallExpr call, NovaType? expected = null)
    {
        switch (call.Callee)
        {
            case NameExpr name:
                switch (Lookup(name.Name.Name))
                {
                    case HirLocal { Type.IsFunctionPointer: true } local:
                        return CheckPointerCall(call, new HirLocalRef(local, name.Span));
                    case HirFunction function when name.TypeArguments.IsEmpty:
                        return CheckUserCall(call, function);
                    case GenericFunctionEntry generic:
                        return CheckGenericCall(call, name, generic);
                    case Builtin.Truncate:
                        return CheckTruncate(call, name);
                    case null:
                        Error(DiagnosticCodes.UndefinedName, name.Span, $"cannot find function '{Name(name.Name.Name)}' in this scope");
                        CheckArgumentsLoosely(call);
                        return ErrorResult(call.Span);
                    case HirFunction:
                        Error(DiagnosticCodes.Unsupported, name.Span, "generic arguments are only supported in calls to truncate<T>(...)");
                        return ErrorResult(call.Span);
                }
                break;
            case MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } target } member
                when Lookup(target.Name.Name) is Builtin builtin and (Builtin.Console or Builtin.Process or Builtin.Cpu or Builtin.Target):
                return CheckBuiltinMemberCall(call, builtin, target, member);
            case MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } target } member
                when Lookup(target.Name.Name) is Builtin builtin and (Builtin.Ptr or Builtin.Slice or Builtin.SliceMut):
                return CheckRawConstructor(call, builtin, member);
            case MemberExpr { Target: NameExpr target } member
                when Lookup(target.Name.Name) is StructEntry entry:
            {
                if (StaticOwner(entry, target) is not { } owner)
                {
                    CheckArgumentsLoosely(call);
                    return ErrorResult(call.Span);
                }
                return CheckStaticCall(call, owner, member);
            }
            case MemberExpr { Target: NameExpr target } member
                when Lookup(target.Name.Name) is EnumEntry enumEntry:
                return CheckEnumValue(enumEntry, member, call.Arguments, call.Span, expected, target.TypeArguments);
            case MemberExpr member:
                return CheckMethodCall(call, member);
        }

        Error(DiagnosticCodes.NotCallable, call.Callee.Span, "this expression cannot be called");
        CheckArgumentsLoosely(call);
        return ErrorResult(call.Span);
    }

    private void CheckArgumentsLoosely(CallExpr call)
    {
        foreach (var argument in call.Arguments)
        {
            Synth(argument is MutArgExpr marked ? marked.Operand : argument, null); // the call is already rejected
        }
    }

    /// <summary>
    /// Checks a call of a package function. For a method, <paramref name="receiver"/> is the
    /// already-checked `this` argument, which comes before the explicit arguments.
    /// </summary>
    private HirExpr CheckUserCall(CallExpr call, HirFunction function, HirExpr? receiver = null)
    {
        var parameters = receiver is null ? function.Parameters : function.Parameters[1..];
        var displayName = function.Owner is null ? Name(function.Name) : $"{function.Owner}.{Name(function.Name)}";
        if (function.Syntax.IsUnsafe)
        {
            RequireUnsafe(call.Span, $"calling unsafe function '{displayName}'");
        }
        else if (function.IsExtern)
        {
            RequireUnsafe(call.Span, $"calling C function '{displayName}'");
        }
        if (call.Arguments.Length != parameters.Length)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span,
                $"'{displayName}' takes {parameters.Length} argument(s) but {call.Arguments.Length} were supplied");
        }
        var arguments = ImmutableArray.CreateBuilder<HirExpr>(call.Arguments.Length + 1);
        if (receiver is not null)
        {
            arguments.Add(receiver);
        }
        for (var i = 0; i < call.Arguments.Length; i++)
        {
            arguments.Add(CheckArgument(call.Arguments[i], i < parameters.Length ? parameters[i].Type : null));
        }
        return call.Arguments.Length == parameters.Length
            ? new HirCall(function, arguments.ToImmutable(), function.ReturnType, call.Span)
            : ErrorResult(call.Span);
    }

    /// <summary><c>E.V</c> or <c>E.V(payload…)</c> (type-system.md §15.1).</summary>
    private HirExpr CheckEnumValue(EnumEntry entry, MemberExpr member, ImmutableArray<Expr> arguments, TextSpan span,
        NovaType? expected = null, ImmutableArray<TypeSyntax> typeArguments = default)
    {
        Used(entry);
        if (_profile == Profile.Freestanding && entry.Type == _ioError.Type)
        {
            HostedOnly(member.Target.Span, "'IoError'");
            foreach (var argument in arguments)
            {
                Synth(argument, null);
            }
            return ErrorResult(span);
        }
        if (!entry.Decl.TypeParameters.IsEmpty)
        {
            // `Maybe.Just(7)`, `Maybe<u8>.Nothing`, or a variant of the expected instance (§20.2).
            var templateVariant = entry.Type.Enum!.FindVariant(member.Member.Name);
            var pairs = templateVariant is null ? [] : templateVariant.Payload.Zip(arguments).Select(p => (p.First.Type, p.Second));
            var found = TypeArgumentsFor(Name(entry.Decl.Name.Name), entry.Type.Enum.TypeArguments,
                typeArguments.IsDefault ? [] : typeArguments,
                expected?.Enum is { } expectedEnum && expectedEnum.Syntax == entry.Decl ? expectedEnum.TypeArguments : null,
                pairs, span);
            if (found.IsDefault)
            {
                foreach (var argument in arguments)
                {
                    Synth(argument, null);
                }
                return ErrorResult(span);
            }
            entry = InstantiateEnum(entry, found, span);
        }
        else if (!typeArguments.IsDefaultOrEmpty)
        {
            Error(DiagnosticCodes.UnknownType, member.Target.Span, $"'{entry.Type}' is not generic; write it without type arguments");
        }
        if (entry.Type.Enum!.FindVariant(member.Member.Name) is not { } variant)
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{entry.Type}' has no variant named '{Name(member.Member.Name)}'");
            foreach (var argument in arguments)
            {
                Synth(argument, null);
            }
            return ErrorResult(span);
        }
        if (arguments.Length != variant.Payload.Count)
        {
            Error(DiagnosticCodes.ArgumentCount, span,
                $"'{entry.Type}.{Name(variant.Name)}' has {variant.Payload.Count} payload value(s) but {arguments.Length} were given");
            foreach (var argument in arguments)
            {
                Synth(argument, null);
            }
            return ErrorResult(span);
        }
        var payload = ImmutableArray.CreateBuilder<HirExpr>(arguments.Length);
        for (var i = 0; i < arguments.Length; i++)
        {
            payload.Add(CheckExpr(arguments[i], variant.Payload[i].Type));
        }
        return new HirEnumValue(variant, payload.MoveToImmutable(), entry.Type, span);
    }

    /// <summary>The struct whose static function <c>S.f(…)</c> or <c>S&lt;T&gt;.f(…)</c> names (§20.2); null after an error.</summary>
    private StructEntry? StaticOwner(StructEntry entry, NameExpr target)
    {
        if (entry.Decl.TypeParameters.IsEmpty)
        {
            if (!target.TypeArguments.IsEmpty)
            {
                Error(DiagnosticCodes.UnknownType, target.Span, $"'{entry.Type}' is not generic; write it without type arguments");
                return null;
            }
            return entry;
        }
        if (target.TypeArguments.IsEmpty)
        {
            Error(DiagnosticCodes.CannotInferTypeArguments, target.Span,
                $"'{Name(entry.Decl.Name.Name)}' is generic: write its type arguments, e.g. '{Name(entry.Decl.Name.Name)}<i32>.{"…"}'");
            return null;
        }
        var type = ResolveGenericItemType(new GenericType(new QualifiedName([target.Name], target.Name.Span), target.TypeArguments, target.Span));
        return type.IsBottom ? null : StructEntryFor(entry, type);
    }

    /// <summary>The entry of an instantiation (or of the template itself) of <paramref name="template"/>.</summary>
    private static StructEntry StructEntryFor(StructEntry template, NovaType type) =>
        template.Type == type ? template : template.Instances[TypeKey(type.Struct!.TypeArguments)];

    /// <summary><c>f(args)</c> or <c>f&lt;T&gt;(args)</c> for a generic function (§20.2): infer, instantiate, check.</summary>
    private HirExpr CheckGenericCall(CallExpr call, NameExpr name, GenericFunctionEntry entry)
    {
        var parameters = entry.Generic.Parameters;
        var found = TypeArgumentsFor(Name(entry.Decl.Name.Name), entry.Parameters, name.TypeArguments, null,
            call.Arguments.Zip(parameters).Select(p => (p.Second.Type, p.First)), call.Span);
        if (found.IsDefault || InstantiateFunction(entry, found, call.Span) is not { } instance)
        {
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        return CheckUserCall(call, instance);
    }

    /// <summary><c>S.f(args)</c>: a static function of struct S (type-system.md §12).</summary>
    private HirExpr CheckStaticCall(CallExpr call, StructEntry entry, MemberExpr member)
    {
        Used(entry);
        if (entry == _failingAllocator && Name(member.Member.Name) is "failAfter" or "reset")
        {
            var failAfter = Name(member.Member.Name) == "failAfter";
            if (call.Arguments.Length != (failAfter ? 1 : 0))
            {
                Error(DiagnosticCodes.ArgumentCount, call.Span, failAfter
                    ? "'FailingAllocator.failAfter' takes one 'u64'" : "'FailingAllocator.reset' takes no arguments");
                CheckArgumentsLoosely(call);
                return ErrorResult(call.Span);
            }
            return new HirBuiltinCall(failAfter ? BuiltinFunction.FailAfter : BuiltinFunction.FailReset,
                failAfter ? [CheckExpr(call.Arguments[0], _types.U64)] : [], _types.Void, call.Span);
        }
        var definition = entry.Type.Struct!;
        if (!definition.Functions.TryGetValue(member.Member.Name, out var function))
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span,
                $"'{entry.Type}' has no function named '{Name(member.Member.Name)}'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (function.IsMethod)
        {
            Error(DiagnosticCodes.MethodCallKind, member.Span,
                $"'{entry.Type}.{Name(function.Name)}' is a method; call it on a value: value.{Name(function.Name)}(...)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        return CheckUserCall(call, function);
    }

    /// <summary>
    /// <c>Vector&lt;T&gt;.create()</c>, <c>Vector&lt;T&gt;.withCapacity(n)</c> and the <c>String</c> forms (§23). Without
    /// type arguments, a vector's type comes from the expected type; a string's defaults to <c>String</c>.
    /// </summary>
    private HirExpr CheckVectorCreate(CallExpr call, MemberExpr member, NameExpr name, bool isString, NovaType? expected)
    {
        var kind = isString ? "String" : "Vector";
        var text = Name(member.Member.Name);
        if (text is not ("create" or "withCapacity"))
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{kind}' has no function '{text}'; use '{kind}.create()' or '{kind}.withCapacity(n)'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var withCapacity = text == "withCapacity";
        if (call.Arguments.Length != (withCapacity ? 1 : 0))
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, withCapacity
                ? $"'{kind}.withCapacity' takes the number of elements: '{kind}.withCapacity(n)'"
                : $"'{kind}.create' takes no arguments: '{kind}.create()'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var capacity = withCapacity ? CheckExpr(call.Arguments[0], _types.USize) : null;
        NovaType? vector;
        if (!name.TypeArguments.IsEmpty)
        {
            if (name.TypeArguments.Length > (isString ? 1 : 2))
            {
                Error(DiagnosticCodes.UnknownType, name.Span, isString
                    ? "'String' takes at most one type argument: 'String<A>'"
                    : "'Vector' takes one or two type arguments: 'Vector<T>' or 'Vector<T, A>'");
                return ErrorResult(call.Span);
            }
            vector = ResolveVectorType(isString, name.TypeArguments, name.Span);
        }
        else
        {
            var hint = withCapacity ? expected?.Enum?.ResultTypes?.Ok : expected;
            vector = hint is { IsVector: true } && hint.IsString == isString ? hint
                : isString ? ResolveVectorType(isString: true, [], name.Span)
                : null;
        }
        if (vector is null)
        {
            if (expected is null || !expected.IsBottom)
            {
                Error(DiagnosticCodes.ResultTypeUnknown, call.Span,
                    $"the element type of 'Vector.{text}(...)' is unknown here; write 'Vector<T>.{text}(...)' or give the variable a type");
            }
            return ErrorResult(call.Span);
        }
        if (vector.IsBottom || capacity is { Type.IsBottom: true })
        {
            return ErrorResult(call.Span);
        }
        return withCapacity
            ? new HirBuiltinCall(BuiltinFunction.VectorWithCapacity, [capacity!], ResultOf(vector, Used(_allocError).Type), call.Span)
            : new HirBuiltinCall(BuiltinFunction.VectorCreate, [], vector, call.Span);
    }

    /// <summary><c>SlotMap&lt;T&gt;.create()</c> / <c>StableVector&lt;T&gt;.create()</c> (§24): type arguments as for vectors.</summary>
    private HirExpr CheckContainerCreate(CallExpr call, MemberExpr member, NameExpr name, TypeKind kind, NovaType? expected)
    {
        var kindName = kind == TypeKind.SlotMap ? "SlotMap" : "StableVector";
        if (Name(member.Member.Name) != "create" || call.Arguments.Length != 0)
        {
            Error(Name(member.Member.Name) != "create" ? DiagnosticCodes.UndefinedName : DiagnosticCodes.ArgumentCount, call.Span,
                $"a '{kindName}' is made with '{kindName}<T>.create()'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        NovaType? container;
        if (!name.TypeArguments.IsEmpty)
        {
            if (name.TypeArguments.Length > 2)
            {
                Error(DiagnosticCodes.UnknownType, name.Span, $"'{kindName}' takes one or two type arguments: '{kindName}<T>' or '{kindName}<T, A>'");
                return ErrorResult(call.Span);
            }
            container = ResolveContainerType(kind, name.TypeArguments);
        }
        else
        {
            container = expected?.Kind == kind ? expected : null;
        }
        if (container is null)
        {
            if (expected is null || !expected.IsBottom)
            {
                Error(DiagnosticCodes.ResultTypeUnknown, call.Span,
                    $"the element type of '{kindName}.create()' is unknown here; write '{kindName}<T>.create()' or give the variable a type");
            }
            return ErrorResult(call.Span);
        }
        return container.IsBottom ? ErrorResult(call.Span) : new HirBuiltinCall(BuiltinFunction.VectorCreate, [], container, call.Span);
    }

    private NovaType IoResult(NovaType ok) => ResultOf(ok, Used(_ioError).Type);

    /// <summary><c>File.open</c>, <c>File.create</c>, <c>File.remove</c>, <c>TcpListener.bind</c>, <c>TcpStream.connect</c> (§26).</summary>
    private HirExpr CheckResourceFunction(CallExpr call, MemberExpr member, Builtin owner)
    {
        var text = Name(member.Member.Name);
        (BuiltinFunction Function, string Usage, NovaType Result)? signature = (owner, text) switch
        {
            (Builtin.File, "open") => (BuiltinFunction.FileOpen, "File.open(path)", IoResult(_types.File)),
            (Builtin.File, "create") => (BuiltinFunction.FileCreate, "File.create(path)", IoResult(_types.File)),
            (Builtin.File, "remove") => (BuiltinFunction.FileRemove, "File.remove(path)", IoResult(_types.Void)),
            (Builtin.TcpListener, "bind") => (BuiltinFunction.TcpListen, "TcpListener.bind(address, port)", IoResult(_types.TcpListener)),
            (Builtin.TcpStream, "connect") => (BuiltinFunction.TcpConnect, "TcpStream.connect(address, port)", IoResult(_types.TcpStream)),
            _ => null,
        };
        if (signature is not { } sig)
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{owner}' has no function '{text}'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var withPort = sig.Function is BuiltinFunction.TcpListen or BuiltinFunction.TcpConnect;
        if (call.Arguments.Length != (withPort ? 2 : 1))
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, $"write '{sig.Usage}'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var arguments = ImmutableArray.CreateBuilder<HirExpr>();
        arguments.Add(CheckArgument(call.Arguments[0], _types.StringView));
        if (withPort)
        {
            arguments.Add(CheckExpr(call.Arguments[1], _types.U16));
        }
        return arguments.Any(a => a.Type.IsBottom) ? ErrorResult(call.Span) : new HirBuiltinCall(sig.Function, arguments.ToImmutable(), sig.Result, call.Span);
    }

    /// <summary>Methods of a file or socket place (§26.3, §26.4).</summary>
    private HirExpr CheckResourceMethod(CallExpr call, MemberExpr member, HirExpr resource)
    {
        var kind = resource.Type.Resource;
        var text = Name(member.Member.Name);
        (BuiltinFunction Function, int Arity, bool Mutable, NovaType Result)? signature = (kind, text) switch
        {
            (ResourceKind.File or ResourceKind.TcpStream, "read") => (BuiltinFunction.ResourceRead, 1, true, IoResult(_types.USize)),
            (ResourceKind.File or ResourceKind.TcpStream, "write") => (BuiltinFunction.ResourceWrite, 1, true, IoResult(_types.Void)),
            (ResourceKind.TcpStream, "shutdownWrite") => (BuiltinFunction.TcpShutdownWrite, 0, true, IoResult(_types.Void)),
            (ResourceKind.TcpListener, "accept") => (BuiltinFunction.TcpAccept, 0, false, IoResult(_types.TcpStream)),
            (ResourceKind.TcpListener, "port") => (BuiltinFunction.TcpPort, 0, false, IoResult(_types.U16)),
            _ => null,
        };
        if (signature is not { } sig)
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{resource.Type}' has no method '{text}'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (call.Arguments.Length != sig.Arity)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, sig.Arity == 0 ? $"'{text}' takes no arguments" : $"'{text}' takes one argument");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var argument = sig.Function switch
        {
            BuiltinFunction.ResourceRead => CheckArgument(call.Arguments[0], _types.SliceOf(_types.U8, isMutable: true)),
            BuiltinFunction.ResourceWrite => CheckArgument(call.Arguments[0], _types.SliceOf(_types.U8, isMutable: false)),
            _ => null,
        };
        var receiver = BorrowPlace(resource, sig.Mutable, member.Target.Span);
        if (receiver.Type.IsBottom || argument is { Type.IsBottom: true })
        {
            return ErrorResult(call.Span);
        }
        return new HirBuiltinCall(sig.Function, argument is null ? [receiver] : [receiver, argument], sig.Result, call.Span);
    }

    /// <summary>
    /// A method of a slot map (§24.1) or stable vector (§24.2) place. <c>insert</c>, <c>getMut</c> and <c>remove</c>
    /// take <c>borrow mut this</c>; <c>get</c>, <c>contains</c> and a stable vector's <c>push</c> take <c>borrow this</c>.
    /// </summary>
    private HirExpr CheckContainerMethod(CallExpr call, MemberExpr member, HirExpr container)
    {
        var type = container.Type;
        var isMap = type.Kind == TypeKind.SlotMap;
        var element = isMap ? type.Element! : type.Pointee!.Element!;
        var text = Name(member.Member.Name);
        var methods = isMap
            ? new Dictionary<string, int> { ["insert"] = 1, ["get"] = 1, ["getMut"] = 1, ["remove"] = 1, ["contains"] = 1 }
            : new Dictionary<string, int> { ["push"] = 1 };
        if (!methods.TryGetValue(text, out var arity))
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{type}' has no method '{text}'; it has {string.Join(", ", methods.Keys.Select(k => $"'{k}'"))}");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (call.Arguments.Length != arity)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, $"'{text}' takes one argument");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        // The argument first: for `insert`, two-phase (B5).
        var argument = text is "insert" or "push" ? CheckExpr(call.Arguments[0], element) : CheckExpr(call.Arguments[0], _types.HandleOf(element));
        var isMutable = text is "insert" or "getMut" or "remove";
        var receiver = BorrowPlace(container, isMutable, member.Target.Span);
        if (receiver.Type.IsBottom || argument.Type.IsBottom)
        {
            return ErrorResult(call.Span);
        }
        var allocError = Used(_allocError).Type;
        return text switch
        {
            "push" => new HirBuiltinCall(BuiltinFunction.StableVectorPush, [receiver, argument], ResultOf(_types.USize, allocError), call.Span),
            "insert" => new HirBuiltinCall(BuiltinFunction.SlotMapInsert, [receiver, argument], ResultOf(_types.HandleOf(element), allocError), call.Span),
            "get" => new HirBuiltinCall(BuiltinFunction.SlotMapGet, [receiver, argument], OptionOf(_types.ReferenceTo(element, isMutable: false)), call.Span),
            "getMut" => new HirBuiltinCall(BuiltinFunction.SlotMapGet, [receiver, argument], OptionOf(_types.ReferenceTo(element, isMutable: true)), call.Span),
            "remove" => new HirBuiltinCall(BuiltinFunction.SlotMapRemove, [receiver, argument], OptionOf(element), call.Span),
            _ => new HirBuiltinCall(BuiltinFunction.SlotMapContains, [receiver, argument], _types.Bool, call.Span),
        };
    }

    /// <summary>
    /// A method of a vector or string place (§23): <c>push</c>, <c>pop</c>, <c>clear</c>, <c>append</c> take
    /// <c>borrow mut this</c>; <c>view</c> and <c>viewMut</c> borrow the elements; <c>bytes</c> reads a string's text.
    /// </summary>
    private HirExpr CheckVectorMethod(CallExpr call, MemberExpr member, HirExpr vector)
    {
        var type = vector.Type;
        var text = Name(member.Member.Name);
        var methods = type.IsString
            ? new Dictionary<string, int> { ["append"] = 1, ["clear"] = 0, ["view"] = 0, ["bytes"] = 0 }
            : new Dictionary<string, int> { ["push"] = 1, ["pop"] = 0, ["clear"] = 0, ["view"] = 0, ["viewMut"] = 0 };
        if (!methods.TryGetValue(text, out var arity))
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{type}' has no method '{text}'; it has {string.Join(", ", methods.Keys.Select(k => $"'{k}'"))}");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (call.Arguments.Length != arity)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, arity == 0 ? $"'{text}' takes no arguments" : $"'{text}' takes one argument");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (text is "view" or "bytes" or "viewMut")
        {
            var view = BorrowPlace(new HirDeref(vector, vector.Span), isMutable: text == "viewMut", call.Span);
            return text == "bytes" && !view.Type.IsBottom ? new HirCast(view, _types.SliceOf(_types.U8, isMutable: false), call.Span) : view;
        }
        // Two-phase (B5): the argument is checked (and later evaluated) before the receiver's mutable loan.
        var argument = text switch
        {
            "push" => CheckExpr(call.Arguments[0], type.Pointee!.Element!),
            "append" => CheckArgument(call.Arguments[0], _types.StringView),
            _ => null,
        };
        var receiver = BorrowPlace(vector, isMutable: true, member.Target.Span);
        if (receiver.Type.IsBottom || argument is { Type.IsBottom: true })
        {
            return ErrorResult(call.Span);
        }
        var voidResult = ResultOf(_types.Void, Used(_allocError).Type);
        return text switch
        {
            "push" => new HirBuiltinCall(BuiltinFunction.VectorPush, [receiver, argument!], voidResult, call.Span),
            "append" => new HirBuiltinCall(BuiltinFunction.StringAppend, [receiver, argument!], voidResult, call.Span),
            "pop" => new HirBuiltinCall(BuiltinFunction.VectorPop, [receiver], OptionOf(type.Pointee!.Element!), call.Span),
            _ => new HirBuiltinCall(BuiltinFunction.VectorClear, [receiver], _types.Void, call.Span),
        };
    }

    /// <summary><c>s.bytes()</c> (§22.2): the view's bytes as a <c>Slice&lt;u8&gt;</c>, with the view's loans.</summary>
    private HirExpr CheckViewMethod(CallExpr call, MemberExpr member, HirExpr view)
    {
        if (Name(member.Member.Name) != "bytes")
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span,
                $"'StringView' has no method '{Name(member.Member.Name)}'; it has 'bytes()' and 'length'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (call.Arguments.Length != 0)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, "'bytes' takes no arguments: 's.bytes()'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        return new HirCast(view, _types.SliceOf(_types.U8, isMutable: false), call.Span);
    }

    /// <summary><c>e.m(args)</c>: a method call; <c>e</c> is evaluated first and passed as `this`.</summary>
    private HirExpr CheckMethodCall(CallExpr call, MemberExpr member)
    {
        var receiver = Synth(member.Target, null);
        if (receiver.Type.IsBottom)
        {
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (AutoDeref(receiver) is { Type.IsRawPointer: true } pointer)
        {
            return CheckPointerMethod(call, member, pointer);
        }
        if (AutoDeref(receiver) is { Type.IsFunctionPointer: true } code)
        {
            if (Name(member.Member.Name) != "isNull" || !call.Arguments.IsEmpty)
            {
                Error(DiagnosticCodes.NotAStruct, member.Member.Span, $"'{code.Type}' has only 'isNull()'; call it as f(…) (type-system.md §29.5)");
                CheckArgumentsLoosely(call);
                return ErrorResult(call.Span);
            }
            return new HirBuiltinCall(BuiltinFunction.PtrIsNull, [code], _types.Bool, call.Span);
        }
        if (AutoDeref(receiver) is { Type.Struct: { } holder } owner && holder.FindField(member.Member.Name) is { Type.IsFunctionPointer: true } callback
            && FindMethod(owner.Type, member.Member.Name, member.Member.Span) is null)
        {
            return CheckPointerCall(call, new HirFieldAccess(owner, callback, member.Span)); // a callback stored in a field
        }
        var vector = receiver;
        while (vector.Type.IsReference)
        {
            vector = new HirDeref(vector, vector.Span);
        }
        if (vector.Type.IsVector)
        {
            return CheckVectorMethod(call, member, vector);
        }
        if (vector.Type.Kind is TypeKind.SlotMap or TypeKind.StableVector)
        {
            return CheckContainerMethod(call, member, vector);
        }
        if (vector.Type.Kind == TypeKind.Resource)
        {
            return CheckResourceMethod(call, member, vector);
        }
        if (vector.Type == _ioError.Type && Name(member.Member.Name) == "message" && call.Arguments.IsEmpty)
        {
            // e.message() (§26.2): static text for the variant.
            return new HirBuiltinCall(BuiltinFunction.IoErrorMessage, [AutoDeref(receiver)], _types.StringView, call.Span);
        }
        if (receiver.Type.IsBox || receiver.Type.Pointee is { IsBox: true })
        {
            receiver = AutoDeref(receiver); // the method receives the box's contents (§18.2)
        }
        if (ViewOf(AutoDeref(receiver)) is { Type.IsStringView: true } view)
        {
            return CheckViewMethod(call, member, view);
        }
        var structType = receiver.Type.Pointee ?? receiver.Type;
        if (structType.Region == RegionKind.Pool)
        {
            return CheckPoolTake(call, member, receiver, structType);
        }
        if (structType.Kind == TypeKind.Region)
        {
            return CheckArenaMethod(call, member, receiver);
        }
        // Own methods, `impl` methods, or a type parameter's bound methods (§21).
        if (FindMethod(structType, member.Member.Name, member.Member.Span) is not { } function)
        {
            if (structType.Struct is { } definition)
            {
                Error(DiagnosticCodes.UndefinedName, member.Member.Span, definition.FindField(member.Member.Name) is not null
                    ? $"'{receiver.Type}.{Name(member.Member.Name)}' is a field, not a function"
                    : $"'{receiver.Type}' has no method named '{Name(member.Member.Name)}'");
            }
            else
            {
                Error(DiagnosticCodes.NotAStruct, member.Member.Span, structType.Kind == TypeKind.TypeParameter
                    ? $"type '{structType}' has no methods; '{Name(member.Member.Name)}' needs a bound that declares it ('{structType}: Interface')"
                    : $"type '{structType}' has no methods; '{Name(member.Member.Name)}' cannot be called on it");
            }
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (!function.IsMethod)
        {
            Error(DiagnosticCodes.MethodCallKind, member.Member.Span,
                $"'{structType}.{Name(function.Name)}' is a static function; call it as {structType}.{Name(function.Name)}(...)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }

        // The receiver is passed as `this` by value, by shared borrow, or by mutable borrow (type-system.md §13.2).
        var receiverArgument = function.ReceiverKind switch
        {
            ReceiverKind.Value => AutoDeref(receiver),
            ReceiverKind.Borrow => receiver.Type.IsReference
                ? Coerce(receiver, _types.ReferenceTo(structType, isMutable: false), member.Target.Span)
                : ToPlace(receiver) is { } place
                    ? new HirBorrow(false, place, _types.ReferenceTo(structType, isMutable: false), member.Target.Span)
                    : IsPathThroughReference(receiver, isMutable: false)
                    ? new HirBorrowPath(false, receiver, _types.ReferenceTo(structType, isMutable: false), member.Target.Span)
                    : new HirBorrowTemp(false, receiver, _types.ReferenceTo(structType, isMutable: false), member.Target.Span),
            _ => receiver.Type.IsMutableReference
                ? Coerce(receiver, receiver.Type, member.Target.Span)
                : receiver.Type.IsReference
                    ? BorrowMutThroughShared(member)
                    : BorrowPlace(receiver, isMutable: true, member.Target.Span),
        };
        if (receiverArgument.Type.IsBottom)
        {
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        return CheckUserCall(call, function, receiverArgument);
    }

    private HirError BorrowMutThroughShared(MemberExpr member)
    {
        Error(DiagnosticCodes.BorrowMutOfImmutable, member.Target.Span,
            $"'{Name(member.Member.Name)}' takes 'borrow mut this', but this value is behind a shared reference");
        return ErrorResult(member.Target.Span);
    }

    private HirExpr CheckTruncate(CallExpr call, NameExpr name)
    {
        if (name.TypeArguments.Length != 1)
        {
            Error(DiagnosticCodes.Unsupported, name.Span, "truncate needs exactly one type argument: truncate<T>(value)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var target = ResolveType(name.TypeArguments[0], allowVoid: false);
        if (!target.IsBottom && !target.IsInteger)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, name.TypeArguments[0].Span, $"truncate<T> requires an integer type, found '{target}'");
            target = _types.Error;
        }
        if (call.Arguments.Length != 1)
        {
            Error(DiagnosticCodes.ArgumentCount, call.Span, $"truncate takes 1 argument but {call.Arguments.Length} were supplied");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }

        var operand = AutoDeref(Synth(call.Arguments[0], null));
        if (!operand.Type.IsBottom && !operand.Type.IsInteger)
        {
            Error(DiagnosticCodes.InvalidOperandTypes, call.Arguments[0].Span, $"truncate requires an integer value, found '{operand.Type}'");
            return ErrorResult(call.Span);
        }
        return target.IsBottom || operand.Type.IsBottom ? ErrorResult(call.Span) : new HirTruncate(operand, target, call.Span);
    }

    private HirExpr CheckBuiltinMemberCall(CallExpr call, Builtin owner, NameExpr target, MemberExpr member)
    {
        var memberName = Name(member.Member.Name);
        (BuiltinFunction Function, int MinArgs, int MaxArgs)? signature = (owner, memberName) switch
        {
            (Builtin.Console, "write") => (BuiltinFunction.ConsoleWrite, 1, 1),
            (Builtin.Console, "writeLine") => (BuiltinFunction.ConsoleWriteLine, 0, 1),
            (Builtin.Process, "exit") => (BuiltinFunction.ProcessExit, 1, 1),
            (Builtin.Process, "argumentCount") => (BuiltinFunction.ArgumentCount, 0, 0),
            (Builtin.Process, "argument") => (BuiltinFunction.Argument, 1, 1),
            (Builtin.Process, "run") => (BuiltinFunction.ProcessRun, 2, 2),
            (Builtin.Console, "read") => (BuiltinFunction.ConsoleRead, 1, 1),
            (Builtin.Console, "writeBytes") => (BuiltinFunction.ConsoleWriteBytes, 1, 1),
            (Builtin.Console, "writeError") => (BuiltinFunction.ConsoleWriteError, 1, 1),
            (Builtin.Console, "writeErrorLine") => (BuiltinFunction.ConsoleWriteErrorLine, 0, 1),
            (Builtin.Cpu, "spinHint") => (BuiltinFunction.SpinHint, 0, 0),
            (Builtin.Target, "isX86_64") => (BuiltinFunction.TargetIsX8664, 0, 0),
            (Builtin.Target, "isAArch64") => (BuiltinFunction.TargetIsAArch64, 0, 0),
            (Builtin.Target, "pointerBits") => (BuiltinFunction.TargetPointerBits, 0, 0),
            _ => null,
        };
        if (signature is not { } sig)
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, $"'{Name(target.Name.Name)}' has no member '{memberName}'");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }

        var count = call.Arguments.Length;
        if (count < sig.MinArgs || count > sig.MaxArgs)
        {
            var expectedCount = sig.MinArgs == sig.MaxArgs ? $"{sig.MinArgs}" : $"{sig.MinArgs} or {sig.MaxArgs}";
            Error(DiagnosticCodes.ArgumentCount, call.Span,
                $"'{Name(target.Name.Name)}.{memberName}' takes {expectedCount} argument(s) but {count} were supplied");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }

        if (sig.Function == BuiltinFunction.ProcessExit)
        {
            var code = CheckExpr(call.Arguments[0], _types.I32);
            return new HirBuiltinCall(sig.Function, [code], _types.Never, call.Span);
        }
        switch (sig.Function)
        {
            case BuiltinFunction.SpinHint:
                return new HirBuiltinCall(sig.Function, [], _types.Void, call.Span);
            case BuiltinFunction.TargetIsX8664 or BuiltinFunction.TargetIsAArch64:
                return new HirBuiltinCall(sig.Function, [], _types.Bool, call.Span); // decided by the C compiler (§29.3)
            case BuiltinFunction.TargetPointerBits:
                return new HirIntConst(_types.Target.PointerBits, _types.U32, call.Span);
            case BuiltinFunction.ArgumentCount:
                return new HirBuiltinCall(sig.Function, [], _types.USize, call.Span);
            case BuiltinFunction.ProcessRun:
            {
                var program = CheckArgument(call.Arguments[0], _types.StringView);
                var views = _types.ReferenceTo(VectorType(_types.StringView, Used(_systemHeap).Type), isMutable: false);
                var argumentList = CheckArgument(call.Arguments[1], views);
                return program.Type.IsBottom || argumentList.Type.IsBottom
                    ? ErrorResult(call.Span)
                    : new HirBuiltinCall(sig.Function, [program, argumentList], IoResult(_types.I32), call.Span);
            }
            case BuiltinFunction.Argument:
                return new HirBuiltinCall(sig.Function, [CheckExpr(call.Arguments[0], _types.USize)], OptionOf(_types.StringView), call.Span);
            case BuiltinFunction.ConsoleRead:
                return new HirBuiltinCall(sig.Function, [CheckArgument(call.Arguments[0], _types.SliceOf(_types.U8, isMutable: true))],
                    IoResult(_types.USize), call.Span);
            case BuiltinFunction.ConsoleWriteBytes:
                return new HirBuiltinCall(sig.Function, [CheckArgument(call.Arguments[0], _types.SliceOf(_types.U8, isMutable: false))],
                    _types.Void, call.Span);
            case BuiltinFunction.ConsoleWriteError or BuiltinFunction.ConsoleWriteErrorLine:
                return new HirBuiltinCall(sig.Function, [.. call.Arguments.Select(a => CheckArgument(a, _types.StringView))], _types.Void, call.Span);
        }

        var arguments = ImmutableArray.CreateBuilder<HirExpr>(count);
        foreach (var argument in call.Arguments)
        {
            var value = PrintableView(AutoDeref(Synth(argument, null)));
            if (!value.Type.IsBottom && !value.Type.IsInteger && value.Type.Kind != TypeKind.Bool && !value.Type.IsStringView)
            {
                Error(DiagnosticCodes.InvalidOperandTypes, argument.Span,
                    $"'Console.{memberName}' can print integers, bools and string views, not '{value.Type}'");
                value = ErrorResult(argument.Span);
            }
            arguments.Add(value);
        }
        return new HirBuiltinCall(sig.Function, arguments.MoveToImmutable(), _types.Void, call.Span);
    }

    private static string OperatorText(BinaryOp op) => op switch
    {
        BinaryOp.LogicalOr => "||",
        BinaryOp.LogicalAnd => "&&",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        BinaryOp.GreaterEqual => ">=",
        BinaryOp.BitOr => "|",
        BinaryOp.BitXor => "^",
        BinaryOp.BitAnd => "&",
        BinaryOp.ShiftLeft => "<<",
        BinaryOp.ShiftRight => ">>",
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        _ => "%",
    };
}
