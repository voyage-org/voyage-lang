namespace Voyage.Compiler.Semantics;

/// <summary>
/// A resolved, semantic type — what a <c>Parsing.TypeNode</c> (or an
/// inferred expression) actually refers to once name resolution has run.
/// Deliberately a separate hierarchy from <c>Parsing.TypeNode</c>: a
/// <c>TypeNode</c> is syntax ("the identifier <c>Int</c> appeared here");
/// a <c>TypeSymbol</c> is semantics ("that identifier resolved to the
/// built-in Int type", or "failed to resolve, so this is ErrorType").
/// <see cref="TypeResolver"/> is what turns one into the other.
/// </summary>
public abstract record TypeSymbol
{
    public abstract string Name { get; }

    public override string ToString() => Name;
}

/// <summary>
/// One of voyage-lang's built-in scalar types. Per type-system.md's
/// primitive-to-CLR mapping table, but Semantics/ only needs the type
/// identity here — the actual CLR mapping is Lowering/'s concern, not
/// this phase's.
/// </summary>
public sealed record PrimitiveType : TypeSymbol
{
    private PrimitiveType(string name) => Name = name;

    public override string Name { get; }

    public static readonly PrimitiveType Int = new("Int");
    public static readonly PrimitiveType Double = new("Double");
    public static readonly PrimitiveType Bool = new("Bool");
    public static readonly PrimitiveType String = new("String");

    /// <summary>The implicit return type of a function with no
    /// <c>-&gt; Type</c> written, and the type of a statement with no
    /// useful value (e.g. an assignment). Not itself a value type a
    /// binding can hold — <see cref="Binder"/> rejects
    /// <c>let x = someVoidCall()</c>.</summary>
    public static readonly PrimitiveType Void = new("Void");
}

/// <summary>
/// A user-declared <c>struct</c>'s type. Two <see cref="StructType"/>s
/// are the same type iff they wrap the same <see cref="StructSymbol"/>
/// instance (nominal typing, not structural) — record value-equality
/// gives us that for free since <see cref="StructSymbol"/> is itself a
/// record and every struct is declared (and thus symbol-created) exactly
/// once per <see cref="DeclarationBinder"/> pass.
/// </summary>
public sealed record StructType(StructSymbol Symbol) : TypeSymbol
{
    public override string Name => Symbol.Name;
}

/// <summary>A user-declared <c>enum</c>'s type. Same nominal-typing note
/// as <see cref="StructType"/> applies.</summary>
public sealed record EnumType(EnumSymbol Symbol) : TypeSymbol
{
    public override string Name => Symbol.Name;
}

/// <summary>
/// A function's type, e.g. <c>(Int, Int) -&gt; Int</c>. Used both for
/// looking up what a <see cref="FunctionSymbol"/> can be called with, and
/// as the type of a bare function reference used as a value — the latter
/// isn't exercised by the ADR-0011 minimal subset yet (no function
/// values/closures), but the type exists now so it doesn't need
/// retrofitting later.
/// </summary>
public sealed record FunctionType(IReadOnlyList<TypeSymbol> ParameterTypes, TypeSymbol ReturnType) : TypeSymbol
{
    public override string Name =>
        $"({string.Join(", ", ParameterTypes.Select(p => p.Name))}) -> {ReturnType.Name}";
}

/// <summary>
/// Sentinel type meaning "this expression's type is unknown because
/// something about it already failed to type-check." Distinguishing this
/// from a real type is what stops one bad expression from producing a
/// cascade of unrelated-looking diagnostics about everything downstream
/// of it — once a subexpression is <see cref="ErrorType"/>, every check
/// involving it is allowed to pass silently rather than report a second,
/// misleading error. A single shared instance, not a real type identity,
/// since "erroneous" isn't a type any two error expressions need to
/// agree on.
/// </summary>
public sealed record ErrorType : TypeSymbol
{
    public static readonly ErrorType Instance = new();
    public override string Name => "<error>";
}
