using Voyage.Compiler.Diagnostics;

namespace Voyage.Compiler.Lexing;

/// <summary>
/// A single lexical token: its kind, the exact source text it came from
/// (the lexeme), its source span, and — for literals — a parsed value
/// ready for Semantics/Lowering to use directly rather than re-parsing
/// the lexeme text later.
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    string Text,
    SourceSpan Span,
    object? LiteralValue = null)
{
    public override string ToString() =>
        LiteralValue is null
            ? $"{Kind} '{Text}' @ {Span}"
            : $"{Kind} '{Text}' ({LiteralValue}) @ {Span}";
}
