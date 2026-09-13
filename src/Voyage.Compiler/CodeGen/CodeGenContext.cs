using System.Reflection;
using System.Reflection.Emit;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.CodeGen;

/// <summary>
/// Per-compilation mutable state: every symbol-to-CLR-builder mapping
/// <see cref="TypeEmitter"/>, <see cref="StatementEmitter"/>, and
/// <see cref="ExpressionEmitter"/> need to share. One instance is built
/// per <see cref="CodeGenerator.Emit"/> call and threaded through every
/// emission call — the same role <see cref="Scope"/> plays during
/// binding, except mapping symbols to Reflection.Emit builders instead
/// of to other symbols.
///
/// Registrations are populated during <see cref="TypeEmitter"/>'s
/// declaration pass (before any method body is emitted), mirroring
/// <see cref="DeclarationBinder"/>'s own "resolve every signature before
/// any body" ordering — see <see cref="TypeEmitter"/>'s remarks for
/// specifics.
/// </summary>
public sealed class CodeGenContext(ModuleBuilder module)
{
    public ModuleBuilder Module { get; } = module;

    /// <summary>The reflected <c>Console.WriteLine(string)</c> —
    /// voyage-lang's <c>print</c> builtin (see
    /// <c>DeclarationBinder.BuiltinPrintSymbol</c>) has no
    /// <c>Parsing.FunctionDeclaration</c> to emit a body for; it's bound
    /// directly to this instead.</summary>
    public MethodInfo PrintMethod { get; } = typeof(Console).GetMethod("WriteLine", [typeof(string)])!;

    private readonly Dictionary<StructSymbol, TypeBuilder> _structTypes = [];
    private readonly Dictionary<StructSymbol, ConstructorBuilder> _structConstructors = [];
    private readonly Dictionary<(StructSymbol Struct, string PropertyName), FieldBuilder> _structFields = [];

    private readonly Dictionary<EnumSymbol, TypeBuilder> _enumTypes = [];
    private readonly Dictionary<EnumSymbol, FieldBuilder> _enumTagFields = [];
    private readonly Dictionary<(EnumSymbol Enum, string CaseName), int> _enumCaseTags = [];
    private readonly Dictionary<(EnumSymbol Enum, string CaseName, int Slot), FieldBuilder> _enumSlotFields = [];
    private readonly Dictionary<(EnumSymbol Enum, string CaseName), MethodBuilder> _enumCaseFactories = [];

    private readonly Dictionary<FunctionSymbol, MethodBuilder> _functions = [];

    // -- Struct registration/lookup --

    public void RegisterStructType(StructSymbol s, TypeBuilder tb) => _structTypes[s] = tb;
    public TypeBuilder GetStructTypeBuilder(StructSymbol s) => _structTypes[s];

    public void RegisterStructField(StructSymbol s, string propertyName, FieldBuilder fb) => _structFields[(s, propertyName)] = fb;
    public FieldBuilder GetStructField(StructSymbol s, string propertyName) => _structFields[(s, propertyName)];

    public void RegisterStructConstructor(StructSymbol s, ConstructorBuilder ctor) => _structConstructors[s] = ctor;
    public ConstructorBuilder GetStructConstructor(StructSymbol s) => _structConstructors[s];

    // -- Enum registration/lookup --

    public void RegisterEnumType(EnumSymbol e, TypeBuilder tb) => _enumTypes[e] = tb;
    public TypeBuilder GetEnumTypeBuilder(EnumSymbol e) => _enumTypes[e];

    public void RegisterEnumTagField(EnumSymbol e, FieldBuilder fb) => _enumTagFields[e] = fb;
    public FieldBuilder GetEnumTagField(EnumSymbol e) => _enumTagFields[e];

    public void RegisterEnumCaseTag(EnumSymbol e, string caseName, int tag) => _enumCaseTags[(e, caseName)] = tag;
    public int GetEnumCaseTag(EnumSymbol e, string caseName) => _enumCaseTags[(e, caseName)];

    public void RegisterEnumSlotField(EnumSymbol e, string caseName, int slot, FieldBuilder fb) => _enumSlotFields[(e, caseName, slot)] = fb;
    public FieldBuilder GetEnumSlotField(EnumSymbol e, string caseName, int slot) => _enumSlotFields[(e, caseName, slot)];

    /// <summary>The static per-case factory method (see
    /// <see cref="TypeEmitter"/>'s remarks) — not yet reachable from any
    /// bound tree Semantics/ can currently produce (see ADR-0012's
    /// Consequences), but registered and emitted regardless so it can be
    /// invoked directly via reflection for testing, and so it's ready
    /// once Semantics/ gains a construction-binding rule.</summary>
    public void RegisterEnumCaseFactory(EnumSymbol e, string caseName, MethodBuilder mb) => _enumCaseFactories[(e, caseName)] = mb;
    public MethodBuilder GetEnumCaseFactory(EnumSymbol e, string caseName) => _enumCaseFactories[(e, caseName)];

    // -- Function registration/lookup --

    public void RegisterFunction(FunctionSymbol f, MethodBuilder mb) => _functions[f] = mb;
    public MethodBuilder GetFunction(FunctionSymbol f) => _functions[f];

    /// <summary>
    /// Maps a resolved <see cref="TypeSymbol"/> to the CLR
    /// <see cref="Type"/> it lowers to, per <c>type-system.md</c>
    /// Section 6's primitive table and ADR-0012's struct/enum decision.
    /// A <see cref="StructType"/>/<see cref="EnumType"/> resolves to its
    /// (possibly still-being-built) <see cref="TypeBuilder"/> — safe to
    /// use as a field/parameter/return type before that
    /// <c>TypeBuilder</c> is baked via <c>CreateType()</c>, which is
    /// exactly why <see cref="TypeEmitter"/> defines every struct/enum
    /// shell before emitting any field or method that references one.
    /// </summary>
    public Type ResolveClrType(TypeSymbol type) => type switch
    {
        PrimitiveType { Name: "Int" } => typeof(long),
        PrimitiveType { Name: "Double" } => typeof(double),
        PrimitiveType { Name: "Bool" } => typeof(bool),
        PrimitiveType { Name: "String" } => typeof(string),
        PrimitiveType { Name: "Void" } => typeof(void),
        PrimitiveType p => throw new NotSupportedException($"unknown primitive type '{p.Name}' — CodeGenContext.ResolveClrType needs updating"),
        StructType s => GetStructTypeBuilder(s.Symbol),
        Semantics.EnumType e => GetEnumTypeBuilder(e.Symbol),
        _ => throw new NotSupportedException($"no CLR mapping for type '{type}' (kind '{type.GetType().Name}') — likely a type Semantics/ resolves but CodeGen/ doesn't emit yet"),
    };
}
