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
    private readonly Symbol _thisSymbol;

    // Context of the body or constant currently being checked.
    private SourceText _source = null!;
    private HirFunction? _function;
    private List<Dictionary<Symbol, HirLocal>> _scopes = [];
    private int _loopDepth;

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
        // Struct names first, so every signature and field can refer to any struct.
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items.OfType<StructDecl>())
            {
                DeclareStruct(item);
            }
        }
        foreach (var unit in _units)
        {
            _source = unit.Source;
            foreach (var item in unit.Items.Where(i => i is not StructDecl))
            {
                Declare(item);
            }
        }
        foreach (var entry in _structs)
        {
            ResolveStructMembers(entry);
        }
        CheckStructRecursion();

        foreach (var constant in _constants)
        {
            EvaluateConstant(constant);
        }

        foreach (var function in _functions)
        {
            CheckFunctionBody(function);
        }

        CheckMain();
        return new HirProgram([.. _functions], [.. _constants.Select(c => c.Hir)], [.. _structs.Select(s => s.Type)]);
    }

    private string Name(Symbol symbol) => _symbols.GetName(symbol);

    private void Error(string code, TextSpan span, string message) => _diagnostics.Error(code, _source, span, message);

    private HirError ErrorResult(TextSpan span) => new(_types.Error, span);

    // ---- Declarations ----

    private void Declare(Item item)
    {
        var name = item.Name.Name;
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
                if (type.Kind == TypeKind.Struct)
                {
                    Error(DiagnosticCodes.Unsupported, c.Type.Span, "constants of struct type are not supported yet");
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
        var returnType = fn.ReturnType is null ? _types.Void : ResolveType(fn.ReturnType, allowVoid: true);
        var hir = new HirFunction(_functions.Count, fn.Name.Name, returnType, _source, fn, owner);
        if (owner is not null && fn.Receiver is not null)
        {
            // `this` is an immutable by-value binding of the struct type (type-system.md §12).
            hir.AddLocal(_thisSymbol, owner, isMutable: false, isParameter: true, fn.Receiver.Span);
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
        _functions.Add(hir);
        return hir;
    }

    private void DeclareStruct(StructDecl decl)
    {
        var name = decl.Name.Name;
        var text = Name(name);
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

    private void ResolveStructMembers(StructEntry entry)
    {
        _source = entry.Source;
        var definition = entry.Type.Struct!;
        var memberNames = new HashSet<Symbol>();
        foreach (var field in entry.Decl.Fields)
        {
            var type = ResolveType(field.Type, allowVoid: false);
            if (!memberNames.Add(field.Name.Name))
            {
                Error(DiagnosticCodes.DuplicateMember, field.Name.Span,
                    $"'{entry.Type}' already has a member named '{Name(field.Name.Name)}'");
                continue;
            }
            definition.AddField(field.Name.Name, type, field.Name.Span);
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

    /// <summary>A struct may not contain itself by value, directly or indirectly (N0227).</summary>
    private void CheckStructRecursion()
    {
        var state = new Dictionary<NovaType, int>(); // 1 = on the DFS stack, 2 = finished
        var stack = new List<NovaType>();

        void Visit(NovaType type)
        {
            state[type] = 1;
            stack.Add(type);
            foreach (var field in type.Struct!.Fields.Where(f => f.Type.Kind == TypeKind.Struct))
            {
                switch (state.GetValueOrDefault(field.Type))
                {
                    case 0:
                        Visit(field.Type);
                        break;
                    case 1:
                        // Every struct on the cycle is recursive; report at the one that closes it.
                        foreach (var member in stack.Skip(stack.IndexOf(field.Type)))
                        {
                            member.Struct!.IsRecursive = true;
                        }
                        _source = type.Struct!.Source;
                        Error(DiagnosticCodes.RecursiveStruct, field.Span,
                            $"'{type}' contains '{field.Type}' by value, which contains '{type}': the struct would have infinite size");
                        break;
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[type] = 2;
        }

        foreach (var entry in _structs)
        {
            if (state.GetValueOrDefault(entry.Type) == 0)
            {
                Visit(entry.Type);
            }
        }
    }

    private NovaType ResolveType(TypeSyntax syntax, bool allowVoid)
    {
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
        var type = _types.Lookup(text)
            ?? (_items.GetValueOrDefault(named.Name.Parts[0].Name) as StructEntry)?.Type;
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

        var value = Fold(CheckExpr(entry.Decl.Value, entry.Hir.Type));

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
                Error(DiagnosticCodes.NotConstant, expr.Span, "constant expressions cannot contain calls");
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
            default:
                throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
        }
    }

    private void CheckInLoop(TextSpan span, string keyword)
    {
        if (_loopDepth == 0)
        {
            Error(DiagnosticCodes.LoopControlOutsideLoop, span, $"'{keyword}' can only be used inside 'while' or 'loop'");
        }
    }

    private HirExpr CheckExpressionStatement(Expr expr)
    {
        if (expr is not CallExpr)
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

        if (LookupLocal(decl.Name.Name) is not null)
        {
            Error(DiagnosticCodes.LocalRedeclared, decl.Name.Span,
                $"'{Name(decl.Name.Name)}' is already declared in this function; locals cannot be shadowed");
        }

        // Declared after the initializer is checked: `let x = x;` does not see the new `x`.
        var local = _function!.AddLocal(decl.Name.Name, type, decl.IsMutable, isParameter: false, decl.Name.Span);
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

    /// <summary>Resolves an assignment target to a place: a local or a field path rooted at one (type-system.md §12).</summary>
    private HirPlace? ResolveAssignmentTarget(Expr target)
    {
        var fieldNames = new List<Identifier>();
        var root = target;
        while (root is MemberExpr member)
        {
            fieldNames.Insert(0, member.Member);
            root = member.Target;
        }

        HirLocal local;
        string rootName;
        switch (root)
        {
            case ThisExpr thisExpr:
                if (LookupLocal(_thisSymbol) is not { } receiver)
                {
                    Error(DiagnosticCodes.ThisOutsideMethod, thisExpr.Span, "'this' can only be used inside a method");
                    return null;
                }
                local = receiver;
                rootName = "this";
                break;
            case NameExpr name:
                rootName = Name(name.Name.Name);
                switch (Lookup(name.Name.Name))
                {
                    case HirLocal found:
                        local = found;
                        break;
                    case null:
                        Error(DiagnosticCodes.UndefinedName, name.Span, $"cannot find '{rootName}' in this scope");
                        return null;
                    default:
                        Error(DiagnosticCodes.AssignToImmutable, target.Span, $"cannot assign to '{rootName}': it is not a variable");
                        return null;
                }
                break;
            default:
                Error(DiagnosticCodes.Unsupported, target.Span, "only variables and their fields can be assigned");
                return null;
        }

        if (!local.IsMutable)
        {
            var what = fieldNames.Count == 0 ? $"'{rootName}'" : $"a field of '{rootName}'";
            var why = local.Name == _thisSymbol ? "'this' is immutable in a by-value method"
                : local.IsParameter ? "parameters are immutable"
                : $"'{rootName}' was declared with 'let'; use 'var' for a mutable variable";
            Error(DiagnosticCodes.AssignToImmutable, target.Span, $"cannot assign to {what}: {why}");
            return null;
        }

        var type = local.Type;
        var path = ImmutableArray.CreateBuilder<StructField>(fieldNames.Count);
        foreach (var fieldName in fieldNames)
        {
            if (ResolveField(type, fieldName) is not { } field)
            {
                return null;
            }
            path.Add(field);
            type = field.Type;
        }
        return new HirPlace(local, path.MoveToImmutable(), type);
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
    private HirExpr CheckExpr(Expr expr, NovaType? expected)
    {
        var hir = Synth(expr, expected);
        if (expected is null || expected.Kind == TypeKind.Error || hir.Type.IsBottom || ReferenceEquals(hir.Type, expected))
        {
            return hir;
        }
        Error(DiagnosticCodes.TypeMismatch, expr.Span, $"expected '{expected}', found '{hir.Type}'");
        return ErrorResult(expr.Span);
    }

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
            case NameExpr name:
                return SynthName(name);
            case UnaryExpr unary:
                return SynthUnary(unary, expected);
            case BinaryExpr binary:
                return SynthBinary(binary, expected);
            case CastExpr cast:
                return SynthCast(cast);
            case CallExpr call:
                return SynthCall(call);
            case MemberExpr member:
                return SynthMember(member);
            case ErrorExpr:
                return ErrorResult(expr.Span);
            default:
                throw new InvalidOperationException($"unexpected expression {expr.GetType().Name}");
        }
    }

    private HirExpr SynthMember(MemberExpr member)
    {
        // `Console.writeLine` or `Point.origin` without a call.
        if (member.Target is NameExpr { TypeArguments.IsEmpty: true } target && Lookup(target.Name.Name) is Builtin or StructEntry)
        {
            Error(DiagnosticCodes.NotCallable, member.Span,
                $"'{Name(target.Name.Name)}.{Name(member.Member.Name)}' is not a value; call it with '(...)'");
            return ErrorResult(member.Span);
        }
        var value = Synth(member.Target, null);
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
            case StructEntry:
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

        var value = Synth(unary.Operand, IntegerHint(expected));
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
            var left = Synth(binary.Left, IntegerHint(expected));
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
            leftHir = Synth(binary.Left, null);
            rightHir = CheckExpr(binary.Right, IntegerHint(leftHir.Type));
        }
        else if (IsFlexible(binary.Left) && !IsFlexible(binary.Right))
        {
            rightHir = Synth(binary.Right, null);
            leftHir = CheckExpr(binary.Left, IntegerHint(rightHir.Type));
        }
        else
        {
            leftHir = Synth(binary.Left, null);
            rightHir = Synth(binary.Right, null);
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
        var hir = Synth(amount, null);
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

        var operand = Synth(cast.Operand, null);
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
            arguments.Add(CheckExpr(call.Arguments[i], i < parameters.Length ? parameters[i].Type : null));
        }
        return call.Arguments.Length == parameters.Length
            ? new HirCall(function, arguments.ToImmutable(), function.ReturnType, call.Span)
            : ErrorResult(call.Span);
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
        if (receiver.Type.Struct is not { } definition)
        {
            Error(DiagnosticCodes.NotAStruct, member.Member.Span,
                $"type '{receiver.Type}' has no methods; '{Name(member.Member.Name)}' cannot be called on it");
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
                $"'{receiver.Type}.{Name(function.Name)}' is a static function; call it as {receiver.Type}.{Name(function.Name)}(...)");
            CheckArgumentsLoosely(call);
            return ErrorResult(call.Span);
        }
        return CheckUserCall(call, function, receiver);
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

        var operand = Synth(call.Arguments[0], null);
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
            var value = Synth(argument, null);
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
