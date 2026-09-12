using Voyage.Compiler.Parsing;

namespace Voyage.Compiler.Semantics;

/// <summary>
/// Base type for anything a <see cref="Scope"/> can hold under a name:
/// a local variable, a function, a struct, an enum, an enum case, or a
/// struct's stored property. One shared base (rather than separate
/// per-kind dictionaries in <see cref="Scope"/>) so name lookup is a
/// single uniform operation regardless of what kind of thing the name
/// turns out to refer to — <see cref="Binder"/> pattern-matches on the
/// returned <see cref="Symbol"/> subtype to decide what's actually valid
/// in a given syntactic position (e.g. a <see cref="StructSymbol"/>
/// looked up where an expression was expected is a call/construction;
/// looked up in a type position, it's the struct's type).
/// </summary>
public abstract record Symbol(string Name);

/// <summary>A <c>let</c>/<c>var</c> local binding, or a function
/// parameter — see <see cref="ParameterSymbol"/> for why parameters get
/// their own subtype instead of reusing this one directly.</summary>
public sealed record VariableSymbol(string Name, TypeSymbol Type, bool IsMutable) : Symbol(Name);

/// <summary>
/// A function parameter. Kept distinct from <see cref="VariableSymbol"/>
/// (rather than every parameter just becoming one) so
/// <see cref="FunctionSymbol.Parameters"/> can carry
/// <see cref="ExternalLabel"/> without every ordinary local variable
/// needing a meaningless always-null field for it. Per ADR-0011's
/// minimal subset, argument binding at a call site is positional only —
/// <see cref="ExternalLabel"/> is carried through from
/// <c>Parsing.Parameter</c> for when labeled-call-argument binding is
/// implemented later, but <see cref="Binder"/> doesn't consult it yet.
/// </summary>
public sealed record ParameterSymbol(string Name, string? ExternalLabel, TypeSymbol Type) : Symbol(Name)
{
    /// <summary>Treats a parameter as a plain immutable local inside the
    /// function body — voyage-lang parameters are <c>let</c>-like unless
    /// declared <c>inout</c>/<c>mutating</c>, neither of which the
    /// minimal subset supports yet.</summary>
    public VariableSymbol AsVariable() => new(Name, Type, IsMutable: false);
}

/// <summary>
/// A function declaration's signature, once resolved. Holds the original
/// <c>Parsing.FunctionDeclaration</c> so <see cref="Binder"/> can bind
/// the body against the already-known signature in a second pass — see
/// <see cref="DeclarationBinder"/>'s remarks for why signatures and
/// bodies are resolved in separate passes at all.
/// </summary>
public sealed record FunctionSymbol(
    string Name,
    IReadOnlyList<ParameterSymbol> Parameters,
    TypeSymbol ReturnType,
    FunctionDeclaration Declaration) : Symbol(Name)
{
    public FunctionType ToFunctionType() => new(Parameters.Select(p => (TypeSymbol)p.Type).ToList(), ReturnType);
}

/// <summary>A struct's stored property, e.g. the <c>x: Double</c> in
/// <c>struct Point { var x: Double }</c>. Per ADR-0011's minimal subset,
/// structs have stored properties only — no computed properties, no
/// methods yet (tracked as the same parser gap that blocks parsing
/// computed-property accessors at all).</summary>
public sealed record PropertySymbol(string Name, TypeSymbol Type, bool IsMutable) : Symbol(Name);

/// <summary>
/// A struct declaration's shape, once resolved: its stored properties in
/// declaration order. That order matters concretely — per ADR-0011's
/// minimal subset (no labeled call arguments yet), constructing a struct
/// value binds positional call arguments to <see cref="Properties"/> in
/// this exact order (an implicit memberwise initializer), the same way
/// <see cref="FunctionSymbol.Parameters"/> order matters for an ordinary
/// call.
/// </summary>
public sealed record StructSymbol(
    string Name,
    IReadOnlyList<PropertySymbol> Properties,
    StructDeclaration Declaration) : Symbol(Name);

/// <summary>
/// One associated-value slot of an <see cref="EnumCaseSymbol"/>, e.g. the
/// <c>radius: Double</c> in <c>case circle(radius: Double)</c>, or an
/// unlabeled <c>T</c> in <c>case some(T)</c> (<see cref="Label"/> null).
/// Not itself a <see cref="Symbol"/> — it only ever exists as an entry
/// in <see cref="EnumCaseSymbol.AssociatedValues"/>, and never gets
/// looked up by name on its own the way a real symbol does.
/// </summary>
public sealed record AssociatedValueSlot(string? Label, TypeSymbol Type);

/// <summary>A single <c>case</c> inside an <see cref="EnumSymbol"/>,
/// e.g. <c>circle(radius: Double)</c> or a bare <c>none</c> with no
/// associated values (<see cref="AssociatedValues"/> empty).</summary>
public sealed record EnumCaseSymbol(string Name, IReadOnlyList<AssociatedValueSlot> AssociatedValues) : Symbol(Name);

/// <summary>An enum declaration's shape, once resolved: its cases, each
/// with its associated-value types already resolved via
/// <see cref="TypeResolver"/>.</summary>
public sealed record EnumSymbol(
    string Name,
    IReadOnlyList<EnumCaseSymbol> Cases,
    EnumDeclaration Declaration) : Symbol(Name)
{
    public EnumCaseSymbol? FindCase(string name) => Cases.FirstOrDefault(c => c.Name == name);
}
