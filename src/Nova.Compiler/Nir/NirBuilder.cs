using System.Collections.Immutable;
using Nova.Compiler.Hir;
using Nova.Compiler.Semantics;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Nir;

/// <summary>
/// Lowers checked HIR to NIR (docs/intermediate-representation.md §3). Must only be given a
/// program without errors. <c>_current</c> is the block being filled, or null when the current
/// point is unreachable (after return/break/continue/exit), in which case lowering is skipped.
/// </summary>
public sealed class NirBuilder
{
    private readonly HirFunction _function;
    private readonly TypeTable _types;
    private readonly List<NirLocal> _locals = [];
    private readonly List<NirBlock> _blocks = [];
    private readonly Stack<LoopTargets> _loops = new();
    /// <summary>Open scopes; each entry is a local (ends with StorageDead) or a deferred statement (type-system.md §14.7).</summary>
    private readonly List<List<object>> _scopes = [];
    private List<NirLocal>? _statementTemps;
    /// <summary>
    /// Temporaries of statements still being lowered, innermost last, with the index of the scope they
    /// belong to. An early exit (return, break, continue, `?`) ends them with that scope.
    /// </summary>
    private readonly List<(List<NirLocal> Temps, int Scope)> _openTemps = [];
    private NirBlock? _current;
    private TextSpan _span;

    private sealed class LoopTargets(NirBlock head, NirBlock exit, int scopeDepth)
    {
        public NirBlock Head { get; } = head;
        public NirBlock Exit { get; } = exit;
        /// <summary>Number of open scopes at the loop; break/continue end the scopes above it.</summary>
        public int ScopeDepth { get; } = scopeDepth;
        public bool ExitUsed { get; set; }
    }

    private NirBuilder(HirFunction function, TypeTable types)
    {
        _function = function;
        _types = types;
    }

    public static NirProgram Build(HirProgram program, TypeTable types)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(types);
        return new NirProgram([.. program.Functions.Where(f => !f.IsExtern).Select(f => new NirBuilder(f, types).BuildFunction())], program.Structs,
            [.. program.Functions.Where(f => f.IsExtern)]) { Headers = program.Headers };
    }

    private NirFunction BuildFunction()
    {
        // HIR locals keep their ids; temporaries are appended after them.
        foreach (var local in _function.Locals)
        {
            _locals.Add(new NirLocal(local.Id, local.Type, local.Name) { IsUsing = local.IsUsing });
        }

        _current = NewBlock();
        // By-value parameters are owned by the function: the outermost scope, ended at every return.
        _scopes.Add([.. _locals.Take(_function.Parameters.Length)]);
        LowerStmt(_function.Body!);
        if (_current is not null)
        {
            // Flow analysis guarantees only void functions can fall off the end.
            EmitStorageDead(0);
            Terminate(_function.ReturnType.Kind == TypeKind.Void ? new NirReturn(null) : new NirUnreachable());
        }

        return new NirFunction(_function, [.. _locals], _function.Parameters.Length, ReachableBlocks());
    }

    private ImmutableArray<NirBlock> ReachableBlocks()
    {
        var reachable = new HashSet<NirBlock>();
        var work = new Stack<NirBlock>();
        work.Push(_blocks[0]);
        while (work.TryPop(out var block))
        {
            if (!reachable.Add(block))
            {
                continue;
            }
            foreach (var successor in block.Terminator!.Successors)
            {
                work.Push(successor);
            }
        }
        return [.. _blocks.Where(reachable.Contains)];
    }

    private NirBlock NewBlock()
    {
        var block = new NirBlock(_blocks.Count);
        _blocks.Add(block);
        return block;
    }

    private NirLocal NewTemp(NovaType type)
    {
        var local = new NirLocal(_locals.Count, type, null);
        _locals.Add(local);
        _statementTemps?.Add(local);
        return local;
    }

    private void Emit(NirStatement statement) => _current!.Statements.Add(statement with { Span = _span });

    private void Terminate(NirTerminator terminator)
    {
        _current!.Terminator = terminator with { Span = _span };
        _current = null;
    }

    /// <summary>
    /// Leaves the scopes at index <paramref name="depth"/> and above, innermost first: each local
    /// ends (StorageDead, where drop elaboration later destroys it) and each deferred statement runs,
    /// in reverse order of position (type-system.md §14.5, §14.7).
    /// </summary>
    private void EmitStorageDead(int depth)
    {
        var top = _scopes.Count - 1;
        for (var i = top; i >= depth; i--)
        {
            // Temporaries of unfinished statements in this scope end before its locals.
            for (var k = _openTemps.Count - 1; k >= 0 && _current is not null; k--)
            {
                if (_openTemps[k].Scope == i)
                {
                    var temps = _openTemps[k].Temps;
                    for (var t = temps.Count - 1; t >= 0; t--)
                    {
                        Emit(new NirStorageDead(temps[t]));
                    }
                }
            }
            var scope = _scopes[i];
            for (var j = scope.Count - 1; j >= 0 && _current is not null; j--)
            {
                if (scope[j] is NirLocal local)
                {
                    Emit(new NirStorageDead(local));
                }
                else
                {
                    var saved = _statementTemps;
                    LowerStmt(((HirDefer)scope[j]).Body);
                    _statementTemps = saved;
                }
            }
        }
    }

    /// <summary>
    /// Lowers one statement whose temporaries end with it (lifetimes.md §2): the temporaries get
    /// StorageDead at the end of the statement if it completes.
    /// </summary>
    private void WithStatementTemps(Action lower)
    {
        var saved = _statementTemps;
        _statementTemps = [];
        _openTemps.Add((_statementTemps, _scopes.Count - 1));
        lower();
        _openTemps.RemoveAt(_openTemps.Count - 1);
        if (_current is not null)
        {
            for (var i = _statementTemps.Count - 1; i >= 0; i--)
            {
                Emit(new NirStorageDead(_statementTemps[i]));
            }
        }
        _statementTemps = saved;
    }

    /// <summary>
    /// Returns from the function: the value goes into a local that no scope owns, then every open
    /// statement's temporaries and every scope end (drops and deferred statements), then the return.
    /// </summary>
    private void EmitReturn(NirRvalue? value)
    {
        NirOperand? result = null;
        if (value is not null)
        {
            var local = new NirLocal(_locals.Count, value.Type, null);
            _locals.Add(local);
            Emit(new NirAssign(new NirPlace(local), value));
            result = Read(new NirPlace(local));
        }
        EmitStorageDead(0);
        if (_current is not null)
        {
            Terminate(new NirReturn(result));
        }
    }

    // ---- Statements ----

    private void LowerStmt(HirStmt stmt)
    {
        if (_current is null)
        {
            return; // unreachable code is not lowered
        }

        _span = stmt.Span;
        switch (stmt)
        {
            case HirBlock block:
                _scopes.Add([]);
                foreach (var s in block.Statements)
                {
                    LowerStmt(s);
                }
                if (_current is not null)
                {
                    _span = new TextSpan(block.Span.End, 0);
                    EmitStorageDead(_scopes.Count - 1);
                }
                _scopes.RemoveAt(_scopes.Count - 1);
                break;
            case HirLocalDecl decl:
                _scopes[^1].Add(_locals[decl.Local.Id]);
                if (decl.Initializer is not null)
                {
                    WithStatementTemps(() => AssignRvalue(new NirPlace(_locals[decl.Local.Id]), decl.Initializer));
                }
                break;
            case HirAssign { Op: null } assign:
                WithStatementTemps(() =>
                {
                    // The target's indexes are evaluated (and checked) before the value.
                    if (LowerPlace(assign.Target) is { } target)
                    {
                        AssignRvalue(target, assign.Value);
                    }
                });
                break;
            case HirAssign assign:
                WithStatementTemps(() =>
                {
                    if (LowerPlace(assign.Target) is not { } target)
                    {
                        return;
                    }
                    var right = LowerOperand(assign.Value);
                    if (right is not null)
                    {
                        _span = assign.Span;
                        if (NeedsIndexRecheck(target, assign.Value))
                        {
                            RecheckVectorIndexes(target);
                        }
                        Emit(new NirAssign(target, new NirBinary(assign.Op!.Value, new NirPlaceOperand(target), right,
                            target.Type, _function.Source, assign.Span)));
                    }
                });
                break;
            case HirIf ifStmt:
                LowerIf(ifStmt);
                break;
            case HirWhile whileStmt:
            {
                var head = NewBlock();
                Terminate(new NirGoto(head));
                _current = head;
                var condition = LowerOperand(whileStmt.Condition);
                if (condition is null)
                {
                    break;
                }
                var body = NewBlock();
                var exit = NewBlock();
                Terminate(new NirBranch(condition, body, exit));
                LowerLoopBody(whileStmt.Body, head, exit, body);
                _current = exit;
                break;
            }
            case HirFor forStmt:
                LowerFor(forStmt);
                break;
            case HirLoop loop:
            {
                var head = NewBlock();
                Terminate(new NirGoto(head));
                var exit = NewBlock();
                var targets = LowerLoopBody(loop.Body, head, exit, head);
                _current = targets.ExitUsed ? exit : null;
                break;
            }
            case HirBreak:
            {
                var loop = _loops.Peek();
                loop.ExitUsed = true;
                EmitStorageDead(loop.ScopeDepth);
                Terminate(new NirGoto(loop.Exit));
                break;
            }
            case HirContinue:
            {
                var loop = _loops.Peek();
                EmitStorageDead(loop.ScopeDepth);
                Terminate(new NirGoto(loop.Head));
                break;
            }
            case HirReturn ret:
            {
                // The value is computed first and moved into a local that no scope owns; then the
                // statement's temporaries end, then every scope (drops and defers), then the return.
                var saved = _statementTemps;
                _statementTemps = [];
                _openTemps.Add((_statementTemps, _scopes.Count - 1));
                var computed = ret.Value is null ? null : LowerOperand(ret.Value);
                if (ret.Value is null || computed is not null)
                {
                    _span = ret.Span;
                    EmitReturn(computed is null ? null : new NirUse(computed));
                }
                _openTemps.RemoveAt(_openTemps.Count - 1);
                _statementTemps = saved;
                break;
            }
            case HirDefer defer:
                _scopes[^1].Add(defer);
                break;
            case HirMatch match:
                WithStatementTemps(() => LowerMatch(match));
                break;
            case HirExprStmt { Expression: HirTry or HirUnwrap } resultStmt when resultStmt.Expression.Type.Kind == TypeKind.Void:
                WithStatementTemps(() => LowerResultAccess(resultStmt.Expression));
                break;
            case HirExprStmt { Expression: HirBuiltinCall { Function: BuiltinFunction.PtrWrite } write }:
                WithStatementTemps(() =>
                {
                    // An assignment to the place `(*p)`; nothing is read or destroyed there (type-system.md §27.5).
                    var operands = LowerOperands(write.Arguments);
                    if (!operands.IsDefault && operands[0] is NirPlaceOperand pointer)
                    {
                        _span = stmt.Span;
                        Emit(new NirAssign(pointer.Place.Deref(), new NirUse(operands[1])));
                    }
                });
                break;
            case HirExprStmt exprStmt when TypeTable.NeedsDrop(exprStmt.Expression.Type):
                // A discarded value that needs drop is a temporary: destroyed at the end of the statement (§17.5).
                WithStatementTemps(() => AssignRvalue(new NirPlace(NewTemp(exprStmt.Expression.Type)), exprStmt.Expression));
                break;
            case HirExprStmt exprStmt:
                WithStatementTemps(() =>
                {
                    var rvalue = LowerRvalue(exprStmt.Expression);
                    if (rvalue is not null)
                    {
                        _span = exprStmt.Span;
                        Emit(new NirEval(rvalue));
                    }
                });
                break;
            default:
                throw new InvalidOperationException($"cannot lower {stmt.GetType().Name}");
        }
    }

    /// <summary>
    /// A match (type-system.md §15.2): switch on the discriminant (or the integer or bool value),
    /// then bind payloads in each arm, as copies or as references into the scrutinee.
    /// </summary>
    private void LowerMatch(HirMatch match)
    {
        var scrutineeType = match.Scrutinee.Type;
        var type = scrutineeType.Pointee ?? scrutineeType;
        NirPlace? scrutinee;
        if (match.Consumes)
        {
            // The match owns the value: it is moved into a temporary, which arms unpack (§18.4).
            if (LowerOperand(match.Scrutinee) is not { } consumed)
            {
                return;
            }
            scrutinee = new NirPlace(NewTemp(consumed.Type));
            Emit(new NirAssign(scrutinee, new NirUse(consumed)));
        }
        else
        {
            scrutinee = LowerPlace(match.Scrutinee);
        }
        if (scrutinee is null)
        {
            return;
        }
        var place = scrutineeType.IsReference ? scrutinee.Deref() : scrutinee;

        NirOperand value;
        if (type.Kind == TypeKind.Enum)
        {
            var tag = NewTemp(_types.U32);
            Emit(new NirAssign(new NirPlace(tag), new NirDiscriminant(place, _types.U32)));
            value = new NirPlaceOperand(tag);
        }
        else
        {
            value = new NirPlaceOperand(place);
        }

        var cases = ImmutableArray.CreateBuilder<(Int128, NirBlock)>();
        var arms = new List<(HirArm Arm, NirBlock Block)>();
        NirBlock? otherwise = null;
        foreach (var arm in match.Arms)
        {
            var block = NewBlock();
            arms.Add((arm, block));
            switch (arm.Kind)
            {
                case HirPatternKind.Wildcard:
                    otherwise ??= block;
                    break;
                case HirPatternKind.Variant:
                    cases.Add((arm.Variant!.Index, block));
                    break;
                default:
                    cases.Add((arm.Value, block));
                    break;
            }
        }
        if (otherwise is null)
        {
            // Exhaustive without `_`: no other value can occur.
            otherwise = NewBlock();
            otherwise.Terminator = new NirUnreachable { Span = match.Span };
        }
        Terminate(new NirSwitch(value, cases.ToImmutable(), otherwise));

        var join = NewBlock();
        var joinReached = false;
        foreach (var (arm, block) in arms)
        {
            _current = block;
            var depth = _scopes.Count;
            _scopes.Add([]);
            if (match.Consumes && arm.ListsPayloads)
            {
                // Every payload moves out: into its binding, or into a hidden local of the arm. Hidden
                // locals come last in the arm's scope, so they are destroyed first when the arm ends.
                var variant = arm.Variant!;
                var targets = new NirLocal?[variant.Payload.Count];
                foreach (var (local, index) in arm.Bindings)
                {
                    targets[index] = _locals[local.Id];
                }
                foreach (var (local, _) in arm.Bindings)
                {
                    _scopes[^1].Add(_locals[local.Id]);
                }
                for (var i = 0; i < targets.Length; i++)
                {
                    if (targets[i] is null && !TypeTable.IsCopy(variant.Payload[i].Type))
                    {
                        targets[i] = new NirLocal(_locals.Count, variant.Payload[i].Type, null);
                        _locals.Add(targets[i]!);
                        _scopes[^1].Add(targets[i]!);
                    }
                }
                Emit(new NirUnpack(place, variant, [.. targets]));
                LowerStmt(arm.Body);
                if (_current is not null)
                {
                    EmitStorageDead(depth);
                    Terminate(new NirGoto(join));
                    joinReached = true;
                }
                _scopes.RemoveAt(_scopes.Count - 1);
                continue;
            }
            foreach (var (local, index) in arm.Bindings)
            {
                var binding = _locals[local.Id];
                _scopes[^1].Add(binding);
                var payload = place.Project(Projection.Downcast(arm.Variant!)).Field(arm.Variant!.Payload[index]);
                // A match on a reference binds payloads by reference; one on a value copies them, even references.
                Emit(new NirAssign(new NirPlace(binding), scrutineeType.IsReference
                    ? new NirRef(local.Type.IsMutableReference, payload, local.Type)
                    : new NirUse(Read(payload))));
            }
            LowerStmt(arm.Body);
            if (_current is not null)
            {
                EmitStorageDead(depth);
                Terminate(new NirGoto(join));
                joinReached = true;
            }
            _scopes.RemoveAt(_scopes.Count - 1);
        }
        _current = joinReached ? join : null;
    }

    /// <summary>
    /// A `for` loop (type-system.md §16.4). The bounds, or the sequence and its length, are evaluated
    /// once into locals that live until the loop exits; a counter runs from the start up to the end.
    /// Each iteration binds the variable in its own scope; `continue` goes to the counter's increment.
    /// </summary>
    private void LowerFor(HirFor forStmt)
    {
        var span = forStmt.Span;
        var savedTemps = _statementTemps;
        var headerTemps = new List<NirLocal>();
        _statementTemps = headerTemps;
        var headerScope = _scopes.Count;
        _scopes.Add([]);

        NirPlace? sequence = null;
        NirLocal counter;
        NirOperand end;
        if (forStmt.End is not null)
        {
            var bounds = LowerOperands([forStmt.Start, forStmt.End]);
            if (!bounds.IsDefault)
            {
                counter = NewTemp(forStmt.Start.Type);
                Emit(new NirAssign(new NirPlace(counter), new NirUse(bounds[0])));
                var endLocal = NewTemp(forStmt.Start.Type);
                Emit(new NirAssign(new NirPlace(endLocal), new NirUse(bounds[1])));
                end = new NirPlaceOperand(endLocal);
            }
            else
            {
                (counter, end) = (null!, null!);
            }
        }
        else
        {
            sequence = LowerPlace(forStmt.Start);
            counter = null!;
            end = null!;
            if (sequence is not null)
            {
                counter = NewTemp(_types.USize);
                Emit(new NirAssign(new NirPlace(counter), new NirUse(new NirConst(0, _types.USize))));
                end = Length(sequence);
            }
        }
        var condition = _current is null ? null : NewTemp(_types.Bool);
        _statementTemps = savedTemps;
        _scopes[headerScope].AddRange(headerTemps);
        if (condition is null)
        {
            _scopes.RemoveAt(headerScope); // the header never completes
            return;
        }

        var head = NewBlock();
        Terminate(new NirGoto(head));
        _current = head;
        _span = span;
        var counterOperand = new NirPlaceOperand(counter);
        Emit(new NirAssign(new NirPlace(condition), new NirBinary(BinaryOp.Less, counterOperand, end, _types.Bool, _function.Source, span)));
        var body = NewBlock();
        var step = NewBlock();
        var exit = NewBlock();
        Terminate(new NirBranch(new NirPlaceOperand(condition), body, exit));

        _loops.Push(new LoopTargets(step, exit, _scopes.Count));
        _current = body;
        var variable = _locals[forStmt.Variable.Id];
        _scopes.Add([variable]);
        Emit(new NirAssign(new NirPlace(variable), new NirUse(sequence is null
            ? counterOperand
            : Read(sequence.Project(Projection.At(counterOperand))))));
        LowerStmt(forStmt.Body);
        if (_current is not null)
        {
            EmitStorageDead(_scopes.Count - 1);
            Terminate(new NirGoto(step));
        }
        _scopes.RemoveAt(_scopes.Count - 1);
        _loops.Pop();

        // counter < end, so the increment cannot overflow.
        _current = step;
        _span = span;
        Emit(new NirAssign(new NirPlace(counter), new NirBinary(BinaryOp.Add, counterOperand,
            new NirConst(1, counter.Type), counter.Type, _function.Source, span)));
        Terminate(new NirGoto(head));

        _current = exit;
        _span = new TextSpan(span.End, 0);
        EmitStorageDead(headerScope);
        _scopes.RemoveAt(headerScope);
    }

    /// <summary>The length of an array (a constant) or of a deref'd slice <c>s.*</c> (read from <c>s</c>).</summary>
    private NirOperand Length(NirPlace array)
    {
        var type = array.Type;
        if (!type.IsUnsized)
        {
            return new NirConst(type.ArrayLength, _types.USize);
        }
        var slice = new NirPlace(array.Local, array.Projections.RemoveAt(array.Projections.Length - 1));
        var length = NewTemp(_types.USize);
        Emit(new NirAssign(new NirPlace(length), new NirLength(slice, _types.USize)));
        return new NirPlaceOperand(length);
    }

    /// <summary>
    /// Element <paramref name="index"/> of an array or deref'd slice place: the index is evaluated
    /// into a temporary (or kept as a constant) and checked against the length (type-system.md §16.3).
    /// </summary>
    private NirPlace? IndexPlace(NirPlace array, HirExpr index)
    {
        var saved = _span;
        var operand = LowerOperand(index);
        if (operand is null)
        {
            return null;
        }
        if (operand is NirPlaceOperand)
        {
            var temp = NewTemp(_types.USize);
            Emit(new NirAssign(new NirPlace(temp), new NirUse(operand)));
            operand = new NirPlaceOperand(temp);
        }
        _span = index.Span;
        var length = Length(array);
        // A constant index below a constant length needs no check.
        if (!(operand is NirConst constant && length is NirConst bound && constant.Value < bound.Value))
        {
            Emit(new NirBoundsCheck(operand, length));
        }
        _span = saved;
        return array.Project(Projection.At(operand));
    }

    /// <summary>A HIR place: its projections, with each index evaluated and checked in order.</summary>
    private NirPlace? LowerPlace(HirPlace place)
    {
        var result = new NirPlace(_locals[place.Root.Id]);
        foreach (var projection in place.Projections)
        {
            if (projection.Kind != ProjectionKind.Index)
            {
                result = result.Project(projection);
            }
            else if (IndexPlace(result, (HirExpr)projection.Index!) is { } element)
            {
                result = element;
            }
            else
            {
                return null;
            }
        }
        return result;
    }

    /// <summary>The place an expression denotes, without moving it; other values are spilled into a temporary.</summary>
    private NirPlace? LowerPlace(HirExpr expr)
    {
        switch (expr)
        {
            case HirLocalRef local:
                return new NirPlace(_locals[local.Local.Id]);
            case HirFieldAccess field:
                return LowerPlace(field.Target)?.Field(field.Field);
            case HirDeref deref:
                return LowerPlace(deref.Reference)?.Deref();
            case HirIndex index:
                return LowerPlace(index.Target) is { } array ? IndexPlace(array, index.Index) : null;
        }
        switch (LowerOperand(expr))
        {
            case NirPlaceOperand operand:
                return operand.Place;
            case NirOperand constant and (NirConst or NirString):
            {
                var temp = NewTemp(constant.Type);
                Emit(new NirAssign(new NirPlace(temp), new NirUse(constant)));
                return new NirPlace(temp);
            }
            default:
                return null;
        }
    }

    private LoopTargets LowerLoopBody(HirBlock body, NirBlock head, NirBlock exit, NirBlock bodyStart)
    {
        var targets = new LoopTargets(head, exit, _scopes.Count);
        _loops.Push(targets);
        _current = bodyStart;
        LowerStmt(body);
        if (_current is not null)
        {
            Terminate(new NirGoto(head));
        }
        _loops.Pop();
        return targets;
    }

    private void LowerIf(HirIf ifStmt)
    {
        var condition = LowerOperand(ifStmt.Condition);
        if (condition is null)
        {
            return;
        }
        var thenBlock = NewBlock();
        var elseBlock = ifStmt.Else is null ? null : NewBlock();
        var join = NewBlock();
        Terminate(new NirBranch(condition, thenBlock, elseBlock ?? join));

        var joinReached = elseBlock is null;
        _current = thenBlock;
        LowerStmt(ifStmt.Then);
        if (_current is not null)
        {
            Terminate(new NirGoto(join));
            joinReached = true;
        }
        if (elseBlock is not null)
        {
            _current = elseBlock;
            LowerStmt(ifStmt.Else!);
            if (_current is not null)
            {
                Terminate(new NirGoto(join));
                joinReached = true;
            }
        }
        _current = joinReached ? join : null;
    }

    private void AssignRvalue(NirPlace target, HirExpr value)
    {
        var statementSpan = _span;
        if (NeedsIndexRecheck(target, value))
        {
            // The value is complete (its calls returned) before the element's index is checked again.
            if (LowerOperand(value) is { } operand)
            {
                _span = statementSpan;
                RecheckVectorIndexes(target);
                Emit(new NirAssign(target, new NirUse(operand)));
            }
            return;
        }
        if (!target.IsLocal && TypeChecker.MayHoldLoans(target.Type))
        {
            // A loan-carrying value stored into a field or element is made in a temporary first, so its loans
            // are those of a local that the borrow checker can follow into the target (type-system.md §25.7).
            if (LowerOperand(value) is { } spilled)
            {
                _span = statementSpan;
                Emit(new NirAssign(target, new NirUse(spilled)));
            }
            return;
        }
        var rvalue = LowerRvalue(value);
        if (rvalue is not null)
        {
            _span = statementSpan;
            Emit(new NirAssign(target, rvalue));
        }
    }

    /// <summary>
    /// A vector element written after a value that calls code: the code may have shrunk the vector since
    /// the target's index was checked, so the index is checked again before the write (§23.1).
    /// </summary>
    private static bool NeedsIndexRecheck(NirPlace target, HirExpr value) =>
        ContainsCall(value) && VectorIndexes(target).Any();

    private static IEnumerable<(NirOperand Index, NirPlace Elements)> VectorIndexes(NirPlace target)
    {
        for (var i = 1; i < target.Projections.Length; i++)
        {
            if (target.Projections[i].Kind == ProjectionKind.Index && target.Projections[i - 1].IsDeref
                && new NirPlace(target.Local, target.Projections[..(i - 1)]).Type.IsVector)
            {
                yield return ((NirOperand)target.Projections[i].Index!, new NirPlace(target.Local, target.Projections[..i]));
            }
        }
    }

    private void RecheckVectorIndexes(NirPlace target)
    {
        foreach (var (index, elements) in VectorIndexes(target))
        {
            Emit(new NirBoundsCheck(index, Length(elements)));
        }
    }

    // ---- Expressions ----

    /// <summary>Lowers to an operand, spilling into a temporary if needed; null if evaluation never completes.</summary>
    private NirOperand? LowerOperand(HirExpr expr)
    {
        var saved = _span;
        _span = expr.Span;
        try
        {
            return Completes(LowerOperandCore(expr), expr);
        }
        finally
        {
            _span = saved;
        }
    }

    /// <summary>
    /// Lowerings return null only for an expression that never completes, after ending the block.
    /// A null with the block still open would silently drop code, so it is an internal error.
    /// </summary>
    private T? Completes<T>(T? lowered, HirExpr expr)
        where T : class =>
        lowered is null && _current is not null
            ? throw new InvalidOperationException($"internal error: no lowering for {expr.GetType().Name} of type '{expr.Type}'")
            : lowered;

    private NirOperand? LowerOperandCore(HirExpr expr)
    {
        switch (expr)
        {
            case HirIntConst i:
                return new NirConst(i.Value, i.Type);
            case HirBoolConst b:
                return new NirConst(b.Value ? 1 : 0, b.Type);
            case HirStringConst s:
                return new NirString(s.Value, s.Type);
            case HirLocalRef l:
                return Read(new NirPlace(_locals[l.Local.Id]));
            case HirMove move:
                return LowerPlace(move.Place) is { } moved ? new NirPlaceOperand(moved, IsMove: true) : null;
            case HirIndex index:
                return LowerPlace(index) is { } element ? Read(element) : null;
            case HirTry or HirUnwrap:
            {
                // The Ok payload, moved (or copied) out of the result: the result is consumed (§17.4).
                if (LowerResultAccess(expr) is not { } result)
                {
                    return null;
                }
                var ok = SuccessVariant(result.Type);
                return Read(result.Project(Projection.Downcast(ok)).Field(ok.Payload[0]));
            }
            case HirFieldAccess field:
            {
                // A field of a place is itself a place; a field of any other value reads from a temporary.
                if (LowerOperand(field.Target) is not NirPlaceOperand target)
                {
                    return null;
                }
                return Read(target.Place.Field(field.Field));
            }
            case HirDeref deref:
            {
                if (LowerOperand(deref.Reference) is not NirPlaceOperand reference)
                {
                    return null;
                }
                return Read(reference.Place.Deref());
            }
        }

        var rvalue = LowerRvalue(expr);
        if (rvalue is null)
        {
            return null;
        }
        var temp = NewTemp(expr.Type);
        Emit(new NirAssign(new NirPlace(temp), rvalue));
        return Read(new NirPlace(temp));
    }

    /// <summary>Using a place by value copies a Copy type and moves anything else (type-system.md §14.3).</summary>
    private static NirPlaceOperand Read(NirPlace place) => new(place, IsMove: !TypeTable.IsCopy(place.Type));

    private NirRvalue? LowerRvalue(HirExpr expr)
    {
        var saved = _span;
        _span = expr.Span;
        try
        {
            return Completes(LowerRvalueCore(expr), expr);
        }
        finally
        {
            _span = saved;
        }
    }

    private NirRvalue? LowerRvalueCore(HirExpr expr)
    {
        switch (expr)
        {
            case HirIntConst or HirBoolConst or HirLocalRef or HirStringConst or HirMove:
                return new NirUse(LowerOperand(expr)!);
            case HirBorrow borrow:
                return LowerPlace(borrow.Place) is { } borrowed ? new NirRef(borrow.IsMutable, borrowed, borrow.Type) : null;
            case HirSlice slice:
                return LowerSlice(slice);
            case HirSliceLength length:
                return LowerPlace(length.Slice) is { } lengthOf ? new NirLength(lengthOf, _types.USize) : null;
            case HirArrayLiteral array:
            {
                var elements = LowerOperands(array.Elements);
                return elements.IsDefault ? null : new NirArray(elements, array.Type);
            }
            case HirArrayRepeat repeat:
            {
                var value = LowerOperand(repeat.Value);
                return value is null ? null : new NirArrayRepeat(value, repeat.Type);
            }
            case HirFunctionAddress address:
                return new NirFunctionAddress(address.Function, address.Type);
            case HirBorrowPath path:
                // The reference at the root is spilled to a temporary by LowerPlace; the loan is a reborrow through it.
                return LowerPlace(path.Path) is { } through ? new NirRef(path.IsMutable, through, path.Type) : null;
            case HirBorrowTemp temp:
            {
                // The value lives in a temporary until the end of the statement (lifetimes.md §6).
                var value = LowerRvalue(temp.Value);
                if (value is null)
                {
                    return null;
                }
                var storage = NewTemp(temp.Value.Type);
                Emit(new NirAssign(new NirPlace(storage), value));
                return new NirRef(temp.IsMutable, new NirPlace(storage), temp.Type);
            }
            case HirReborrow reborrow:
            {
                if (LowerOperand(reborrow.Reference) is not NirPlaceOperand reference)
                {
                    return null;
                }
                return new NirRef(reborrow.IsMutable, reference.Place.Deref(), reborrow.Type);
            }
            case HirIsNull test:
            {
                if (LowerOperand(test.Reference) is not NirPlaceOperand reference)
                {
                    return null;
                }
                var tag = NewTemp(_types.U32);
                Emit(new NirAssign(new NirPlace(tag), new NirDiscriminant(reference.Place.Deref(), _types.U32)));
                return new NirBinary(test.IsNull ? BinaryOp.Equal : BinaryOp.NotEqual, new NirPlaceOperand(tag),
                    new NirConst(0, _types.U32), _types.Bool, _function.Source, test.Span);
            }
            case HirFieldAccess or HirDeref or HirIndex or HirTry or HirUnwrap:
            {
                var operand = LowerOperand(expr);
                return operand is null ? null : new NirUse(operand);
            }
            case HirEnumValue enumValue:
            {
                var payload = LowerOperands(enumValue.Payload);
                return payload.IsDefault ? null : new NirEnumAggregate(enumValue.Variant, payload, enumValue.Type);
            }
            case HirStructLiteral literal:
            {
                // Evaluate in source order, then place the values in declaration order.
                var values = LowerOperands([.. literal.Fields.Select(f => f.Value)]);
                if (values.IsDefault)
                {
                    return null;
                }
                var ordered = new NirOperand[literal.Fields.Length];
                for (var i = 0; i < literal.Fields.Length; i++)
                {
                    ordered[literal.Fields[i].Field.Index] = values[i];
                }
                return new NirAggregate([.. ordered], literal.Type);
            }
            case HirUnary unary:
            {
                var operand = LowerOperand(unary.Operand);
                return operand is null ? null : new NirUnary(unary.Op, operand, unary.Type, _function.Source, unary.Span);
            }
            case HirBinary { Op: BinaryOp.LogicalAnd or BinaryOp.LogicalOr } logical:
                return LowerShortCircuit(logical);
            case HirBinary binary:
            {
                var operands = LowerOperands([binary.Left, binary.Right]);
                return operands.IsDefault
                    ? null
                    : new NirBinary(binary.Op, operands[0], operands[1], binary.Type, _function.Source, binary.Span);
            }
            case HirCast cast:
            {
                var operand = LowerOperand(cast.Operand);
                return operand is null ? null : new NirCast(operand, cast.Type);
            }
            case HirTruncate truncate:
            {
                var operand = LowerOperand(truncate.Operand);
                return operand is null ? null : new NirTruncate(operand, truncate.Type);
            }
            case HirCall call when call.Callee.ReceiverKind == ReceiverKind.BorrowMut
                                   && call.Arguments[0] is HirBorrow or HirReborrow { Reference: HirLocalRef }:
            {
                // B5 (lifetimes.md §5): the receiver's mutable loan starts after the arguments are evaluated.
                var arguments = LowerOperands(call.Arguments[1..]);
                if (arguments.IsDefault)
                {
                    return null;
                }
                var receiver = LowerOperand(call.Arguments[0]);
                return receiver is null ? null : new NirCall(call.Callee, [receiver, .. arguments], call.Type);
            }
            case HirCall call:
            {
                var arguments = LowerOperands(call.Arguments);
                return arguments.IsDefault ? null : new NirCall(call.Callee, arguments, call.Type);
            }
            case HirBuiltinCall { Function: BuiltinFunction.ProcessExit } exit:
            {
                var code = LowerOperand(exit.Arguments[0]);
                if (code is not null)
                {
                    Terminate(new NirExit(code));
                }
                return null;
            }
            case HirBuiltinCall { Function: BuiltinFunction.PtrRead } read:
            {
                // A copy from the place `(*p)` (type-system.md §27.5).
                if (LowerOperand(read.Arguments[0]) is not NirPlaceOperand pointer)
                {
                    return null;
                }
                return new NirUse(Read(pointer.Place.Deref()));
            }
            case HirBuiltinCall { Function: BuiltinFunction.VectorPush or BuiltinFunction.StringAppend or BuiltinFunction.SlotMapInsert } mutation:
            {
                // B5, as for a `borrow mut this` method: the receiver's loan starts after the argument (§23.1).
                var argument = LowerOperand(mutation.Arguments[1]);
                if (argument is null)
                {
                    return null;
                }
                var receiver = LowerOperand(mutation.Arguments[0]);
                return receiver is null ? null : new NirBuiltin(mutation.Function, [receiver, argument], mutation.Type);
            }
            case HirBuiltinCall builtin:
            {
                var arguments = LowerOperands(builtin.Arguments);
                return arguments.IsDefault ? null : new NirBuiltin(builtin.Function, arguments, builtin.Type);
            }
            default:
                throw new InvalidOperationException($"cannot lower {expr.GetType().Name}; the program has errors");
        }
    }

    /// <summary>
    /// Lowers operands left to right. A local read is copied to a temporary when a later operand
    /// contains a call, so the read happens before the call (intermediate-representation.md §3).
    /// Returns default if evaluation never completes.
    /// </summary>
    private ImmutableArray<NirOperand> LowerOperands(IReadOnlyList<HirExpr> exprs)
    {
        var result = ImmutableArray.CreateBuilder<NirOperand>(exprs.Count);
        for (var i = 0; i < exprs.Count; i++)
        {
            var operand = LowerOperand(exprs[i]);
            if (operand is null)
            {
                return default;
            }
            if (operand is NirPlaceOperand { Place.Local.Name: not null } && exprs.Skip(i + 1).Any(ContainsCall))
            {
                var temp = NewTemp(operand.Type);
                Emit(new NirAssign(new NirPlace(temp), new NirUse(operand)));
                operand = Read(new NirPlace(temp));
            }
            result.Add(operand);
        }
        return result.MoveToImmutable();
    }

    private static bool ContainsCall(HirExpr expr) => expr switch
    {
        HirCall or HirBuiltinCall => true,
        HirUnary u => ContainsCall(u.Operand),
        HirBinary b => ContainsCall(b.Left) || ContainsCall(b.Right),
        HirCast c => ContainsCall(c.Operand),
        HirTruncate t => ContainsCall(t.Operand),
        HirFieldAccess f => ContainsCall(f.Target),
        HirDeref d => ContainsCall(d.Reference),
        HirReborrow r => ContainsCall(r.Reference),
        HirBorrowTemp t => ContainsCall(t.Value),
        HirBorrowPath p => ContainsCall(p.Path),
        HirStructLiteral s => s.Fields.Any(f => ContainsCall(f.Value)),
        HirEnumValue e => e.Payload.Any(ContainsCall),
        HirArrayLiteral a => a.Elements.Any(ContainsCall),
        HirArrayRepeat a => ContainsCall(a.Value),
        HirIndex i => ContainsCall(i.Target) || ContainsCall(i.Index),
        HirSlice s => s.Base.Projections.Any(p => p.Index is HirExpr index && ContainsCall(index))
                      || (s.Low is not null && ContainsCall(s.Low)) || (s.High is not null && ContainsCall(s.High)),
        HirSliceLength l => ContainsCall(l.Slice),
        HirTry t => ContainsCall(t.Operand),
        HirUnwrap u => ContainsCall(u.Operand),
        HirIsNull n => ContainsCall(n.Reference),
        _ => false,
    };

    /// <summary>The variant <c>?</c> and <c>!</c> continue with: <c>Ok</c> of a result, <c>Some</c> of an optional.</summary>
    private static EnumVariant SuccessVariant(NovaType type) => type.Enum!.Variants[type.Enum.OptionType is null ? 0 : 1];

    /// <summary>
    /// <c>e?</c> / <c>e!</c> (type-system.md §17.4, §22.1): moves the result or optional into a temporary and
    /// switches on its tag. On <c>Err</c> or <c>null</c>, <c>?</c> returns <c>Err(payload)</c> or <c>null</c>
    /// (with every open temporary and scope ended) and <c>!</c> panics. Returns the temporary, in the success
    /// block; null if the operand never completes.
    /// </summary>
    private NirPlace? LowerResultAccess(HirExpr expr)
    {
        var (operandExpr, propagate) = expr is HirTry t ? (t.Operand, true) : (((HirUnwrap)expr).Operand, false);
        if (LowerOperand(operandExpr) is not { } operand)
        {
            return null;
        }
        _span = expr.Span;
        var result = new NirPlace(NewTemp(operand.Type));
        Emit(new NirAssign(result, new NirUse(operand)));
        var tag = NewTemp(_types.U32);
        Emit(new NirAssign(new NirPlace(tag), new NirDiscriminant(result, _types.U32)));
        var okBlock = NewBlock();
        var errBlock = NewBlock();
        var success = SuccessVariant(operand.Type);
        Terminate(new NirSwitch(new NirPlaceOperand(tag), [(success.Index, okBlock)], errBlock));

        _current = errBlock;
        var returnType = _function.ReturnType;
        if (propagate && operand.Type.Enum!.OptionType is not null)
        {
            EmitReturn(new NirEnumAggregate(returnType.Enum!.Variants[0], [], returnType)); // `null` (§22.1)
        }
        else if (propagate)
        {
            var err = operand.Type.Enum!.Variants[1];
            var payload = Read(result.Project(Projection.Downcast(err)).Field(err.Payload[0]));
            EmitReturn(new NirEnumAggregate(returnType.Enum!.Variants[1], [payload], returnType));
        }
        else
        {
            Terminate(new NirPanic(operand.Type.Enum!.OptionType is null ? NirPanicKind.Unwrap : NirPanicKind.UnwrapNull));
        }

        _current = okBlock;
        _span = expr.Span;
        return result;
    }

    /// <summary>
    /// <c>a[lo..hi]</c>: the base place, then the bounds left to right, then the range check
    /// (skipped for <c>a[..]</c>), then the slice, which borrows the whole base.
    /// </summary>
    private NirSlice? LowerSlice(HirSlice slice)
    {
        if (LowerPlace(slice.Base) is not { } array)
        {
            return null;
        }
        var bounds = LowerOperands([.. new[] { slice.Low, slice.High }.OfType<HirExpr>()]);
        if (bounds.IsDefault)
        {
            return null;
        }
        var length = Length(array);
        var low = slice.Low is null ? new NirConst(0, _types.USize) : bounds[0];
        var high = slice.High is null ? length : bounds[^1];
        var staticallyValid = low is NirConst l && high is NirConst h && length is NirConst n && l.Value <= h.Value && h.Value <= n.Value;
        if (slice.Type.IsStringView && (slice.Low is not null || slice.High is not null))
        {
            Emit(new NirRangeCheck(low, high, length) { Text = array }); // the bytes decide the boundaries
        }
        else if ((slice.Low is not null || slice.High is not null) && !staticallyValid)
        {
            Emit(new NirRangeCheck(low, high, length));
        }
        return new NirSlice(slice.IsMutable, array, low, high, slice.Type);
    }

    private NirUse? LowerShortCircuit(HirBinary logical)
    {
        var left = LowerOperand(logical.Left);
        if (left is null)
        {
            return null;
        }
        var result = NewTemp(_types.Bool);
        var evaluateRight = NewBlock();
        var shortCircuit = NewBlock();
        var join = NewBlock();
        var isAnd = logical.Op == BinaryOp.LogicalAnd;
        Terminate(isAnd ? new NirBranch(left, evaluateRight, shortCircuit) : new NirBranch(left, shortCircuit, evaluateRight));

        _current = shortCircuit;
        Emit(new NirAssign(new NirPlace(result), new NirUse(new NirConst(isAnd ? 0 : 1, _types.Bool))));
        Terminate(new NirGoto(join));

        _current = evaluateRight;
        var right = LowerOperand(logical.Right);
        if (_current is not null)
        {
            Emit(new NirAssign(new NirPlace(result), new NirUse(right!)));
            Terminate(new NirGoto(join));
        }

        _current = join;
        return new NirUse(new NirPlaceOperand(result));
    }
}
