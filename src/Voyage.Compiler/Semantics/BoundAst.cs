using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Parsing;

namespace Voyage.Compiler.Semantics;

/// <summary>
/// Base type for every node in the "bound" tree — <c>Parsing/</c>'s AST
/// after name resolution and type checking. Mirrors
/// <c>Parsing.AstNode</c>'s own role: every bound node still carries its
/// source <see cref="Span"/>, so a later phase (<c>Lowering/</c>, or
/// diagnostics discovered post-binding) can still point at exact source.
/// Deliberately a separate hierarchy from <c>Parsing.AstNode</c> rather
/// than decorating the syntax nodes in place — a <c>Parsing.Expression</c>
/// has no type; a <see cref="BoundExpression"/> always does (see its own
/// remarks), and keeping the two hierarchies apart means
/// <c>Lowering/</c> only ever sees nodes that are known-well-typed,
/// never a raw syntax node that might still be semantically invalid.
/// </summary>
public abstract record BoundNode(SourceSpan Span);

// ----------------------------------------------------------------------
// Expressions
// ----------------------------------------------------------------------

/// <summary>
/// A type-checked expression. Every <see cref="BoundExpression"/> carries
/// a resolved <see cref="Type"/> — this is the entire point of binding an
/// expression at all. A subexpression that failed to type-check still
/// produces a <see cref="BoundExpression"/> (typically
/// <see cref="BoundErrorExpression"/>), typed as
/// <see cref="Semantics.ErrorType"/> rather than the binder giving up —
/// see <see cref="Semantics.ErrorType"/>'s remarks for why that matters
/// for diagnostic quality.
/// </summary>
public abstract record BoundExpression(TypeSymbol Type, SourceSpan Span) : BoundNode(Span);

public sealed record BoundIntegerLiteral(long Value, SourceSpan Span) : BoundExpression(PrimitiveType.Int, Span);

public sealed record BoundFloatLiteral(double Value, SourceSpan Span) : BoundExpression(PrimitiveType.Double, Span);

public sealed record BoundBooleanLiteral(bool Value, SourceSpan Span) : BoundExpression(PrimitiveType.Bool, Span);

public sealed record BoundStringLiteral(string Value, SourceSpan Span) : BoundExpression(PrimitiveType.String, Span);

/// <summary>
/// A type-checked interpolated string. Unlike
/// <c>Parsing.InterpolatedStringExpression</c>, there's no separate
/// text-vs-expression segment distinction here — every literal text run
/// is just bound as a <see cref="BoundStringLiteral"/> segment, so
/// <see cref="Segments"/> is uniformly a list of already-String-typed
/// expressions ready to concatenate. Every embedded expression segment
/// must itself bind to something ultimately convertible to
/// <c>String</c> — per ADR-0011's minimal subset, that means
/// <c>PrimitiveType.String</c> exactly; no <c>CustomStringConvertible</c>-
/// style protocol conformance exists yet to widen this.
/// </summary>
public sealed record BoundInterpolatedString(IReadOnlyList<BoundExpression> Segments, SourceSpan Span)
    : BoundExpression(PrimitiveType.String, Span);

/// <summary>A reference to a local <c>let</c>/<c>var</c> binding or
/// function parameter, resolved to exactly which
/// <see cref="VariableSymbol"/> it means.</summary>
public sealed record BoundVariableReference(VariableSymbol Variable, SourceSpan Span)
    : BoundExpression(Variable.Type, Span);

/// <summary>A call to a known top-level function, with every argument
/// already bound and checked against <see cref="Function"/>'s parameter
/// types in order — see <see cref="Binder"/>'s remarks on why call
/// binding is positional-only for now.</summary>
public sealed record BoundCall(FunctionSymbol Function, IReadOnlyList<BoundExpression> Arguments, SourceSpan Span)
    : BoundExpression(Function.ReturnType, Span);

/// <summary>
/// A struct construction, e.g. <c>Point(1, 2)</c>. Syntactically
/// identical to an ordinary call (<c>Parsing.CallExpression</c> doesn't
/// distinguish the two) — <see cref="Binder"/> tells them apart by
/// whether the callee name resolves to a <see cref="StructSymbol"/> or a
/// <see cref="FunctionSymbol"/>. <see cref="Arguments"/> binds
/// positionally against <see cref="Struct"/>'s
/// <see cref="StructSymbol.Properties"/> in declaration order — an
/// implicit memberwise initializer, since labeled call arguments aren't
/// implemented in <c>Parsing/</c> yet.
/// </summary>
public sealed record BoundStructConstruction(StructSymbol Struct, IReadOnlyList<BoundExpression> Arguments, SourceSpan Span)
    : BoundExpression(new StructType(Struct), Span);

/// <summary>A read of a struct value's stored property, e.g. the
/// <c>.x</c> in <c>point.x</c>. <see cref="Property"/> identifies which
/// of <see cref="Target"/>'s struct properties this is — resolved by
/// name against <see cref="Target"/>'s <see cref="StructType"/> during
/// binding, so later phases don't need to re-look-up the property by
/// string name.</summary>
public sealed record BoundPropertyAccess(BoundExpression Target, PropertySymbol Property, SourceSpan Span)
    : BoundExpression(Property.Type, Span);

public sealed record BoundBinary(BoundExpression Left, BinaryOperator Operator, BoundExpression Right, TypeSymbol Type, SourceSpan Span)
    : BoundExpression(Type, Span);

public sealed record BoundUnary(UnaryOperator Operator, BoundExpression Operand, TypeSymbol Type, SourceSpan Span)
    : BoundExpression(Type, Span);

/// <summary>Produced wherever an expression failed to bind or
/// type-check — see <see cref="Semantics.ErrorType"/>'s remarks. Carries
/// no further information: the diagnostic explaining *why* was already
/// reported at the point this was produced.</summary>
public sealed record BoundErrorExpression(SourceSpan Span) : BoundExpression(ErrorType.Instance, Span);

// ----------------------------------------------------------------------
// Patterns (switch/case)
// ----------------------------------------------------------------------

/// <summary>Base type for a bound <c>switch</c>-case pattern — mirrors
/// <c>Parsing.Pattern</c>'s role once resolved against the subject's
/// actual type.</summary>
public abstract record BoundPattern(SourceSpan Span) : BoundNode(Span);

public sealed record BoundWildcardPattern(SourceSpan Span) : BoundPattern(Span);

/// <summary>A <c>let name</c> binding pattern, resolved to the new
/// <see cref="VariableSymbol"/> it introduces — its
/// <see cref="VariableSymbol.Type"/> is already known from context (the
/// switch subject's type for a top-level binding pattern, or an enum
/// case's associated-value type for a nested one inside
/// <see cref="BoundEnumCasePattern"/>).</summary>
public sealed record BoundBindingPattern(VariableSymbol Variable, SourceSpan Span) : BoundPattern(Span);

/// <summary>An enum-case pattern, e.g. <c>.circle(let radius)</c>,
/// resolved to exactly which <see cref="EnumCaseSymbol"/> it matches.
/// <see cref="AssociatedValues"/> has the same length and order as
/// <see cref="EnumCaseSymbol.AssociatedValues"/> — <see cref="Binder"/>
/// diagnoses a mismatch rather than producing a shorter/longer
/// list.</summary>
public sealed record BoundEnumCasePattern(EnumCaseSymbol Case, IReadOnlyList<BoundPattern> AssociatedValues, SourceSpan Span)
    : BoundPattern(Span);

/// <summary>A pattern matched by value equality against a bound
/// expression, e.g. the <c>1</c> in <c>case 1:</c>.</summary>
public sealed record BoundExpressionPattern(BoundExpression Expression, SourceSpan Span) : BoundPattern(Span);

/// <summary>One <c>case ...:</c> clause of a bound
/// <see cref="BoundSwitch"/>. Not a <see cref="BoundStatement"/> itself —
/// same relationship <c>Parsing.SwitchCase</c> has to
/// <c>Parsing.SwitchStatement</c>.</summary>
public sealed record BoundSwitchCase(
    IReadOnlyList<BoundPattern> Patterns,
    BoundExpression? Guard,
    IReadOnlyList<BoundStatement> Body,
    SourceSpan Span) : BoundNode(Span);

// ----------------------------------------------------------------------
// Statements
// ----------------------------------------------------------------------

public abstract record BoundStatement(SourceSpan Span) : BoundNode(Span);

public sealed record BoundExpressionStatement(BoundExpression Expression, SourceSpan Span) : BoundStatement(Span);

/// <summary>A type-checked <c>let</c>/<c>var</c> binding. Unlike
/// <c>Parsing.BindingStatement</c>, <see cref="Initializer"/> being
/// absent doesn't mean "no initializer was written" on its own — a
/// binding with neither a declared type nor an initializer is rejected
/// during binding (same rule <c>Parsing/</c> already documents for
/// itself), so by the time this node exists <see cref="Variable"/>'s
/// type is always known one way or another.</summary>
public sealed record BoundVariableDeclaration(VariableSymbol Variable, BoundExpression? Initializer, SourceSpan Span)
    : BoundStatement(Span);

public sealed record BoundAssignment(BoundExpression Target, AssignmentOperator Operator, BoundExpression Value, SourceSpan Span)
    : BoundStatement(Span);

/// <summary>
/// A bound <c>if</c> statement. <see cref="Then"/>/<see cref="Else"/>
/// each get their own child <see cref="Scope"/> while binding (a
/// <c>let</c> inside one branch doesn't leak into the other, or past the
/// <c>if</c> entirely) — see <see cref="Scope"/>'s remarks on
/// per-block scoping.
/// </summary>
public sealed record BoundIf(BoundExpression Condition, IReadOnlyList<BoundStatement> Then, IReadOnlyList<BoundStatement>? Else, SourceSpan Span)
    : BoundStatement(Span);

public sealed record BoundWhile(BoundExpression Condition, IReadOnlyList<BoundStatement> Body, SourceSpan Span)
    : BoundStatement(Span);

/// <summary><c>break</c>/<c>continue</c> validity (must be lexically
/// inside a loop) is checked during binding — see
/// <see cref="Binder"/>'s loop-depth tracking — but nothing further
/// needs to be carried on the bound node itself, same as the unlabeled
/// syntax forms.</summary>
public sealed record BoundBreak(SourceSpan Span) : BoundStatement(Span);

public sealed record BoundContinue(SourceSpan Span) : BoundStatement(Span);

/// <summary>A bound <c>return</c>. <see cref="Value"/>'s type (or its
/// absence, for a bare <c>return</c>) is checked against the enclosing
/// function's declared return type during binding — mismatches are
/// diagnosed at the <c>return</c> site, not deferred to
/// <c>Lowering/</c>.</summary>
public sealed record BoundReturn(BoundExpression? Value, SourceSpan Span) : BoundStatement(Span);

public sealed record BoundSwitch(
    BoundExpression Subject,
    IReadOnlyList<BoundSwitchCase> Cases,
    IReadOnlyList<BoundStatement>? DefaultBody,
    SourceSpan Span) : BoundStatement(Span);

/// <summary>Produced wherever a statement failed to bind — same role as
/// <see cref="BoundErrorExpression"/>, and same reasoning for why it
/// exists rather than aborting the whole binding pass.</summary>
public sealed record BoundErrorStatement(SourceSpan Span) : BoundStatement(Span);

// ----------------------------------------------------------------------
// Top-level declarations
// ----------------------------------------------------------------------

/// <summary>A fully bound function: its already-resolved
/// <see cref="Symbol"/> (signature) paired with its type-checked
/// <see cref="Body"/>. Produced once per function by
/// <see cref="Binder"/>'s second pass, after
/// <see cref="DeclarationBinder"/>'s first pass has already resolved
/// every function's signature — see <see cref="DeclarationBinder"/>'s
/// remarks for why signatures resolve before any body does.</summary>
public sealed record BoundFunctionDeclaration(FunctionSymbol Symbol, IReadOnlyList<BoundStatement> Body, SourceSpan Span)
    : BoundNode(Span);

/// <summary>A fully bound struct. Per ADR-0011's minimal subset a struct
/// has no body to bind beyond its already-resolved
/// <see cref="Symbol"/> — stored-property types are resolved by
/// <see cref="DeclarationBinder"/>, and structs have no methods yet, so
/// there's nothing left for a second binding pass to do here.</summary>
public sealed record BoundStructDeclaration(StructSymbol Symbol, SourceSpan Span) : BoundNode(Span);

/// <summary>A fully bound enum — same "nothing left to bind" note as
/// <see cref="BoundStructDeclaration"/> applies; case shapes are
/// resolved by <see cref="DeclarationBinder"/> and enums have no methods
/// yet either.</summary>
public sealed record BoundEnumDeclaration(EnumSymbol Symbol, SourceSpan Span) : BoundNode(Span);

/// <summary>
/// The root of a bound source file — the <c>Semantics/</c> analogue of
/// <c>Parsing.CompilationUnit</c>. Declarations are split out by kind
/// (rather than kept as one mixed <c>IReadOnlyList&lt;BoundNode&gt;</c>,
/// the way <c>Parsing.CompilationUnit.Statements</c> is) because
/// <c>Lowering/</c> genuinely needs to process each kind differently
/// (e.g. emitting a CLR type per struct/enum vs. a CLR method per
/// function) — see <see cref="SemanticAnalyzer"/> for how a
/// <c>Parsing.CompilationUnit</c>'s single statement list gets sorted
/// into these on the way in.
/// </summary>
public sealed record BoundCompilationUnit(
    IReadOnlyList<BoundFunctionDeclaration> Functions,
    IReadOnlyList<BoundStructDeclaration> Structs,
    IReadOnlyList<BoundEnumDeclaration> Enums,
    IReadOnlyList<BoundStatement> TopLevelStatements,
    SourceSpan Span) : BoundNode(Span);
