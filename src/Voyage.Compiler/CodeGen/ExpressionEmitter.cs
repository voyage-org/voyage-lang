using System.Reflection.Emit;
using Voyage.Compiler.Lowering;
using Voyage.Compiler.Parsing;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.CodeGen;

/// <summary>
/// Emits IL for a <see cref="BoundExpression"/>, leaving its value on
/// the evaluation stack — the CodeGen/ analogue of
/// <see cref="Semantics.Binder"/>'s expression-binding dispatch, one
/// case per concrete node kind. Every method here is a pure emission
/// step: none of them return a CLR value, since the whole point of IL
/// emission is that the *stack*, not a C# return value, carries results
/// forward to whatever emits next.
/// </summary>
public static class ExpressionEmitter
{
    public static void Emit(CodeGenContext ctx, MethodEmitContext method, BoundExpression expression)
    {
        switch (expression)
        {
            case BoundIntegerLiteral e:
                method.IL.Emit(OpCodes.Ldc_I8, e.Value);
                break;

            case BoundFloatLiteral e:
                method.IL.Emit(OpCodes.Ldc_R8, e.Value);
                break;

            case BoundBooleanLiteral e:
                method.IL.Emit(e.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;

            case BoundStringLiteral e:
                method.IL.Emit(OpCodes.Ldstr, e.Value);
                break;

            case BoundInterpolatedString e:
                EmitInterpolatedString(ctx, method, e);
                break;

            case BoundVariableReference e:
                EmitLoadVariable(method, e.Variable);
                break;

            case BoundCall e:
                EmitCall(ctx, method, e);
                break;

            case BoundStructConstruction e:
                EmitStructConstruction(ctx, method, e);
                break;

            case BoundPropertyAccess e:
                EmitPropertyRead(ctx, method, e);
                break;

            case BoundBinary e:
                EmitBinary(ctx, method, e);
                break;

            case BoundUnary e:
                EmitUnary(ctx, method, e);
                break;

            case LoweredEnumTagCheck e:
                EmitEnumTagCheck(ctx, method, e);
                break;

            case LoweredAssociatedValueAccess e:
                EmitAssociatedValueAccess(ctx, method, e);
                break;

            default:
                throw new NotSupportedException($"CodeGen/ cannot emit expression kind '{expression.GetType().Name}' yet");
        }
    }

    /// <summary>
    /// Pushes the *address* of an assignable expression rather than its
    /// value — needed wherever a write has to land back in the original
    /// storage (a struct property assignment's target — see
    /// <see cref="StatementEmitter"/>'s assignment handling) rather than
    /// a throwaway copy. Recurses through a property-access chain (e.g.
    /// <c>outer.inner.x</c>) via <c>ldflda</c>, bottoming out at a real
    /// variable's <c>ldloca</c>/<c>ldarga</c> — the same "walk down to a
    /// real storage location" structure
    /// <see cref="Semantics.Binder"/>'s own lvalue check
    /// (<c>BindAssignment</c>'s <c>isMutableLValue</c> pattern) already
    /// established was the valid shape for an assignment target.
    /// </summary>
    public static void EmitAddress(CodeGenContext ctx, MethodEmitContext method, BoundExpression expression)
    {
        switch (expression)
        {
            case BoundVariableReference e:
                EmitAddressOfVariable(method, e.Variable);
                break;

            case BoundPropertyAccess e:
                EmitAddress(ctx, method, e.Target);
                var structSymbol = ((StructType)e.Target.Type).Symbol;
                method.IL.Emit(OpCodes.Ldflda, ctx.GetStructField(structSymbol, e.Property.Name));
                break;

            default:
                throw new NotSupportedException($"CodeGen/ cannot take the address of expression kind '{expression.GetType().Name}' — only variables and struct property chains are assignable");
        }
    }

    public static void EmitLoadVariable(MethodEmitContext method, VariableSymbol variable)
    {
        switch (method.GetStorage(variable))
        {
            case ArgumentStorage a: method.IL.Emit(OpCodes.Ldarg, a.Index); break;
            case LocalStorage l: method.IL.Emit(OpCodes.Ldloc, l.Local); break;
        }
    }

    public static void EmitStoreVariable(MethodEmitContext method, VariableSymbol variable)
    {
        switch (method.GetStorage(variable))
        {
            case ArgumentStorage a: method.IL.Emit(OpCodes.Starg, a.Index); break;
            case LocalStorage l: method.IL.Emit(OpCodes.Stloc, l.Local); break;
        }
    }

    private static void EmitAddressOfVariable(MethodEmitContext method, VariableSymbol variable)
    {
        switch (method.GetStorage(variable))
        {
            case ArgumentStorage a: method.IL.Emit(OpCodes.Ldarga, a.Index); break;
            case LocalStorage l: method.IL.Emit(OpCodes.Ldloca, l.Local); break;
        }
    }

    /// <summary>
    /// Every segment of a <see cref="BoundInterpolatedString"/> —
    /// literal text pieces and embedded expressions alike — is already
    /// guaranteed <c>String</c>-typed by
    /// <see cref="Semantics.Binder.BindInterpolationSegment"/> (it
    /// rejects anything else, since there's no
    /// <c>CustomStringConvertible</c>-style protocol yet to convert a
    /// non-<c>String</c> value). That means this never needs a
    /// <c>ToString()</c> conversion step — just pairwise
    /// <c>string.Concat</c>.
    /// </summary>
    private static void EmitInterpolatedString(CodeGenContext ctx, MethodEmitContext method, BoundInterpolatedString e)
    {
        if (e.Segments.Count == 0)
        {
            method.IL.Emit(OpCodes.Ldstr, string.Empty);
            return;
        }

        Emit(ctx, method, e.Segments[0]);
        var concat = typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!;
        for (var i = 1; i < e.Segments.Count; i++)
        {
            Emit(ctx, method, e.Segments[i]);
            method.IL.Emit(OpCodes.Call, concat);
        }
    }

    private static void EmitCall(CodeGenContext ctx, MethodEmitContext method, BoundCall e)
    {
        foreach (var argument in e.Arguments)
        {
            Emit(ctx, method, argument);
        }

        // The `print` builtin has no Parsing.FunctionDeclaration (see
        // DeclarationBinder.BuiltinPrintSymbol) — that's the reliable
        // signal it's the builtin rather than a same-named user
        // function (voyage-lang has no shadowing/overload resolution
        // yet, so a user `func print(...)` would be a separate,
        // already-caught "already declared" error before ever reaching
        // here — see DeclarationBinder's duplicate-declaration check).
        if (e.Function.Declaration is null)
        {
            method.IL.Emit(OpCodes.Call, ctx.PrintMethod);
            return;
        }

        method.IL.Emit(OpCodes.Call, ctx.GetFunction(e.Function));
    }

    private static void EmitStructConstruction(CodeGenContext ctx, MethodEmitContext method, BoundStructConstruction e)
    {
        foreach (var argument in e.Arguments)
        {
            Emit(ctx, method, argument);
        }

        method.IL.Emit(OpCodes.Newobj, ctx.GetStructConstructor(e.Struct));
    }

    private static void EmitPropertyRead(CodeGenContext ctx, MethodEmitContext method, BoundPropertyAccess e)
    {
        Emit(ctx, method, e.Target);

        // `ldfld` works directly on a value-type instance already on
        // the stack (verified against the actual runtime before writing
        // this — no `ldflda`/address needed for a plain read; only a
        // write, via EmitAddress above, needs one).
        var structSymbol = ((StructType)e.Target.Type).Symbol;
        method.IL.Emit(OpCodes.Ldfld, ctx.GetStructField(structSymbol, e.Property.Name));
    }

    private static void EmitEnumTagCheck(CodeGenContext ctx, MethodEmitContext method, LoweredEnumTagCheck e)
    {
        Emit(ctx, method, e.Subject);
        var enumSymbol = ((Semantics.EnumType)e.Subject.Type).Symbol;
        method.IL.Emit(OpCodes.Ldfld, ctx.GetEnumTagField(enumSymbol));
        method.IL.Emit(OpCodes.Ldc_I4, ctx.GetEnumCaseTag(enumSymbol, e.Case.Name));
        method.IL.Emit(OpCodes.Ceq);
    }

    private static void EmitAssociatedValueAccess(CodeGenContext ctx, MethodEmitContext method, LoweredAssociatedValueAccess e)
    {
        Emit(ctx, method, e.Subject);
        var enumSymbol = ((Semantics.EnumType)e.Subject.Type).Symbol;
        method.IL.Emit(OpCodes.Ldfld, ctx.GetEnumSlotField(enumSymbol, e.Case.Name, e.SlotIndex));
    }

    private static void EmitBinary(CodeGenContext ctx, MethodEmitContext method, BoundBinary e)
    {
        // Logical &&/|| need short-circuit evaluation (the right operand
        // must not run at all if it wouldn't affect the result — it may
        // have side effects), so they branch instead of unconditionally
        // evaluating both operands first the way every other operator
        // below does.
        switch (e.Operator)
        {
            case BinaryOperator.LogicalAnd:
                EmitLogicalAnd(ctx, method, e);
                return;
            case BinaryOperator.LogicalOr:
                EmitLogicalOr(ctx, method, e);
                return;
        }

        Emit(ctx, method, e.Left);
        Emit(ctx, method, e.Right);

        switch (e.Operator)
        {
            case BinaryOperator.Add: method.IL.Emit(OpCodes.Add); break;
            case BinaryOperator.Subtract: method.IL.Emit(OpCodes.Sub); break;
            case BinaryOperator.Multiply: method.IL.Emit(OpCodes.Mul); break;
            case BinaryOperator.Divide: method.IL.Emit(OpCodes.Div); break;
            case BinaryOperator.Modulo: method.IL.Emit(OpCodes.Rem); break;

            case BinaryOperator.Equal: EmitEquality(method.IL, e.Left.Type, negate: false); break;
            case BinaryOperator.NotEqual: EmitEquality(method.IL, e.Left.Type, negate: true); break;

            case BinaryOperator.Less: method.IL.Emit(OpCodes.Clt); break;
            case BinaryOperator.Greater: method.IL.Emit(OpCodes.Cgt); break;

            // No direct "less-or-equal"/"greater-or-equal" comparison
            // opcode exists in IL — each is the negation of the
            // opposite strict comparison (a <= b  <=>  !(a > b)).
            case BinaryOperator.LessEqual:
                method.IL.Emit(OpCodes.Cgt);
                method.IL.Emit(OpCodes.Ldc_I4_0);
                method.IL.Emit(OpCodes.Ceq);
                break;
            case BinaryOperator.GreaterEqual:
                method.IL.Emit(OpCodes.Clt);
                method.IL.Emit(OpCodes.Ldc_I4_0);
                method.IL.Emit(OpCodes.Ceq);
                break;

            default:
                throw new NotSupportedException($"CodeGen/ cannot emit binary operator '{e.Operator}' — Semantics/Binder should have already rejected this (e.g. NilCoalescing)");
        }
    }

    /// <summary>
    /// String equality needs its own path: raw <c>ceq</c> on two string
    /// references compares *reference identity*, not the value equality
    /// voyage-lang's <c>==</c> actually means (the same distinction C#'s
    /// own <c>==</c> operator draws for <c>string</c>, which is exactly
    /// what <c>string.op_Equality</c>/<c>op_Inequality</c> implement —
    /// reusing those gives voyage-lang the identical, already-correct
    /// semantics rather than reimplementing string comparison here).
    /// Every other type <see cref="Semantics.Binder.CheckEquality"/>
    /// currently allows (<c>Int</c>, <c>Double</c>, <c>Bool</c>) has
    /// value semantics under plain <c>ceq</c> already.
    /// </summary>
    private static void EmitEquality(ILGenerator il, TypeSymbol operandType, bool negate)
    {
        if (operandType is PrimitiveType { Name: "String" })
        {
            var method = negate
                ? typeof(string).GetMethod("op_Inequality", [typeof(string), typeof(string)])!
                : typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!;
            il.Emit(OpCodes.Call, method);
            return;
        }

        il.Emit(OpCodes.Ceq);
        if (negate)
        {
            // No direct "cne" (compare-not-equal) opcode — negate ceq's
            // 0/1 result the same way LessEqual/GreaterEqual do above.
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
        }
    }

    private static void EmitLogicalAnd(CodeGenContext ctx, MethodEmitContext method, BoundBinary e)
    {
        var shortCircuitFalse = method.IL.DefineLabel();
        var end = method.IL.DefineLabel();

        Emit(ctx, method, e.Left);
        method.IL.Emit(OpCodes.Brfalse, shortCircuitFalse);
        Emit(ctx, method, e.Right);
        method.IL.Emit(OpCodes.Br, end);
        method.IL.MarkLabel(shortCircuitFalse);
        method.IL.Emit(OpCodes.Ldc_I4_0);
        method.IL.MarkLabel(end);
    }

    private static void EmitLogicalOr(CodeGenContext ctx, MethodEmitContext method, BoundBinary e)
    {
        var shortCircuitTrue = method.IL.DefineLabel();
        var end = method.IL.DefineLabel();

        Emit(ctx, method, e.Left);
        method.IL.Emit(OpCodes.Brtrue, shortCircuitTrue);
        Emit(ctx, method, e.Right);
        method.IL.Emit(OpCodes.Br, end);
        method.IL.MarkLabel(shortCircuitTrue);
        method.IL.Emit(OpCodes.Ldc_I4_1);
        method.IL.MarkLabel(end);
    }

    private static void EmitUnary(CodeGenContext ctx, MethodEmitContext method, BoundUnary e)
    {
        Emit(ctx, method, e.Operand);

        switch (e.Operator)
        {
            case UnaryOperator.Negate:
                method.IL.Emit(OpCodes.Neg);
                break;
            case UnaryOperator.LogicalNot:
                // No direct "logical not" opcode — a bool is a 0/1
                // int32 in IL, so comparing it against 0 flips it.
                method.IL.Emit(OpCodes.Ldc_I4_0);
                method.IL.Emit(OpCodes.Ceq);
                break;
            default:
                throw new NotSupportedException($"CodeGen/ cannot emit unary operator '{e.Operator}'");
        }
    }
}
