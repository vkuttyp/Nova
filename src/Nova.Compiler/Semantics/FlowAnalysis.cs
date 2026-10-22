using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Semantics;

/// <summary>
/// Missing-return analysis over structured HIR (docs/type-system.md §9): may the end of a
/// non-void function body be reached? Initialisation and moves are checked on NIR since M2c
/// (Analysis/MoveChecker.cs, type-system.md §14.4).
/// </summary>
internal sealed class FlowAnalysis
{
    private readonly Stack<bool> _loopExits = new();

    public static void Analyze(HirFunction function, SymbolTable symbols, TypeTable types, DiagnosticBag diagnostics)
    {
        var analysis = new FlowAnalysis();
        var endReachable = analysis.Completes(function.Body!);
        var returnType = function.ReturnType;
        if (endReachable && returnType.Kind != TypeKind.Void && !ReferenceEquals(returnType, types.Error))
        {
            diagnostics.Error(DiagnosticCodes.MissingReturn, function.Source, function.Syntax.Name.Span,
                $"'{symbols.GetName(function.Name)}' must return a '{returnType}' on every path; the end of its body is reachable");
        }
    }

    /// <summary>May control leave <paramref name="stmt"/> normally (fall through to what follows)?</summary>
    private bool Completes(HirStmt stmt)
    {
        switch (stmt)
        {
            case HirBlock block:
                foreach (var s in block.Statements)
                {
                    if (!Completes(s))
                    {
                        return false; // the rest of the block is unreachable
                    }
                }
                return true;
            case HirIf ifStmt:
            {
                var then = Completes(ifStmt.Then);
                var otherwise = ifStmt.Else is null || Completes(ifStmt.Else);
                return !Diverges(ifStmt.Condition) && (then || otherwise);
            }
            case HirWhile whileStmt:
                if (Diverges(whileStmt.Condition))
                {
                    return false;
                }
                RunLoop(whileStmt.Body);
                return true; // the condition can be false
            case HirLoop loop:
                return RunLoop(loop.Body); // only a break leaves a `loop`
            case HirFor forStmt:
                if (Diverges(forStmt.Start) || (forStmt.End is not null && Diverges(forStmt.End)))
                {
                    return false;
                }
                RunLoop(forStmt.Body);
                return true; // the sequence or range can be empty
            case HirBreak:
                if (_loopExits.Count > 0)
                {
                    _loopExits.Pop();
                    _loopExits.Push(true);
                }
                return false;
            case HirContinue or HirReturn:
                return false;
            case HirLocalDecl decl:
                return decl.Initializer is null || !Diverges(decl.Initializer);
            case HirAssign assign:
                return !Diverges(assign.Value);
            case HirExprStmt exprStmt:
                return !Diverges(exprStmt.Expression);
            case HirDefer:
                return true; // the deferred statement runs at scope exit, not here
            case HirMatch match:
            {
                if (Diverges(match.Scrutinee))
                {
                    return false;
                }
                var any = false;
                foreach (var arm in match.Arms)
                {
                    any |= Completes(arm.Body); // matches are exhaustive (N0256), so some arm always runs
                }
                return any;
            }
            default:
                throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
        }
    }

    /// <summary>Checks a loop body; returns whether a `break` targets the loop.</summary>
    private bool RunLoop(HirBlock body)
    {
        _loopExits.Push(false);
        Completes(body);
        return _loopExits.Pop();
    }

    /// <summary>Does evaluating the expression certainly not complete (a call of type never, e.g. Process.exit)?</summary>
    private static bool Diverges(HirExpr expr) => expr switch
    {
        HirCall or HirBuiltinCall when expr.Type.Kind == TypeKind.Never => true,
        HirCall call => call.Arguments.Any(Diverges),
        HirBuiltinCall call => call.Arguments.Any(Diverges),
        HirBinary { Op: BinaryOp.LogicalAnd or BinaryOp.LogicalOr } logical => Diverges(logical.Left),
        HirBinary binary => Diverges(binary.Left) || Diverges(binary.Right),
        HirUnary unary => Diverges(unary.Operand),
        HirCast cast => Diverges(cast.Operand),
        HirTruncate truncate => Diverges(truncate.Operand),
        HirFieldAccess field => Diverges(field.Target),
        HirStructLiteral literal => literal.Fields.Any(f => Diverges(f.Value)),
        HirEnumValue value => value.Payload.Any(Diverges),
        HirDeref deref => Diverges(deref.Reference),
        HirReborrow reborrow => Diverges(reborrow.Reference),
        HirBorrowTemp temp => Diverges(temp.Value),
        HirArrayLiteral array => array.Elements.Any(Diverges),
        HirArrayRepeat repeat => Diverges(repeat.Value),
        HirIndex index => Diverges(index.Target) || Diverges(index.Index),
        HirSlice slice => (slice.Low is not null && Diverges(slice.Low)) || (slice.High is not null && Diverges(slice.High)),
        HirSliceLength length => Diverges(length.Slice),
        HirTry t => Diverges(t.Operand),
        HirUnwrap u => Diverges(u.Operand),
        HirIsNull n => Diverges(n.Reference),
        _ => false,
    };
}
