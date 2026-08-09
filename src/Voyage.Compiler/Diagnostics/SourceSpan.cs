namespace Voyage.Compiler.Diagnostics;

/// <summary>
/// A single position in source text. 1-based line and column, matching
/// how editors and most compiler diagnostics report positions to humans.
/// </summary>
public readonly record struct SourceLocation(int Line, int Column)
{
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>
/// A span of source text, from an inclusive start to an exclusive end.
/// Used by every phase (Lexing/, Parsing/, Semantics/, Lowering/) so
/// diagnostics can point at exact source ranges — this matters
/// concretely for ADR-0008's transitive-await diagnostic, which needs to
/// highlight a full multi-hop call path, not just a single point.
/// </summary>
public readonly record struct SourceSpan(SourceLocation Start, SourceLocation End)
{
    public static SourceSpan At(SourceLocation location) => new(location, location);

    public override string ToString() => $"{Start}-{End}";
}
