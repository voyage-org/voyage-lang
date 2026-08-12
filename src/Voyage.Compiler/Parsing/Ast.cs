using Voyage.Compiler.Diagnostics;

namespace Voyage.Compiler.Parsing;

/// <summary>
/// Base type for every AST node. Every node carries its source span so
/// later phases (Semantics/, Lowering/) can build diagnostics that point
/// at exact source locations — this matters concretely for ADR-0008's
/// multi-span transitive-await diagnostic once Semantics/ exists.
/// </summary>
public abstract record AstNode(SourceSpan Span);

// ----------------------------------------------------------------------
// Expressions
// ----------------------------------------------------------------------

public abstract record Expression(SourceSpan Span) : AstNode(Span);

/// <summary>
/// Binary operators voyage-lang currently defines, per grammar.md's
/// "Operator Precedence and Associativity" table. Deliberately its own
/// enum rather than reusing Lexing.TokenKind directly — later phases
/// (Semantics/, Lowering/) shouldn't need to know lexer token
/// representations to reason about what operation an expression performs.
/// </summary>
public enum BinaryOperator
{
    Add, Subtract, Multiply, Divide, Modulo,
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    LogicalAnd, LogicalOr,
    NilCoalescing,
}

public enum UnaryOperator
{
    Negate,     // unary -
    LogicalNot, // !
}

/// <summary>A bare name reference, e.g. `print`, `x`.</summary>
public sealed record IdentifierExpression(string Name, SourceSpan Span) : Expression(Span);

/// <summary>
/// A non-interpolated string literal, e.g. `"Hello, Voyage."`.
/// Interpolated strings (`"\(...)"`) are not yet parsed — see this
/// file's remarks and Parsing/README.md for why that's an explicit,
/// tracked scope limit rather than an oversight.
/// </summary>
public sealed record StringLiteralExpression(string Value, SourceSpan Span) : Expression(Span);

public sealed record IntegerLiteralExpression(long Value, SourceSpan Span) : Expression(Span);

public sealed record FloatLiteralExpression(double Value, SourceSpan Span) : Expression(Span);

public sealed record BooleanLiteralExpression(bool Value, SourceSpan Span) : Expression(Span);

public sealed record NilLiteralExpression(SourceSpan Span) : Expression(Span);

/// <summary>A parenthesized expression, e.g. `(x)`. Kept as its own node
/// (rather than discarded during parsing) so a future pretty-printer or
/// source-preserving tool can round-trip explicit grouping.</summary>
public sealed record ParenthesizedExpression(Expression Inner, SourceSpan Span) : Expression(Span);

/// <summary>
/// A call expression, e.g. `print("Hello, Voyage.")`. This is the one
/// expression form the first Parsing/ milestone actually needs, since
/// the hello world sample is exactly one of these.
/// </summary>
public sealed record CallExpression(
    Expression Callee,
    IReadOnlyList<Expression> Arguments,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// A binary expression, e.g. `a + b`, `x == y`, `a ?? b`. Precedence and
/// associativity are baked into how the parser builds this tree (see
/// grammar.md's "Operator Precedence and Associativity" table) — by the
/// time a BinaryExpression exists, precedence has already been resolved
/// structurally; later phases don't need to re-derive it.
/// </summary>
public sealed record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right,
    SourceSpan Span) : Expression(Span);

/// <summary>A unary expression, e.g. `-x`, `!flag`.</summary>
public sealed record UnaryExpression(
    UnaryOperator Operator,
    Expression Operand,
    SourceSpan Span) : Expression(Span);

/// <summary>
/// A recovery placeholder produced where an expression was expected but
/// the parser couldn't make sense of what it found (or found a
/// not-yet-supported construct, e.g. string interpolation). Lets parsing
/// continue past a single bad expression rather than aborting the whole
/// file, matching the same "report and continue where safe" philosophy
/// the Lexer already follows for malformed input.
/// </summary>
public sealed record ErrorExpression(SourceSpan Span) : Expression(Span);

// ----------------------------------------------------------------------
// Statements
// ----------------------------------------------------------------------

public abstract record Statement(SourceSpan Span) : AstNode(Span);

/// <summary>
/// A statement that's just an expression evaluated for its effect, e.g.
/// `print("Hello, Voyage.")` as a top-level statement. This is the only
/// statement kind the first Parsing/ milestone implements — every other
/// statement form (let/var, if, func, etc.) is explicitly out of scope
/// until a later milestone, per Parsing/README.md.
/// </summary>
public sealed record ExpressionStatement(Expression Expression, SourceSpan Span) : Statement(Span);

/// <summary>
/// A `let`/`var` binding with an initializer, e.g. `let x = 10`. Per
/// grammar.md's "Parser implementation note" under Section 2, the
/// current parser milestone requires an initializer and does not yet
/// parse an explicit `: Type` annotation — both are real, spec'd
/// grammar, just not yet implemented here.
/// </summary>
public sealed record BindingStatement(
    bool IsMutable,
    string Name,
    Expression Initializer,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// Placeholder for a statement the parser recognized the start of but
/// doesn't yet know how to parse (e.g. `let`, `if`, `func` — anything
/// beyond a bare expression statement). Rather than crashing or silently
/// dropping content, the parser reports a diagnostic and produces one of
/// these, carrying the span of what it skipped, so a file mixing
/// already-supported and not-yet-supported constructs still parses as
/// far as it can.
/// </summary>
public sealed record UnsupportedStatement(SourceSpan Span) : Statement(Span);

// ----------------------------------------------------------------------
// Root
// ----------------------------------------------------------------------

/// <summary>The root AST node for a single source file.</summary>
public sealed record CompilationUnit(IReadOnlyList<Statement> Statements, SourceSpan Span) : AstNode(Span);
