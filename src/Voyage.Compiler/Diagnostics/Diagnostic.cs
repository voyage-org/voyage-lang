namespace Voyage.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A single compiler diagnostic. Every phase (Lexing/, Parsing/,
/// Semantics/, Lowering/) constructs these rather than throwing raw
/// exceptions or printing directly, per ADR-0010 — this is what keeps
/// error formatting consistent regardless of which phase detected the
/// problem, and is what ADR-0008's multi-line transitive-await
/// diagnostic (a single Diagnostic with a message plus a list of related
/// spans forming a call path) will build on.
/// </summary>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    SourceSpan Span)
{
    public override string ToString()
    {
        var tag = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"{tag} [{Span.Start}]: {Message}";
    }
}

/// <summary>
/// Where a phase reports diagnostics. A trivial in-memory sink is enough
/// for the current Lexing/-only milestone; Diagnostics/ proper (rich
/// formatting, source-line rendering, multi-span call-path output for
/// ADR-0008) is a later, more involved piece of work, deliberately not
/// built out yet — see ADR-0010.
/// </summary>
public interface IDiagnosticSink
{
    void Report(Diagnostic diagnostic);
}

public sealed class InMemoryDiagnosticSink : IDiagnosticSink
{
    private readonly List<Diagnostic> _diagnostics = [];

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);
}
