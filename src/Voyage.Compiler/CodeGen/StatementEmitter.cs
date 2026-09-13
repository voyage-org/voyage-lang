using System.Reflection.Emit;
using Voyage.Compiler.Parsing;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.CodeGen;

/// <summary>
/// Emits IL for a <see cref="BoundStatement"/> — the CodeGen/ analogue
/// of <see cref="Semantics.Binder"/>'s statement-binding dispatch. By
/// the time a statement list reaches here, <c>Lowering/</c> has already
/// removed every <c>BoundSwitch</c> (desugared into <c>BoundIf</c>
/// chains — see <c>Lowering/README.md</c>), so this never needs to
/// handle patterns at all.
/// </summary>
public static class StatementEmitter
{
    public static void EmitStatements(CodeGenContext ctx, MethodEmitContext method, IReadOnlyList<BoundStatement> statements)
    {
        foreach (var statement in statements)
        {
            EmitStatement(ctx, method, statement);
        }
    }

    private static void EmitStatement(CodeGenContext ctx, MethodEmitContext method, BoundStatement statement)
    {
        switch (statement)
        {
            case BoundExpressionStatement s:
                EmitExpressionStatement(ctx, method, s);
                break;

            case BoundVariableDeclaration s:
                EmitVariableDeclaration(ctx, method, s);
                break;

            case BoundAssignment s:
                EmitAssignment(ctx, method, s);
                break;

            case BoundIf s:
                EmitIf(ctx, method, s);
                break;

            case BoundWhile s:
                EmitWhile(ctx, method, s);
                break;

            case BoundBreak:
                method.IL.Emit(OpCodes.Br, method.CurrentLoop.BreakLabel);
                break;

            case BoundContinue:
                method.IL.Emit(OpCodes.Br, method.CurrentLoop.ContinueLabel);
                break;

            case BoundReturn s:
                EmitReturn(ctx, method, s);
                break;

            default:
                // BoundSwitch specifically should never arrive here —
                // Lowering/ already removed it — and BoundErrorStatement
                // should never arrive either, since a bound tree with
                // any diagnostics shouldn't reach CodeGen/ at all (see
                // CodeGenerator's remarks). Either one reaching here is
                // a genuine pipeline-ordering bug upstream, not
                // malformed user input.
                throw new NotSupportedException($"CodeGen/ cannot emit statement kind '{statement.GetType().Name}' — expected Lowering/ to have removed it, or Semantics/ to have rejected it");
        }
    }

    private static void EmitExpressionStatement(CodeGenContext ctx, MethodEmitContext method, BoundExpressionStatement s)
    {
        ExpressionEmitter.Emit(ctx, method, s.Expression);

        // A statement's result (if it has one — e.g. a non-Void function
        // called for its side effects only) is never consumed by
        // anything else, so it has to be popped to keep the evaluation
        // stack balanced at the end of the statement. `print`'s Void
        // return means no pop is needed for the common case.
        if (s.Expression.Type != PrimitiveType.Void)
        {
            method.IL.Emit(OpCodes.Pop);
        }
    }

    private static void EmitVariableDeclaration(CodeGenContext ctx, MethodEmitContext method, BoundVariableDeclaration s)
    {
        var local = method.IL.DeclareLocal(ctx.ResolveClrType(s.Variable.Type));
        method.DeclareLocal(s.Variable, local);

        if (s.Initializer is not null)
        {
            ExpressionEmitter.Emit(ctx, method, s.Initializer);
            method.IL.Emit(OpCodes.Stloc, local);
        }

        // No Initializer (a binding with only a declared type, e.g.
        // `var x: Int`) leaves the local at its CLR default (0/false/
        // null) rather than genuinely "uninitialized" — .NET locals are
        // always zero-initialized by the JIT. voyage-lang itself has no
        // definite-assignment check yet to catch a *read* of such a
        // binding before it's ever assigned (an open Semantics/ gap,
        // not introduced by CodeGen/ — tracked in this phase's README).
    }

    /// <summary>
    /// Handles both plain (<c>=</c>) and compound (<c>+=</c> etc.)
    /// assignment, and both a variable target and a struct-property
    /// target — <c>Lowering/</c> doesn't desugar compound assignment
    /// (see <c>Lowering/README.md</c>'s scope), so the read-modify-write
    /// expansion for <c>+=</c>/<c>-=</c>/<c>*=</c>/<c>/=</c> happens
    /// here.
    /// </summary>
    private static void EmitAssignment(CodeGenContext ctx, MethodEmitContext method, BoundAssignment s)
    {
        switch (s.Target)
        {
            case BoundVariableReference variableRef:
                EmitVariableAssignment(ctx, method, variableRef.Variable, s);
                return;

            case BoundPropertyAccess propertyAccess:
                EmitPropertyAssignment(ctx, method, propertyAccess, s);
                return;

            default:
                throw new NotSupportedException($"CodeGen/ cannot emit an assignment target of kind '{s.Target.GetType().Name}' — Semantics/Binder's isMutableLValue check should have already rejected anything else");
        }
    }

    private static void EmitVariableAssignment(CodeGenContext ctx, MethodEmitContext method, VariableSymbol variable, BoundAssignment s)
    {
        if (s.Operator != AssignmentOperator.Assign)
        {
            ExpressionEmitter.EmitLoadVariable(method, variable);
        }

        ExpressionEmitter.Emit(ctx, method, s.Value);

        if (s.Operator != AssignmentOperator.Assign)
        {
            EmitCompoundOp(method.IL, s.Operator);
        }

        ExpressionEmitter.EmitStoreVariable(method, variable);
    }

    private static void EmitPropertyAssignment(CodeGenContext ctx, MethodEmitContext method, BoundPropertyAccess target, BoundAssignment s)
    {
        // The struct instance's address is computed once and reused —
        // for a plain `=` it's consumed once (by the final `stfld`); for
        // a compound op it's needed twice (once to read the current
        // value via `ldfld`, once to write the result back via
        // `stfld`), which `dup` provides without recomputing the
        // (possibly multi-level, e.g. `a.b.c`) address chain twice.
        ExpressionEmitter.EmitAddress(ctx, method, target.Target);
        var structSymbol = ((StructType)target.Target.Type).Symbol;
        var field = ctx.GetStructField(structSymbol, target.Property.Name);

        if (s.Operator != AssignmentOperator.Assign)
        {
            method.IL.Emit(OpCodes.Dup);
            method.IL.Emit(OpCodes.Ldfld, field);
            ExpressionEmitter.Emit(ctx, method, s.Value);
            EmitCompoundOp(method.IL, s.Operator);
        }
        else
        {
            ExpressionEmitter.Emit(ctx, method, s.Value);
        }

        method.IL.Emit(OpCodes.Stfld, field);
    }

    private static void EmitCompoundOp(ILGenerator il, AssignmentOperator op)
    {
        il.Emit(op switch
        {
            AssignmentOperator.AddAssign => OpCodes.Add,
            AssignmentOperator.SubtractAssign => OpCodes.Sub,
            AssignmentOperator.MultiplyAssign => OpCodes.Mul,
            AssignmentOperator.DivideAssign => OpCodes.Div,
            _ => throw new NotSupportedException($"'{op}' is not a compound assignment operator"),
        });
    }

    private static void EmitIf(CodeGenContext ctx, MethodEmitContext method, BoundIf s)
    {
        var elseLabel = method.IL.DefineLabel();
        var endLabel = method.IL.DefineLabel();

        ExpressionEmitter.Emit(ctx, method, s.Condition);
        method.IL.Emit(OpCodes.Brfalse, elseLabel);
        EmitStatements(ctx, method, s.Then);
        method.IL.Emit(OpCodes.Br, endLabel);

        method.IL.MarkLabel(elseLabel);
        if (s.Else is not null)
        {
            EmitStatements(ctx, method, s.Else);
        }

        method.IL.MarkLabel(endLabel);
    }

    private static void EmitWhile(CodeGenContext ctx, MethodEmitContext method, BoundWhile s)
    {
        // Top-tested loop, matching source semantics exactly (the
        // condition is checked before every iteration, including the
        // first) — `continue` jumps back to the condition check, not
        // into the middle of the body.
        var checkLabel = method.IL.DefineLabel();
        var endLabel = method.IL.DefineLabel();

        method.IL.MarkLabel(checkLabel);
        ExpressionEmitter.Emit(ctx, method, s.Condition);
        method.IL.Emit(OpCodes.Brfalse, endLabel);

        method.PushLoop(continueLabel: checkLabel, breakLabel: endLabel);
        EmitStatements(ctx, method, s.Body);
        method.PopLoop();

        method.IL.Emit(OpCodes.Br, checkLabel);
        method.IL.MarkLabel(endLabel);
    }

    private static void EmitReturn(CodeGenContext ctx, MethodEmitContext method, BoundReturn s)
    {
        if (s.Value is not null)
        {
            ExpressionEmitter.Emit(ctx, method, s.Value);
        }

        method.IL.Emit(OpCodes.Ret);
    }
}
