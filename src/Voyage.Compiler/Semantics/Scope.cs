namespace Voyage.Compiler.Semantics;

/// <summary>
/// A single lexical scope: a name→<see cref="Symbol"/> table, chained to
/// an enclosing <see cref="Parent"/> scope. Mutable by design (unlike the
/// rest of Semantics/, which favors immutable records) — a scope is
/// inherently a stateful thing being built up statement-by-statement as
/// <see cref="Binder"/> walks a block (each <c>let</c> adds to the
/// current scope, and everything after it can see it; nothing before it
/// could). One <see cref="Scope"/> instance is created per block (a
/// function body, an <c>if</c>/<c>while</c> body, a <c>switch</c> case
/// body) via <see cref="CreateChild"/>, so a name declared inside an
/// inner block doesn't leak into its enclosing one once that block ends
/// — the caller simply stops using the child scope and reverts to using
/// the parent again.
/// </summary>
public sealed class Scope(Scope? parent)
{
    private readonly Dictionary<string, Symbol> _symbols = [];

    public Scope? Parent { get; } = parent;

    /// <summary>Adds <paramref name="symbol"/> to this scope. Returns
    /// false on a name already declared in *this* scope specifically
    /// (shadowing a parent scope's name is allowed and returns true —
    /// only a same-scope redeclaration, e.g. two <c>let x</c> in one
    /// block, is a conflict). Callers are expected to turn a false
    /// return into a diagnostic; <see cref="Scope"/> itself doesn't know
    /// about <see cref="Diagnostics.IDiagnosticSink"/> or have a source
    /// span to report against.</summary>
    public bool TryDeclare(Symbol symbol) => _symbols.TryAdd(symbol.Name, symbol);

    /// <summary>Looks up <paramref name="name"/> in this scope, then
    /// each enclosing scope in turn — the standard lexical-scoping walk,
    /// so an inner block sees everything an outer one declared.</summary>
    public bool TryLookup(string name, out Symbol symbol)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._symbols.TryGetValue(name, out var found))
            {
                symbol = found;
                return true;
            }
        }

        symbol = null!;
        return false;
    }

    /// <summary>Starts a new nested scope whose parent is this one —
    /// see the class remarks for when callers should do this (once per
    /// block entered while binding statements).</summary>
    public Scope CreateChild() => new(this);
}
