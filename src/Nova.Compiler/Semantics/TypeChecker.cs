using System.Collections.Immutable;
using System.Numerics;
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

    private sealed class StructEntry(StructDecl decl, SourceText source, NovaType type)
    {
        public StructDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public NovaType Type { get; } = type;
    }

    private sealed class EnumEntry(EnumDecl decl, SourceText source, NovaType type)
    {
        public EnumDecl Decl { get; } = decl;
        public SourceText Source { get; } = source;
        public NovaType Type { get; } = type;
    }

    private enum Builtin
    {
        Console,
        Process,
        Truncate,
    }

    private readonly IReadOnlyList<CompilationUnit> _units;
    private readonly SymbolTable _symbols;
    private readonly TypeTable _types;
    private readonly DiagnosticBag _diagnostics;

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
    private EnumEntry _allocError = null!;
    private bool _allocErrorUsed;
    private static readonly string[] s_reservedNames = ["Result", "Ok", "Err"];

    public TypeChecker(IReadOnlyList<CompilationUnit> units, SymbolTable symbols, TypeTable types, DiagnosticBag diagnostics)
    {
        _units = units;
        _symbols = symbols;
        _types = types;
        _diagnostics = diagnostics;
        _builtins = new Dictionary<Symbol, Builtin>
        {
            [symbols.Intern("Console")] = Builtin.Console,
            [symbols.Intern("Process")] = Builtin.Process,
            [symbols.Intern("truncate")] = Builtin.Truncate,
        };
        _thisSymbol = symbols.Intern("this");
    }

    public HirProgram Check()
    {
        DeclareAllocError();
        // Struct names first, so every signature and field can refer to any struct.
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
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items.Where(i => i is not (StructDecl or EnumDecl)))
            {
                Declare(item);
            }
        }
        foreach (var entry in _structs)
        {
            ResolveStructMembers(entry);
        }
        foreach (var entry in _enums)
        {
            ResolveEnumVariants(entry);
        }
        CheckStructRecursion();
        ComputeCopyAndDrop();
        _copyKnown = true;
        foreach (var (element, length, span, source) in _pendingElements)
        {
            _source = source;
            CheckElementCopy(element, length, span);
        }

        foreach (var constant in _constants)
        {
            EvaluateConstant(constant);
        }

        foreach (var function in _functions)
        {
            CheckFunctionBody(function);
        }

        CheckMain();
        // The built-in AllocError is emitted only if the program uses it.
        return new HirProgram([.. _functions], [.. _constants.Select(c => c.Hir)],
            [.. _structs.Select(s => s.Type), .. _enums.Where(e => e != _allocError || _allocErrorUsed).Select(e => e.Type)]);
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
    }

    /// <summary>Reports N0201 if <paramref name="name"/> is a built-in name that items cannot take (type-system.md §17.1).</summary>
    private bool IsReservedName(Identifier name)
    {
        var text = Name(name.Name);
        if (!s_reservedNames.Contains(text) && !(text == "AllocError" && _items.ContainsKey(name.Name)))
        {
            return false;
        }
        Error(DiagnosticCodes.DuplicateItem, name.Span, $"'{text}' is a built-in name");
        return true;
    }

    private void Declare(Item item)
    {
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
                else if (type.IsReference)
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
    private HirFunction DeclareFunction(FunctionDecl fn, NovaType? owner)
    {
        var returnType = fn.ReturnType is null ? _types.Void : ResolveType(fn.ReturnType, allowVoid: true, allowSources: true);
        var hir = new HirFunction(_functions.Count, fn.Name.Name, returnType, _source, fn, owner);
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
        if (returnType.IsReference)
        {
            hir.ReturnSources = ResolveReturnSources(fn, hir);
        }
        _functions.Add(hir);
        return hir;
    }

    /// <summary>The parameters a returned reference may be derived from (type-system.md §13.5, lifetimes.md §7).</summary>
    private ImmutableArray<int> ResolveReturnSources(FunctionDecl fn, HirFunction hir)
    {
        // A slice return type has no source list: it uses the default rule.
        var syntax = fn.ReturnType as ReferenceType;
        var referenceParameters = hir.Parameters.Where(p => p.Type.IsReference).ToList();
        if (syntax is not { HasSources: true })
        {
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
            var parameter = hir.Parameters.FirstOrDefault(p => p.Name == source.Name);
            if (parameter is null || !parameter.Type.IsReference)
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
        var entry = new StructEntry(decl, _source, TypeTable.CreateStruct(text, decl, _source));
        _items.Add(name, entry);
        _structs.Add(entry);
    }

    private void DeclareEnum(EnumDecl decl)
    {
        var name = decl.Name.Name;
        var text = Name(name);
        if (IsReservedName(decl.Name))
        {
            return;
        }
        if (_types.Lookup(text) is not null || _items.ContainsKey(name))
        {
            Error(DiagnosticCodes.DuplicateItem, decl.Name.Span, _types.Lookup(text) is not null
                ? $"'{text}' is the name of a built-in type"
                : $"'{text}' is already defined");
            return;
        }
        var entry = new EnumEntry(decl, _source, TypeTable.CreateEnum(text, decl, _source));
        _items.Add(name, entry);
        _enums.Add(entry);
    }

    private void ResolveEnumVariants(EnumEntry entry)
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
                if (type.IsReference)
                {
                    Error(DiagnosticCodes.ReferenceNotAllowed, variantDecl.Payload[i].Span,
                        "enum payloads cannot be references in v0 (they would need lifetime parameters)");
                    type = _types.Error;
                }
                variant.Payload.Add(new StructField(_symbols.Intern($"_{i}"), type, i, variantDecl.Payload[i].Span));
            }
            definition.Variants.Add(variant);
        }
    }

    private void ResolveStructMembers(StructEntry entry)
    {
        _source = entry.Source;
        var definition = entry.Type.Struct!;
        var memberNames = new HashSet<Symbol>();
        foreach (var field in entry.Decl.Fields)
        {
            var type = ResolveType(field.Type, allowVoid: false);
            if (type.IsReference)
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, field.Type.Span,
                    "struct fields cannot be references in v0 (they would need lifetime parameters)");
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
            definition.DropFunction = DeclareFunction(syntax, entry.Type);
        }
        foreach (var function in entry.Decl.Functions)
        {
            if (!memberNames.Add(function.Name.Name))
            {
                Error(DiagnosticCodes.DuplicateMember, function.Name.Span,
                    $"'{entry.Type}' already has a member named '{Name(function.Name.Name)}'");
                continue;
            }
            definition.Functions.Add(function.Name.Name, DeclareFunction(function, entry.Type));
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
        _ => [type],
    };

    private static bool IsAggregate(NovaType type) => type.Kind is TypeKind.Struct or TypeKind.Enum;

    /// <summary>An aggregate may not contain itself by value, directly or indirectly (N0227).</summary>
    private void CheckStructRecursion()
    {
        var state = new Dictionary<NovaType, int>(); // 1 = on the DFS stack, 2 = finished
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
        var done = new HashSet<NovaType>();
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
                definition.IsCopy = definition.DropFunction is null && contained.All(TypeTable.IsCopy);
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

    private NovaType ResolveType(TypeSyntax syntax, bool allowVoid, bool allowSources = false)
    {
        if (syntax is ReferenceType reference)
        {
            if (reference.HasSources && !allowSources)
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, reference.Span,
                    "a source list 'borrow(...)' is only allowed in a function's return type");
            }
            var pointee = ResolveType(reference.Pointee, allowVoid: false);
            if (pointee.IsReference)
            {
                Error(DiagnosticCodes.ReferenceNotAllowed, reference.Span, "references to references are not supported");
                return _types.Error;
            }
            return pointee.IsBottom ? _types.Error : _types.ReferenceTo(pointee, reference.IsMutable);
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
        if (syntax is GenericType generic)
        {
            var genericName = generic.Name.Parts.Length == 1 ? Name(generic.Name.Parts[0].Name) : "";
            if (genericName == "Result" && generic.Arguments.Length == 2)
            {
                var ok = ResolveType(generic.Arguments[0], allowVoid: true);
                var err = ResolveType(generic.Arguments[1], allowVoid: false);
                foreach (var (argument, argumentSyntax) in new[] { (ok, generic.Arguments[0]), (err, generic.Arguments[1]) })
                {
                    if (argument.IsReference)
                    {
                        Error(DiagnosticCodes.ReferenceNotAllowed, argumentSyntax.Span, "'Result' cannot hold a reference or slice in v0");
                        return _types.Error;
                    }
                }
                return ok.IsBottom || err.IsBottom ? _types.Error : ResultOf(ok, err);
            }
            if (genericName is not ("Slice" or "SliceMut") || generic.Arguments.Length != 1)
            {
                Error(DiagnosticCodes.UnknownType, generic.Span, genericName is "Slice" or "SliceMut"
                    ? $"'{genericName}' takes one type argument: '{genericName}<T>'"
                    : genericName == "Result" ? "'Result' takes two type arguments: 'Result<T, E>'"
                    : "only 'Slice<T>', 'SliceMut<T>' and 'Result<T, E>' take type arguments");
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

        var text = Name(named.Name.Parts[0].Name);
        var type = _types.Lookup(text) ?? _items.GetValueOrDefault(named.Name.Parts[0].Name) switch
        {
            StructEntry s => s.Type,
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
        return type;
    }

    private EnumEntry Used(EnumEntry entry)
    {
        _allocErrorUsed |= entry == _allocError;
        return entry;
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

    /// <summary>The operand of <c>e?</c> or <c>e!</c>: a value of a <c>Result</c> type (N0262).</summary>
    private HirExpr? CheckResultOperand(Expr operand, string text)
    {
        var value = Synth(operand, null);
        if (value.Type.IsBottom)
        {
            return null;
        }
        if (value.Type.Enum is not { ResultTypes: not null })
        {
            Error(DiagnosticCodes.NotAResult, operand.Span, value.Type.Pointee?.Enum is { ResultTypes: not null }
                ? $"'{text}' needs a 'Result' value, but this is a reference to one; '{text}' consumes the result"
                : $"'{text}' needs a 'Result<T, E>' value, found '{value.Type}'");
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
        var (ok, err) = operand.Type.Enum!.ResultTypes!.Value;
        var returnType = _function?.ReturnType;
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
        if (element.IsReference)
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
        if (!TypeTable.IsCopy(element))
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
    private static bool HasLayout(NovaType type) => Flatten(type).All(inner =>
        inner.Struct is not { IsRecursive: true } && inner.Enum is not { IsRecursive: true }
        && Contained(inner).All(c => HasLayout(c.Type)));

    private void CheckMain()
    {
        if (_items.GetValueOrDefault(_symbols.Intern("main")) is not HirFunction main)
        {
            return;
        }
        var okReturn = ReferenceEquals(main.ReturnType, _types.I32) || ReferenceEquals(main.ReturnType, _types.Void);
        if (!main.Parameters.IsEmpty || !okReturn)
        {
            _source = main.Source;
            Error(DiagnosticCodes.InvalidMain, main.Syntax.Name.Span,
                "'main' must take no parameters and return 'i32' or 'void'");
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
        _loopDepth = 0;
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

        FlowAnalysis.Analyze(function, _symbols, _types, _diagnostics);
        _function = null;
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
            start = AutoDeref(Synth(forStmt.Start, null));
            variableType = start.Type.Kind == TypeKind.Array ? start.Type.Element! : _types.Error;
            if (!start.Type.IsBottom && start.Type.Kind != TypeKind.Array)
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
                case VariantPattern pattern when ok:
                    kind = HirPatternKind.Variant;
                    variant = CheckVariantPattern(pattern, type, reference, bindings);
                    if (variant is not null && !seenVariants.Add(variant))
                    {
                        Error(DiagnosticCodes.UnreachableArm, pattern.Span, $"variant '{Name(variant.Name)}' is already matched by an earlier arm");
                    }
                    break;
            }
            var body = CheckBlock(arm.Body);
            _scopes.RemoveAt(_scopes.Count - 1);
            arms.Add(new HirArm(kind, variant, value, bindings.ToImmutable(), body, arm.Span));
        }

        if (ok && !wildcard)
        {
            var missing = type.Kind switch
            {
                TypeKind.Enum => type.Enum!.Variants.Where(v => !seenVariants.Contains(v)).Select(v => $"{type}.{Name(v.Name)}").ToList(),
                TypeKind.Bool => s_boolValues.Where(b => !seenValues.Contains(b ? 1 : 0)).Select(b => b ? "true" : "false").ToList(),
                _ => ["_"],
            };
            if (missing.Count > 0)
            {
                Error(DiagnosticCodes.NonExhaustiveMatch, match.Scrutinee.Span,
                    $"this match does not cover {string.Join(", ", missing)}; add the missing arms or a '_' arm");
            }
        }
        return ok ? new HirMatch(scrutinee, arms.ToImmutable(), match.Span) : new HirExprStmt(ErrorResult(match.Span), match.Span);
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
        else if (parts.Length != 2 || parts[0].Name != _symbols.Intern(type.Name))
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
            if (reference is null && !TypeTable.IsCopy(payloadType))
            {
                Error(DiagnosticCodes.NonCopyBinding, name.Span,
                    $"'{Name(name.Name)}' would copy a '{payloadType}', which is not Copy; match on 'borrow …' to bind it by reference");
            }
            if (LookupLocal(name.Name) is not null)
            {
                Error(DiagnosticCodes.LocalRedeclared, name.Span,
                    $"'{Name(name.Name)}' is already declared in this function; locals cannot be shadowed");
            }
            var bindingType = reference is null ? payloadType : _types.ReferenceTo(payloadType, reference.IsMutableReference);
            var local = _function!.AddLocal(name.Name, bindingType, isMutable: false, isParameter: false, name.Span);
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

        if (type.IsReference && decl.IsMutable)
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
        var local = _function!.AddLocal(decl.Name.Name, type, decl.IsMutable, isParameter: false, decl.Name.Span);
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
        if (place.Type.IsReference)
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
            if (projection.IsDeref)
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
        return place.Projections.Any(p => p.Kind == ProjectionKind.Index) ? $"an element of '{text}'"
            : hasField ? $"'{text}'" : place.Projections.IsEmpty ? $"'{text}'" : $"the value '{text}' refers to";
    }

    /// <summary>Reads through references until the value is not a reference (auto-deref, type-system.md §13.3).</summary>
    private static HirExpr AutoDeref(HirExpr expr)
    {
        while (expr.Type.IsReference)
        {
            expr = new HirDeref(expr, expr.Span);
        }
        return expr;
    }

    /// <summary>A `borrow p` / `borrow mut p` of the place a checked expression denotes (N0247, N0248).</summary>
    private HirExpr BorrowPlace(HirExpr value, bool isMutable, TextSpan span)
    {
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
        if (!expected.IsReference && hir.Type.IsReference && ReferenceEquals(hir.Type.Pointee, expected))
        {
            return new HirDeref(hir, span);
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
        Coerce(Synth(expr, expected?.Pointee ?? expected), expected, expr.Span);

    private static NovaType? IntegerHint(NovaType? expected) => expected is { IsInteger: true } ? expected : null;

    private HirExpr Synth(Expr expr, NovaType? expected)
    {
        switch (expr)
        {
            case IntLiteralExpr literal:
                return CheckIntLiteral(literal, negative: false, expected, literal.Span);
            case BoolLiteralExpr b:
                return new HirBoolConst(b.Value, _types.Bool, b.Span);
            case FloatLiteralExpr:
                Error(DiagnosticCodes.Unsupported, expr.Span, "floating-point numbers are not supported in M1");
                return ErrorResult(expr.Span);
            case CharLiteralExpr:
                Error(DiagnosticCodes.Unsupported, expr.Span, "'char' values are not supported in M1");
                return ErrorResult(expr.Span);
            case StringLiteralExpr:
                Error(DiagnosticCodes.Unsupported, expr.Span,
                    "string literals can only be passed directly to Console.write or Console.writeLine in M1");
                return ErrorResult(expr.Span);
            case ParenExpr paren:
                return Synth(paren.Inner, expected) with { Span = paren.Span };
            case ThisExpr:
                if (LookupLocal(_thisSymbol) is { } receiver)
                {
                    return new HirLocalRef(receiver, expr.Span);
                }
                Error(DiagnosticCodes.ThisOutsideMethod, expr.Span, "'this' can only be used inside a method");
                return ErrorResult(expr.Span);
            case StructLiteralExpr literal:
                return SynthStructLiteral(literal);
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
            case CallExpr call:
                return SynthCall(call);
            case TryExpr tryExpr:
                return CheckTry(tryExpr);
            case UnwrapExpr unwrap:
            {
                var operand = CheckResultOperand(unwrap.Operand, "!");
                return operand is null
                    ? ErrorResult(unwrap.Span)
                    : new HirUnwrap(operand, operand.Type.Enum!.ResultTypes!.Value.Ok, unwrap.Span);
            }
            case MemberExpr member:
                return SynthMember(member);
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
        var value = AutoDeref(Synth(element, null));
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
        if (isMutable && MutabilityError(place) is { } reason)
        {
            Error(DiagnosticCodes.BorrowMutOfImmutable, span, $"cannot borrow {DescribePlace(place)} as mutable: {reason}");
            return ErrorResult(span);
        }
        return new HirSlice(isMutable, place, low, high, _types.SliceOf(target.Type.Element!, isMutable), span);
    }

    private HirExpr SynthMember(MemberExpr member)
    {
        // `Shape.Empty`: a variant without payload.
        if (member.Target is NameExpr { TypeArguments.IsEmpty: true } enumName && Lookup(enumName.Name.Name) is EnumEntry entry)
        {
            return CheckEnumValue(entry, member, [], member.Span);
        }
        // `Console.writeLine` or `Point.origin` without a call.
        if (member.Target is NameExpr { TypeArguments.IsEmpty: true } target && Lookup(target.Name.Name) is Builtin or StructEntry)
        {
            Error(DiagnosticCodes.NotCallable, member.Span,
                $"'{Name(target.Name.Name)}.{Name(member.Member.Name)}' is not a value; call it with '(...)'");
            return ErrorResult(member.Span);
        }
        var value = AutoDeref(Synth(member.Target, null));
        if (value.Type.Kind == TypeKind.Array)
        {
            if (Name(member.Member.Name) != "length")
            {
                Error(DiagnosticCodes.UnknownField, member.Member.Span,
                    $"'{value.Type}' has no member '{Name(member.Member.Name)}'; arrays and slices only have 'length'");
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

    private HirExpr SynthStructLiteral(StructLiteralExpr literal)
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
            Error(DiagnosticCodes.Unsupported, name.Span, "generic arguments are only supported in calls to truncate<T>(...)");
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
            case HirFunction:
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
            BinaryOp.Equal or BinaryOp.NotEqual => type.IsInteger || type.Kind == TypeKind.Bool,
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
        // `5 as u64`: a flexible literal simply takes the target type.
        if (IsFlexible(cast.Operand))
        {
            return CheckExpr(cast.Operand, target) with { Span = cast.Span };
        }

        var operand = AutoDeref(Synth(cast.Operand, null));
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

    // ---- Calls ----

    private HirExpr SynthCall(CallExpr call)
    {
        switch (call.Callee)
        {
            case NameExpr name:
                switch (Lookup(name.Name.Name))
                {
                    case HirFunction function when name.TypeArguments.IsEmpty:
                        return CheckUserCall(call, function);
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
                when Lookup(target.Name.Name) is Builtin builtin and (Builtin.Console or Builtin.Process):
                return CheckBuiltinMemberCall(call, builtin, target, member);
            case MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } target } member
                when Lookup(target.Name.Name) is StructEntry entry:
                return CheckStaticCall(call, entry, member);
            case MemberExpr { Target: NameExpr { TypeArguments.IsEmpty: true } target } member
                when Lookup(target.Name.Name) is EnumEntry enumEntry:
                return CheckEnumValue(enumEntry, member, call.Arguments, call.Span);
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
            if (argument is not StringLiteralExpr)
            {
                Synth(argument, null);
            }
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
    private HirExpr CheckEnumValue(EnumEntry entry, MemberExpr member, ImmutableArray<Expr> arguments, TextSpan span)
    {
        Used(entry);
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

    /// <summary><c>S.f(args)</c>: a static function of struct S (type-system.md §12).</summary>
    private HirExpr CheckStaticCall(CallExpr call, StructEntry entry, MemberExpr member)
    {
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

    /// <summary><c>e.m(args)</c>: a method call; <c>e</c> is evaluated first and passed as `this`.</summary>
    private HirExpr CheckMethodCall(CallExpr call, MemberExpr member)
    {
        var receiver = Synth(member.Target, null);
        if (receiver.Type.IsBottom)
        {
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        var structType = receiver.Type.Pointee ?? receiver.Type;
        if (structType.Struct is not { } definition)
        {
            Error(DiagnosticCodes.NotAStruct, member.Member.Span,
                $"type '{structType}' has no methods; '{Name(member.Member.Name)}' cannot be called on it");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        if (!definition.Functions.TryGetValue(member.Member.Name, out var function))
        {
            Error(DiagnosticCodes.UndefinedName, member.Member.Span, definition.FindField(member.Member.Name) is not null
                ? $"'{receiver.Type}.{Name(member.Member.Name)}' is a field, not a function"
                : $"'{receiver.Type}' has no method named '{Name(member.Member.Name)}'");
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

        var arguments = ImmutableArray.CreateBuilder<HirExpr>(count);
        foreach (var argument in call.Arguments)
        {
            if (argument is StringLiteralExpr text)
            {
                arguments.Add(new HirStringConst(text.Value, _types.StringLiteral, text.Span));
                continue;
            }
            var value = AutoDeref(Synth(argument, null));
            if (!value.Type.IsBottom && !value.Type.IsInteger && value.Type.Kind != TypeKind.Bool)
            {
                Error(DiagnosticCodes.InvalidOperandTypes, argument.Span,
                    $"'Console.{memberName}' can print integers, bools and string literals, not '{value.Type}'");
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
