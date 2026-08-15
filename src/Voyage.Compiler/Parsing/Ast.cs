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
// Types (minimal — see FunctionDeclaration remarks for current scope)
// ----------------------------------------------------------------------

/// <summary>
/// A minimal named type reference, e.g. `Int`, `String`, `String?`.
/// Scope-limited to a bare identifier with an optional trailing `?`
/// (Optional&lt;T&gt; sugar) — generic type arguments (`Array&lt;T&gt;`),
/// array sugar (`[T]`), function types (`(Int) -&gt; String`), and
/// keyword-spelled type forms (`Self`, `any P`, `some P`, `Optional&lt;T&gt;`
/// written out) are not yet parsed. This is real, spec'd grammar
/// (type-system.md), just not yet implemented here — see
/// Parsing/README.md.
/// </summary>
public sealed record TypeNode(string Name, bool IsOptional, SourceSpan Span) : AstNode(Span);

/// <summary>
/// A single function parameter, e.g. `a: Int` in `func add(a: Int, ...)`.
/// Scope-limited to "name: Type" — Swift-style external parameter
/// labels (a separate external label, or `_` to suppress it entirely,
/// as in `func identity&lt;T&gt;(_ value: T) -&gt; T`) are not yet parsed;
/// the declared name currently serves as both internal and external
/// name. See Parsing/README.md.
/// </summary>
public sealed record Parameter(string Name, TypeNode Type, SourceSpan Span) : AstNode(Span);

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
/// Assignment operators voyage-lang currently defines: plain `=` plus
/// the compound arithmetic forms already lexed (`+=`, `-=`, `*=`, `/=`).
/// A dedicated enum for the same reason <see cref="BinaryOperator"/> is
/// — later phases shouldn't need lexer token knowledge to reason about
/// what an assignment does.
/// </summary>
public enum AssignmentOperator
{
    Assign, AddAssign, SubtractAssign, MultiplyAssign, DivideAssign,
}

/// <summary>
/// An assignment statement, e.g. `x = 0`, `x += 1`. <see cref="Target"/>
/// is a full <see cref="Expression"/>, not just an identifier — this is
/// deliberate: it means once member access (`.`) or subscripting (`[]`)
/// are added, `self.x = 0` or `items[0] = 0` become valid Target shapes
/// with no change needed here. Whether a given Target expression is
/// actually a valid *assignable* thing (an lvalue) is a `Semantics/`
/// question, not a `Parsing/` one — e.g. `f() = 0` parses today (nothing
/// stops a call expression from being the Target syntactically) but
/// isn't semantically meaningful; that check belongs downstream.
/// </summary>
public sealed record AssignmentStatement(
    Expression Target,
    AssignmentOperator Operator,
    Expression Value,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// A `let`/`var` binding, e.g. `let x = 10`, `var x: Double`, or
/// `let z: Int = 30`. At least one of <see cref="DeclaredType"/> or
/// <see cref="Initializer"/> must be present — a binding with neither
/// (bare `let x`) has no way to determine its type and is not valid
/// grammar; the parser treats that case as unsupported (see
/// Parsing/README.md).
/// </summary>
public sealed record BindingStatement(
    bool IsMutable,
    string Name,
    TypeNode? DeclaredType,
    Expression? Initializer,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// A function declaration, e.g. `func add(a: Int, b: Int) -&gt; Int { return a + b }`.
/// `ReturnType` is null when no `-&gt; Type` is written, meaning an
/// implicit Void return. A single-expression body (ADR-0005 implicit
/// return, e.g. `func greet() -&gt; String { "hi" }`) is just a
/// one-element `Body` holding an `ExpressionStatement` — the parser
/// does not special-case it; turning that last expression into a
/// return is Lowering's job, per ADR-0005. Generic parameters
/// (`&lt;T&gt;`/`where`) are not yet parsed — see Parsing/README.md.
/// </summary>
public sealed record FunctionDeclaration(
    string Name,
    IReadOnlyList<Parameter> Parameters,
    TypeNode? ReturnType,
    IReadOnlyList<Statement> Body,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// A `struct` declaration, e.g. `struct Point { var x: Double; var y:
/// Double }`. `Members` reuses the same block-statement parsing as
/// function bodies — `var`/`let` properties and `func` methods are both
/// just statements per <see cref="ParseBlockStatements"/>'s existing
/// dispatch, so no new member-parsing infrastructure was needed here.
/// `Protocol` conformance clauses (`struct Point: Drawable { ... }`) are
/// not yet parsed — see Parsing/README.md.
/// </summary>
public sealed record StructDeclaration(
    string Name,
    IReadOnlyList<Statement> Members,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// An `enum` declaration, e.g. `enum Shape { case circle(radius: Double)
/// case rectangle(width: Double, height: Double) }`. `Members` is
/// typically a list of <see cref="CaseDeclaration"/>s, though the parser
/// doesn't restrict it to only cases — a `func` method inside an `enum`
/// parses the same way it would inside a `struct`, since both reuse
/// `ParseBlockStatements`. Whether that's actually valid voyage-lang is
/// a `Semantics/` question, not a `Parsing/` one — this parser stays
/// permissive about *shape* and leaves *validity* to the phase whose job
/// that is.
/// </summary>
public sealed record EnumDeclaration(
    string Name,
    IReadOnlyList<Statement> Members,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// A single `case` inside an `enum` body, e.g. `case circle(radius:
/// Double)` or a bare `case none` with no associated values.
/// `AssociatedValues` reuses <see cref="Parameter"/> directly — an
/// associated-value list has the exact same shape as a function
/// parameter list (`name: Type, name: Type, ...`), so no new node type
/// was needed for it. Swift's comma-separated multi-case shorthand
/// (`case a, b, c`) is not yet supported — one `case` per declaration
/// only.
/// </summary>
public sealed record CaseDeclaration(
    string Name,
    IReadOnlyList<Parameter> AssociatedValues,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// A `return` statement, e.g. `return a + b` or a bare `return` with no value.
/// </summary>
public sealed record ReturnStatement(Expression? Value, SourceSpan Span) : Statement(Span);

/// <summary>
/// An `if` statement, with an optional else branch. `else if` chains are
/// represented by the else branch being a single-element list holding
/// another `IfStatement` — the standard desugaring (`else if X` is
/// exactly `else { if X { ... } }`), so `Semantics/`/`Lowering/` don't
/// need a separate "else-if" concept.
/// `if`-as-expression (grammar.md Section 3's implicit-return form, e.g.
/// `if cond { "a" } else { "b" }` used as a function body) is not
/// special-cased here — same as `FunctionDeclaration`, a branch holding
/// exactly one `ExpressionStatement` is what that form parses to, and
/// turning it into a value is Lowering's job per ADR-0005.
/// </summary>
public sealed record IfStatement(
    Expression Condition,
    IReadOnlyList<Statement> ThenBranch,
    IReadOnlyList<Statement>? ElseBranch,
    SourceSpan Span) : Statement(Span);

/// <summary>A `while` loop.</summary>
public sealed record WhileStatement(
    Expression Condition,
    IReadOnlyList<Statement> Body,
    SourceSpan Span) : Statement(Span);

/// <summary>A bare `break` statement. Labeled break (`break outerLoop`) is
/// not yet supported — voyage-lang doesn't have loop labels yet.</summary>
public sealed record BreakStatement(SourceSpan Span) : Statement(Span);

/// <summary>A bare `continue` statement. Same labeled-loop caveat as
/// <see cref="BreakStatement"/>.</summary>
public sealed record ContinueStatement(SourceSpan Span) : Statement(Span);

/// <summary>
/// Placeholder for a statement the parser recognized the start of but
/// doesn't yet know how to parse (e.g. `switch`, `for`, `guard` —
/// anything beyond what's listed in Parsing/README.md's current scope).
/// Rather than crashing or silently dropping content, the parser reports
/// a diagnostic and produces one of these, carrying the span of what it
/// skipped, so a file mixing already-supported and not-yet-supported
/// constructs still parses as far as it can.
/// </summary>
public sealed record UnsupportedStatement(SourceSpan Span) : Statement(Span);

// ----------------------------------------------------------------------
// Root
// ----------------------------------------------------------------------

/// <summary>The root AST node for a single source file.</summary>
public sealed record CompilationUnit(IReadOnlyList<Statement> Statements, SourceSpan Span) : AstNode(Span);
