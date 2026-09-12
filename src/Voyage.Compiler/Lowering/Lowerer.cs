using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Parsing;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.Lowering;

/// <summary>
/// The <c>Lowering/</c> phase entry point — see <see cref="LoweredEnumTagCheck"/>'s
/// remarks for why this transforms a <c>Semantics.BoundCompilationUnit</c>
/// in place rather than producing a separate IR type. Performs exactly
/// two rewrites, both required by ADR-0011's minimal subset:
///
/// 1. <b>Implicit-return injection</b> (ADR-0005) — a function whose
///    entire body is one bare expression statement (no <c>return</c>
///    written) has that expression's value returned implicitly, same as
///    Swift's single-expression-function-body sugar. Deliberately
///    narrow in scope: this only recognizes the exact shape
///    <c>Parsing/</c> already documents for it (the *entire* body is
///    one <c>ExpressionStatement</c>) — it does not attempt general
///    "insert a <c>return</c> wherever control would otherwise fall off
///    the end of a multi-statement body" reachability analysis. A
///    multi-statement, non-<c>Void</c> function that doesn't return on
///    every path is not currently caught anywhere in the pipeline
///    (tracked as an open gap — see this phase's <c>README.md</c>).
///
/// 2. <b>Switch/pattern-match desugaring</b> — a <c>BoundSwitch</c> is
///    rewritten into an equivalent chain of ordinary <c>BoundIf</c>
///    statements, so <c>CodeGen/</c> never needs to know what a pattern
///    is at all — only how to emit an <c>if</c>. See
///    <see cref="CompilePattern"/> for how each pattern kind becomes a
///    boolean test (plus, for a binding, a variable declaration to
///    prepend to the matched branch).
/// </summary>
public sealed class Lowerer(IDiagnosticSink diagnostics)
{
    public BoundCompilationUnit Lower(BoundCompilationUnit unit)
    {
        var functions = unit.Functions
            .Select(f => new BoundFunctionDeclaration(f.Symbol, InjectImplicitReturn(f.Symbol, LowerStatements(f.Body)), f.Span))
            .ToList();

        var topLevel = LowerStatements(unit.TopLevelStatements);

        // Structs/enums carry no statements of their own to lower (see
        // BoundStructDeclaration/BoundEnumDeclaration's own remarks —
        // "nothing left to bind" applies equally to "nothing left to
        // lower" until they have methods), so they pass through as-is.
        return new BoundCompilationUnit(functions, unit.Structs, unit.Enums, topLevel, unit.Span);
    }

    /// <summary>Applies implicit-return injection to a single function
    /// body — see the class remarks for the exact (narrow) shape this
    /// recognizes.</summary>
    private static List<BoundStatement> InjectImplicitReturn(FunctionSymbol function, List<BoundStatement> body)
    {
        if (function.ReturnType != PrimitiveType.Void && body is [BoundExpressionStatement stmt])
        {
            return [new BoundReturn(stmt.Expression, stmt.Span)];
        }

        return body;
    }

    /// <summary>
    /// Recursively lowers a statement list: passes almost everything
    /// through unchanged, but rewrites a <c>BoundSwitch</c> wherever one
    /// appears (whether directly in this list, or nested inside an
    /// <c>if</c>/<c>while</c> body reached from it) into its desugared
    /// <c>BoundIf</c>-chain form. Recursing into <c>BoundIf</c>/
    /// <c>BoundWhile</c> bodies here — rather than only handling a
    /// top-level switch — is what makes a <c>switch</c> nested inside
    /// either one lower correctly too.
    /// </summary>
    private List<BoundStatement> LowerStatements(IReadOnlyList<BoundStatement> statements) =>
        statements.SelectMany(LowerStatement).ToList();

    private IEnumerable<BoundStatement> LowerStatement(BoundStatement statement) => statement switch
    {
        BoundIf s => [new BoundIf(s.Condition, LowerStatements(s.Then), s.Else is null ? null : LowerStatements(s.Else), s.Span)],
        BoundWhile s => [new BoundWhile(s.Condition, LowerStatements(s.Body), s.Span)],
        BoundSwitch s => LowerSwitch(s),

        // BoundExpressionStatement, BoundVariableDeclaration,
        // BoundAssignment, BoundReturn, BoundBreak, BoundContinue,
        // BoundErrorStatement: none of these contain nested statement
        // lists that could hide a switch, and none need rewriting
        // themselves — passed through unchanged.
        _ => [statement],
    };

    private List<BoundStatement> LowerSwitch(BoundSwitch s)
    {
        var fallback = s.DefaultBody is null ? [] : LowerStatements(s.DefaultBody);
        return BuildCaseChain(s.Subject, s.Cases, index: 0, fallback, s.Span);
    }

    /// <summary>
    /// Builds the <c>BoundIf</c> chain for cases starting at
    /// <paramref name="index"/>, recursing to build the "rest of the
    /// chain" first so it can be reused in two places when a case has a
    /// <c>where</c> guard — see the guard-handling branch below for why
    /// that reuse (rather than a single flat OR of conditions) is
    /// necessary.
    /// </summary>
    private List<BoundStatement> BuildCaseChain(BoundExpression subject, IReadOnlyList<BoundSwitchCase> cases, int index, List<BoundStatement> fallback, SourceSpan switchSpan)
    {
        if (index >= cases.Count)
        {
            return fallback;
        }

        var switchCase = cases[index];
        var rest = BuildCaseChain(subject, cases, index + 1, fallback, switchSpan);
        var loweredBody = LowerStatements(switchCase.Body);

        var compiledPatterns = switchCase.Patterns.Select(p => CompilePattern(p, subject)).ToList();
        var bindingPatternCount = compiledPatterns.Count(c => c.Bindings.Count > 0);

        if (bindingPatternCount > 0 && compiledPatterns.Count > 1)
        {
            // Swift itself requires identical bindings across every
            // comma-separated pattern in one case — Binder doesn't
            // check that yet (tracked as an open Semantics/ gap in its
            // own README), so rather than silently picking one
            // pattern's bindings arbitrarily, this is diagnosed and the
            // case is treated as unmatched (falls through to the rest
            // of the chain) as the safest recovery.
            diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                "a case with multiple comma-separated patterns cannot bind a value yet — use one pattern per case",
                switchCase.Span));
            return rest;
        }

        var test = compiledPatterns
            .Select(c => c.Test)
            .Aggregate((a, b) => new BoundBinary(a, BinaryOperator.LogicalOr, b, PrimitiveType.Bool, switchSpan));

        var bindings = compiledPatterns.Count == 1 ? compiledPatterns[0].Bindings : [];
        var matchedBody = bindings.Concat(loweredBody).ToList();

        if (switchCase.Guard is null)
        {
            return [new BoundIf(test, matchedBody, rest, switchSpan)];
        }

        // A pattern match whose guard fails falls through to the next
        // case, not to the switch's own default — so the guard check
        // has to be nested *inside* the pattern-matched branch, with
        // `rest` as its own else, rather than combined into `test` with
        // a plain AND (which would make a failing guard skip straight
        // to the final default, skipping any cases after this one).
        // This does mean `rest` is emitted twice (once as this BoundIf's
        // own else, once again inside the guard's else) when a guard is
        // present — a deliberate, documented trade-off: correct and
        // simple beats compact for a first pipeline, and collapsing the
        // duplication (e.g. via a shared local function per switch, once
        // CodeGen/ exists to emit one) is a reasonable future
        // optimization, not a correctness requirement now.
        var guardedBody = new List<BoundStatement> { new BoundIf(switchCase.Guard, matchedBody, rest, switchSpan) };
        return [new BoundIf(test, guardedBody, rest, switchSpan)];
    }

    /// <summary>
    /// Compiles a single bound pattern against <paramref name="scrutinee"/>
    /// into a boolean test expression, plus any variable declarations a
    /// binding inside the pattern introduces (to be prepended to the
    /// matched branch's body — see <see cref="BuildCaseChain"/>).
    /// Recurses for <see cref="BoundEnumCasePattern"/>'s associated-value
    /// slots, since each slot is itself a full pattern (e.g. the nested
    /// <c>.circle(let radius)</c> inside <c>.some(.circle(let radius))</c>).
    /// </summary>
    internal (BoundExpression Test, List<BoundStatement> Bindings) CompilePattern(BoundPattern pattern, BoundExpression scrutinee)
    {
        switch (pattern)
        {
            case BoundWildcardPattern p:
                return (new BoundBooleanLiteral(true, p.Span), []);

            case BoundBindingPattern p:
                return (new BoundBooleanLiteral(true, p.Span), [new BoundVariableDeclaration(p.Variable, scrutinee, p.Span)]);

            case BoundExpressionPattern p:
                return (new BoundBinary(scrutinee, BinaryOperator.Equal, p.Expression, PrimitiveType.Bool, p.Span), []);

            case BoundEnumCasePattern p:
                return CompileEnumCasePattern(p, scrutinee);

            default:
                throw new NotSupportedException($"unknown bound pattern kind '{pattern.GetType().Name}'");
        }
    }

    private (BoundExpression Test, List<BoundStatement> Bindings) CompileEnumCasePattern(BoundEnumCasePattern pattern, BoundExpression scrutinee)
    {
        BoundExpression test = new LoweredEnumTagCheck(scrutinee, pattern.Case, pattern.Span);
        var bindings = new List<BoundStatement>();

        for (var slot = 0; slot < pattern.AssociatedValues.Count; slot++)
        {
            var slotAccess = new LoweredAssociatedValueAccess(scrutinee, pattern.Case, slot, pattern.Span);
            var (slotTest, slotBindings) = CompilePattern(pattern.AssociatedValues[slot], slotAccess);
            bindings.AddRange(slotBindings);

            // A wildcard or plain binding slot always compiles to a
            // literal `true` test (see the two cases above) — ANDing
            // that in would still be correct, just a wasted `&& true`
            // for what's by far the most common case (a plain
            // `.circle(let radius)`), so it's skipped rather than
            // accumulated.
            if (slotTest is not BoundBooleanLiteral { Value: true })
            {
                test = new BoundBinary(test, BinaryOperator.LogicalAnd, slotTest, PrimitiveType.Bool, pattern.Span);
            }
        }

        return (test, bindings);
    }
}
