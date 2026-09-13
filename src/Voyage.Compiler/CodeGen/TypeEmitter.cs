using System.Reflection;
using System.Reflection.Emit;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.CodeGen;

/// <summary>
/// Builds the CLR type for every <c>struct</c>/<c>enum</c> in a
/// <see cref="BoundCompilationUnit"/>, per ADR-0012's Decision 2. Runs
/// in three passes, mirroring <see cref="DeclarationBinder"/>'s own
/// two-step struct/enum resolution for the identical reason: a
/// property's or associated-value's type may name a struct/enum
/// declared later in the same file, so every type needs to exist as at
/// least an empty shell before any field/method referencing one is
/// defined.
///
/// 1. <see cref="DefineShells"/> — an empty <see cref="TypeBuilder"/>
///    per struct/enum, registered in <see cref="CodeGenContext"/> so
///    later field/parameter/return types can resolve them.
/// 2. <see cref="EmitStructBody"/>/<see cref="EmitEnumBody"/> — fields,
///    the struct's generated memberwise constructor, and (for enums)
///    the tag field, per-case slot fields, and per-case static factory
///    methods.
/// 3. <c>CreateType()</c> on every shell, only once every field/method
///    across every struct/enum has been defined — required by
///    Reflection.Emit regardless of cross-references.
/// </summary>
public static class TypeEmitter
{
    public static void EmitStructsAndEnums(CodeGenContext ctx, BoundCompilationUnit unit)
    {
        DefineShells(ctx, unit);

        foreach (var s in unit.Structs)
        {
            EmitStructBody(ctx, s.Symbol);
        }

        foreach (var e in unit.Enums)
        {
            EmitEnumBody(ctx, e.Symbol);
        }

        // Bake every shell only after every field/method across every
        // struct/enum is defined — a struct referencing an enum (or
        // vice versa) declared later in the file needs that other
        // type's shell to still be open (not yet baked) while its own
        // body is being defined.
        foreach (var s in unit.Structs)
        {
            ctx.GetStructTypeBuilder(s.Symbol).CreateType();
        }

        foreach (var e in unit.Enums)
        {
            ctx.GetEnumTypeBuilder(e.Symbol).CreateType();
        }
    }

    private static void DefineShells(CodeGenContext ctx, BoundCompilationUnit unit)
    {
        foreach (var s in unit.Structs)
        {
            var tb = ctx.Module.DefineType(s.Symbol.Name, TypeAttributes.Public, typeof(ValueType));
            ctx.RegisterStructType(s.Symbol, tb);
        }

        foreach (var e in unit.Enums)
        {
            var tb = ctx.Module.DefineType(e.Symbol.Name, TypeAttributes.Public, typeof(ValueType));
            ctx.RegisterEnumType(e.Symbol, tb);
        }
    }

    private static void EmitStructBody(CodeGenContext ctx, StructSymbol s)
    {
        var tb = ctx.GetStructTypeBuilder(s);

        foreach (var property in s.Properties)
        {
            var field = tb.DefineField(property.Name, ctx.ResolveClrType(property.Type), FieldAttributes.Public);
            ctx.RegisterStructField(s, property.Name, field);
        }

        // The implicit memberwise constructor — mirrors
        // Semantics.Binder's own already-implicit positional-
        // memberwise-construction rule (see StructSymbol's remarks):
        // one constructor parameter per property, in declaration order,
        // assigned straight into the matching field.
        var parameterTypes = s.Properties.Select(p => ctx.ResolveClrType(p.Type)).ToArray();
        var ctorBuilder = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameterTypes);
        var il = ctorBuilder.GetILGenerator();
        for (var i = 0; i < s.Properties.Count; i++)
        {
            il.Emit(OpCodes.Ldarg_0); // `this` — instance constructors always have it at argument 0.
            il.Emit(OpCodes.Ldarg, i + 1);
            il.Emit(OpCodes.Stfld, ctx.GetStructField(s, s.Properties[i].Name));
        }

        il.Emit(OpCodes.Ret);
        ctx.RegisterStructConstructor(s, ctorBuilder);
    }

    private static void EmitEnumBody(CodeGenContext ctx, EnumSymbol e)
    {
        var tb = ctx.GetEnumTypeBuilder(e);
        var tagField = tb.DefineField("Tag", typeof(int), FieldAttributes.Public);
        ctx.RegisterEnumTagField(e, tagField);

        var seenCaseNames = new HashSet<string>();

        for (var caseIndex = 0; caseIndex < e.Cases.Count; caseIndex++)
        {
            var enumCase = e.Cases[caseIndex];

            // Semantics/DeclarationBinder doesn't currently reject two
            // cases sharing a name (tracked as a discovered gap — see
            // this phase's README) — a duplicate would otherwise
            // silently collide on the factory-method name below, so
            // it's caught here defensively instead of producing a
            // broken assembly. Not a Diagnostic (CodeGen/ has no
            // diagnostic sink — see CodeGenerator's remarks): reaching
            // this means a real internal-consistency bug in an earlier
            // phase, not malformed-but-otherwise-valid user input.
            if (!seenCaseNames.Add(enumCase.Name))
            {
                throw new NotSupportedException(
                    $"enum '{e.Name}' has two cases named '{enumCase.Name}' — Semantics/ should reject this but doesn't yet (see CodeGen/README.md)");
            }

            ctx.RegisterEnumCaseTag(e, enumCase.Name, caseIndex);

            var slotFields = new List<FieldBuilder>();
            for (var slot = 0; slot < enumCase.AssociatedValues.Count; slot++)
            {
                var field = tb.DefineField($"_{enumCase.Name}_slot{slot}", ctx.ResolveClrType(enumCase.AssociatedValues[slot].Type), FieldAttributes.Public);
                slotFields.Add(field);
                ctx.RegisterEnumSlotField(e, enumCase.Name, slot, field);
            }

            EmitCaseFactory(ctx, e, enumCase, caseIndex, tagField, slotFields);
        }
    }

    /// <summary>
    /// A static factory method per case — e.g. <c>Shape.circle(double)
    /// -&gt; Shape</c> — since a single overloaded constructor can't
    /// disambiguate by case name, only by parameter types (which can
    /// collide across cases). Not yet reachable from any bound tree
    /// <c>Semantics/</c> can currently produce — there's no enum-case
    /// *construction* binding rule yet (ADR-0012's Consequences; also
    /// this phase's README) — but emitted regardless: it's part of the
    /// enum's CLR shape either way, and having it lets a test invoke it
    /// directly via reflection to exercise tag-check/associated-value
    /// codegen without waiting on that Semantics/ follow-up.
    /// </summary>
    private static void EmitCaseFactory(CodeGenContext ctx, EnumSymbol e, EnumCaseSymbol enumCase, int caseIndex, FieldBuilder tagField, IReadOnlyList<FieldBuilder> slotFields)
    {
        var tb = ctx.GetEnumTypeBuilder(e);
        var parameterTypes = enumCase.AssociatedValues.Select(v => ctx.ResolveClrType(v.Type)).ToArray();
        var factory = tb.DefineMethod(enumCase.Name, MethodAttributes.Public | MethodAttributes.Static, tb, parameterTypes);

        var il = factory.GetILGenerator();
        var result = il.DeclareLocal(tb);

        il.Emit(OpCodes.Ldloca, result);
        il.Emit(OpCodes.Ldc_I4, caseIndex);
        il.Emit(OpCodes.Stfld, tagField);

        for (var slot = 0; slot < slotFields.Count; slot++)
        {
            il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Ldarg, slot);
            il.Emit(OpCodes.Stfld, slotFields[slot]);
        }

        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);

        ctx.RegisterEnumCaseFactory(e, enumCase.Name, factory);
    }
}
