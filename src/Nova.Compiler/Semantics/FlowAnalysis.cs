using System.Collections;
using Nova.Compiler.Diagnostics;
using Nova.Compiler.Hir;
using Nova.Compiler.Syntax;

namespace Nova.Compiler.Semantics;

/// <summary>
/// Definite assignment and missing-return analysis over structured HIR (docs/type-system.md §9).
/// The state is the set of locals assigned on every path, or null when the point is unreachable.
/// M1 has no moves, so assigned-sets only grow along a path and a loop head's state equals its
/// entry state: no fixpoint iteration is needed. M2 replaces this with NIR dataflow.
/// </summary>
internal sealed class FlowAnalysis
{
    private readonly HirFunction _function;
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly HashSet<int> _reported = [];
    private readonly Stack<List<BitArray>> _breakStates = new();

    private FlowAnalysis(HirFunction function, SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _function = function;
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    public static void Analyze(HirFunction function, SymbolTable symbols, TypeTable types, DiagnosticBag diagnostics)
    {
        var analysis = new FlowAnalysis(function, symbols, diagnostics);
        var entry = new BitArray(function.Locals.Count);
        foreach (var parameter in function.Parameters)
        {
            entry[parameter.Id] = true;
        }

        var exit = analysis.Stmt(function.Body!, entry);
        var returnType = function.ReturnType;
        if (exit is not null && returnType.Kind != TypeKind.Void && !ReferenceEquals(returnType, types.Error))
        {
            diagnostics.Error(DiagnosticCodes.MissingReturn, function.Source, function.Syntax.Name.Span,
                $"'{symbols.GetName(function.Name)}' must return a '{returnType}' on every path; the end of its body is reachable");
        }
    }

    private static BitArray Copy(BitArray state) => new(state);

    /// <summary>Intersection of two states; null (unreachable) is the identity.</summary>
    private static BitArray? Join(BitArray? a, BitArray? b) =>
        a is null ? b : b is null ? a : Copy(a).And(b);

    private BitArray? Stmt(HirStmt stmt, BitArray? state)
    {
        if (state is null)
        {
            return null; // unreachable code is not checked
        }

        switch (stmt)
        {
            case HirBlock block:
                foreach (var s in block.Statements)
                {
                    state = Stmt(s, state);
                }
                return state;
            case HirLocalDecl decl:
                if (decl.Initializer is not null)
                {
                    state = Expr(decl.Initializer, state);
                    state?.Set(decl.Local.Id, true);
                }
                return state;
            case HirAssign assign:
                state = Expr(assign.Value, state);
                if (state is not null)
                {
                    var target = assign.Target;
                    // `x += 1` reads x; `p.f = e` needs p fully assigned (no partial initialisation in M2a).
                    if (assign.Op is not null || !target.Path.IsEmpty)
                    {
                        Read(target.Root, assign.Span, state);
                    }
                    if (target.Path.IsEmpty)
                    {
                        state.Set(target.Root.Id, true);
                    }
                }
                return state;
            case HirIf ifStmt:
            {
                state = Expr(ifStmt.Condition, state);
                if (state is null)
                {
                    return null;
                }
                var then = Stmt(ifStmt.Then, Copy(state));
                var otherwise = ifStmt.Else is null ? state : Stmt(ifStmt.Else, Copy(state));
                return Join(then, otherwise);
            }
            case HirWhile whileStmt:
            {
                state = Expr(whileStmt.Condition, state);
                if (state is null)
                {
                    return null;
                }
                var breaks = RunLoopBody(whileStmt.Body, state);
                var result = state; // the condition can be false on entry
                foreach (var b in breaks)
                {
                    result = Join(result, b);
                }
                return result;
            }
            case HirLoop loop:
            {
                BitArray? result = null; // only a break leaves a `loop`
                foreach (var b in RunLoopBody(loop.Body, state))
                {
                    result = Join(result, b);
                }
                return result;
            }
            case HirBreak:
                // Outside a loop the checker has already reported N0214; there is no target to record.
                if (_breakStates.TryPeek(out var targets))
                {
                    targets.Add(Copy(state));
                }
                return null;
            case HirContinue:
                return null;
            case HirReturn ret:
                if (ret.Value is not null)
                {
                    Expr(ret.Value, state);
                }
                return null;
            case HirExprStmt exprStmt:
                return Expr(exprStmt.Expression, state);
            default:
                throw new InvalidOperationException($"unexpected statement {stmt.GetType().Name}");
        }
    }

    private List<BitArray> RunLoopBody(HirBlock body, BitArray entry)
    {
        _breakStates.Push([]);
        Stmt(body, Copy(entry));
        return _breakStates.Pop();
    }

    /// <summary>Checks reads in evaluation order; returns null if the expression never completes.</summary>
    private BitArray? Expr(HirExpr expr, BitArray state)
    {
        switch (expr)
        {
            case HirLocalRef local:
                Read(local.Local, local.Span, state);
                return state;
            case HirUnary unary:
                return Expr(unary.Operand, state);
            case HirBinary { Op: BinaryOp.LogicalAnd or BinaryOp.LogicalOr } logical:
            {
                // The right operand may be skipped, so its divergence does not make the whole diverge.
                var left = Expr(logical.Left, state);
                if (left is not null)
                {
                    Expr(logical.Right, left);
                }
                return left;
            }
            case HirBinary binary:
            {
                var left = Expr(binary.Left, state);
                return left is null ? null : Expr(binary.Right, left);
            }
            case HirCast cast:
                return Expr(cast.Operand, state);
            case HirFieldAccess field:
                return Expr(field.Target, state);
            case HirStructLiteral literal:
                return Arguments(literal.Fields.Select(f => f.Value), state, literal.Type);
            case HirTruncate truncate:
                return Expr(truncate.Operand, state);
            case HirCall call:
                return Arguments(call.Arguments, state, call.Type);
            case HirBuiltinCall call:
                return Arguments(call.Arguments, state, call.Type);
            default:
                return state; // constants and errors read nothing
        }
    }

    private BitArray? Arguments(IEnumerable<HirExpr> arguments, BitArray state, Semantics.NovaType resultType)
    {
        BitArray? current = state;
        foreach (var argument in arguments)
        {
            if (current is null)
            {
                return null;
            }
            current = Expr(argument, current);
        }
        return resultType.Kind == TypeKind.Never ? null : current;
    }

    private void Read(HirLocal local, TextSpan span, BitArray state)
    {
        if (!state[local.Id] && _reported.Add(local.Id))
        {
            _diagnostics.Error(DiagnosticCodes.PossiblyUnassigned, _function.Source, span,
                $"'{_symbols.GetName(local.Name)}' is read here but may not have been assigned a value");
        }
    }
}
