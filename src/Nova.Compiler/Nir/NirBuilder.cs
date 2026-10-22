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
    private NirBlock? _current;

    private sealed class LoopTargets(NirBlock head, NirBlock exit)
    {
        public NirBlock Head { get; } = head;
        public NirBlock Exit { get; } = exit;
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
        return new NirProgram([.. program.Functions.Select(f => new NirBuilder(f, types).BuildFunction())], program.Structs);
    }

    private NirFunction BuildFunction()
    {
        // HIR locals keep their ids; temporaries are appended after them.
        foreach (var local in _function.Locals)
        {
            _locals.Add(new NirLocal(local.Id, local.Type, local.Name));
        }

        _current = NewBlock();
        LowerStmt(_function.Body!);
        if (_current is not null)
        {
            // Flow analysis guarantees only void functions can fall off the end.
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
            switch (block.Terminator)
            {
                case NirGoto g:
                    work.Push(g.Target);
                    break;
                case NirBranch b:
                    work.Push(b.Then);
                    work.Push(b.Else);
                    break;
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
        return local;
    }

    private void Emit(NirStatement statement) => _current!.Statements.Add(statement);

    private void Terminate(NirTerminator terminator)
    {
        _current!.Terminator = terminator;
        _current = null;
    }

    // ---- Statements ----

    private void LowerStmt(HirStmt stmt)
    {
        if (_current is null)
        {
            return; // unreachable code is not lowered
        }

        switch (stmt)
        {
            case HirBlock block:
                foreach (var s in block.Statements)
                {
                    LowerStmt(s);
                }
                break;
            case HirLocalDecl decl when decl.Initializer is not null:
                AssignRvalue(new NirPlace(_locals[decl.Local.Id]), decl.Initializer);
                break;
            case HirLocalDecl:
                break;
            case HirAssign { Op: null } assign:
                AssignRvalue(ToPlace(assign.Target), assign.Value);
                break;
            case HirAssign assign:
            {
                var target = ToPlace(assign.Target);
                var right = LowerOperand(assign.Value);
                if (right is not null)
                {
                    Emit(new NirAssign(target, new NirBinary(assign.Op!.Value, new NirPlaceOperand(target), right,
                        target.Type, _function.Source, assign.Span)));
                }
                break;
            }
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
                Terminate(new NirGoto(loop.Exit));
                break;
            }
            case HirContinue:
                Terminate(new NirGoto(_loops.Peek().Head));
                break;
            case HirReturn ret:
            {
                NirOperand? value = null;
                if (ret.Value is not null && (value = LowerOperand(ret.Value)) is null)
                {
                    break; // the value never completes
                }
                Terminate(new NirReturn(value));
                break;
            }
            case HirExprStmt exprStmt:
            {
                var rvalue = LowerRvalue(exprStmt.Expression);
                if (rvalue is not null)
                {
                    Emit(new NirEval(rvalue));
                }
                break;
            }
            default:
                throw new InvalidOperationException($"cannot lower {stmt.GetType().Name}");
        }
    }

    private LoopTargets LowerLoopBody(HirBlock body, NirBlock head, NirBlock exit, NirBlock bodyStart)
    {
        var targets = new LoopTargets(head, exit);
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

    private NirPlace ToPlace(HirPlace place) => new(_locals[place.Root.Id], place.Path);

    private void AssignRvalue(NirPlace target, HirExpr value)
    {
        var rvalue = LowerRvalue(value);
        if (rvalue is not null)
        {
            Emit(new NirAssign(target, rvalue));
        }
    }

    // ---- Expressions ----

    /// <summary>Lowers to an operand, spilling into a temporary if needed; null if evaluation never completes.</summary>
    private NirOperand? LowerOperand(HirExpr expr)
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
                return new NirPlaceOperand(_locals[l.Local.Id]);
            case HirFieldAccess field:
            {
                // A field of a place is itself a place; a field of any other value reads from a temporary.
                if (LowerOperand(field.Target) is not NirPlaceOperand target)
                {
                    return null;
                }
                return new NirPlaceOperand(target.Place.Field(field.Field));
            }
        }

        var rvalue = LowerRvalue(expr);
        if (rvalue is null)
        {
            return null;
        }
        var temp = NewTemp(expr.Type);
        Emit(new NirAssign(new NirPlace(temp), rvalue));
        return new NirPlaceOperand(temp);
    }

    private NirRvalue? LowerRvalue(HirExpr expr)
    {
        switch (expr)
        {
            case HirIntConst or HirBoolConst or HirLocalRef or HirStringConst:
                return new NirUse(LowerOperand(expr)!);
            case HirFieldAccess:
            {
                var operand = LowerOperand(expr);
                return operand is null ? null : new NirUse(operand);
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
                operand = new NirPlaceOperand(temp);
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
        HirStructLiteral s => s.Fields.Any(f => ContainsCall(f.Value)),
        _ => false,
    };

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
