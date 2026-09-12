using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Parsing;

namespace Voyage.Compiler.Semantics;

/// <summary>
/// The second binding pass — see <see cref="DeclarationBinder"/>'s
/// remarks for why signatures resolve first. Given the global
/// <see cref="Scope"/> already populated with every function/struct/enum
/// signature, walks each function body (and any top-level statements)
/// binding every <c>Parsing.Statement</c>/<c>Parsing.Expression</c> into
/// its <see cref="BoundStatement"/>/<see cref="BoundExpression"/>
/// counterpart, resolving names against <see cref="Scope"/> and checking
/// types as it goes.
///
/// One <see cref="Binder"/> instance is created per function body (see
/// <see cref="SemanticAnalyzer"/>) rather than being reused across
/// functions, since <see cref="_currentReturnType"/> and
/// <see cref="_loopDepth"/> are both per-function-body state that
/// shouldn't leak between unrelated functions.
/// </summary>
public sealed class Binder
{
    private readonly Scope _globalScope;
    private readonly TypeResolver _typeResolver;
    private readonly IDiagnosticSink _diagnostics;
    private readonly TypeSymbol _currentReturnType;
    private int _loopDepth;

    public Binder(Scope globalScope, TypeResolver typeResolver, IDiagnosticSink diagnostics, TypeSymbol currentReturnType)
    {
        _globalScope = globalScope;
        _typeResolver = typeResolver;
        _diagnostics = diagnostics;
        _currentReturnType = currentReturnType;
    }

    /// <summary>Binds a function's body against a fresh child scope of
    /// the global scope, with each parameter pre-declared as a local
    /// variable — see <see cref="ParameterSymbol.AsVariable"/> for why a
    /// parameter reads inside the body as an ordinary
    /// <see cref="VariableSymbol"/> reference.</summary>
    public IReadOnlyList<BoundStatement> BindFunctionBody(FunctionSymbol function, IReadOnlyList<Statement> body)
    {
        var scope = _globalScope.CreateChild();
        foreach (var parameter in function.Parameters)
        {
            scope.TryDeclare(parameter.AsVariable());
        }

        return BindStatements(body, scope);
    }

    /// <summary>Binds a sequence of top-level statements (outside any
    /// function) directly against the global scope — used for a bare
    /// expression statement like the <c>hello.voy</c> sample's
    /// top-level <c>print(...)</c> call, which isn't inside any
    /// function body at all.</summary>
    public IReadOnlyList<BoundStatement> BindTopLevelStatements(IReadOnlyList<Statement> statements) =>
        BindStatements(statements, _globalScope);

    private List<BoundStatement> BindStatements(IReadOnlyList<Statement> statements, Scope scope) =>
        statements.Select(s => BindStatement(s, scope)).ToList();

    private BoundStatement BindStatement(Statement statement, Scope scope) => statement switch
    {
        ExpressionStatement s => new BoundExpressionStatement(BindExpression(s.Expression, scope), s.Span),
        BindingStatement s => BindBinding(s, scope),
        AssignmentStatement s => BindAssignment(s, scope),
        ReturnStatement s => BindReturn(s, scope),
        IfStatement s => BindIf(s, scope),
        WhileStatement s => BindWhile(s, scope),
        BreakStatement s => BindBreak(s),
        ContinueStatement s => BindContinue(s),
        SwitchStatement s => BindSwitch(s, scope),

        // Nested func/struct/enum/protocol/extension declarations, and
        // UnsupportedStatement (whatever Parsing/ itself couldn't parse
        // yet) — all outside ADR-0011's minimal subset. Top-level-only
        // func/struct/enum are handled by SemanticAnalyzer directly, not
        // here; encountering one of these *inside* a statement list
        // means it was nested, which isn't supported yet either.
        _ => UnsupportedStatement(statement),
    };

    private BoundStatement UnsupportedStatement(Statement statement)
    {
        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"statement of kind '{statement.GetType().Name}' is not yet supported by Semantics/",
            statement.Span));
        return new BoundErrorStatement(statement.Span);
    }

    private BoundStatement BindBinding(BindingStatement s, Scope scope)
    {
        var boundInitializer = s.Initializer is null ? null : BindExpression(s.Initializer, scope);

        // Parsing/ already guarantees at least one of DeclaredType/
        // Initializer is present (see BindingStatement's own remarks) —
        // this is a defensive fallback, not a reachable user-facing
        // diagnostic path.
        TypeSymbol type;
        if (s.DeclaredType is not null)
        {
            type = _typeResolver.Resolve(s.DeclaredType);
            if (boundInitializer is not null && !IsAssignable(boundInitializer.Type, type))
            {
                _diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"cannot assign a value of type '{boundInitializer.Type}' to a binding declared as '{type}'",
                    s.Initializer!.Span));
            }
        }
        else
        {
            type = boundInitializer!.Type;
        }

        if (type == PrimitiveType.Void)
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, "a binding cannot have type 'Void'", s.Span));
            type = ErrorType.Instance;
        }

        var variable = new VariableSymbol(s.Name, type, s.IsMutable);
        if (!scope.TryDeclare(variable))
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{s.Name}' is already declared in this scope", s.Span));
        }

        return new BoundVariableDeclaration(variable, boundInitializer, s.Span);
    }

    private BoundStatement BindAssignment(AssignmentStatement s, Scope scope)
    {
        var target = BindExpression(s.Target, scope);
        var value = BindExpression(s.Value, scope);

        // Whether Target is actually assignable (an lvalue — a variable
        // reference or a property access on one, not e.g. a call result)
        // is exactly the Semantics/ question Parsing.AssignmentStatement's
        // own remarks flag as deferred to this phase.
        var isMutableLValue = target switch
        {
            BoundVariableReference { Variable.IsMutable: true } => true,
            BoundPropertyAccess { Property.IsMutable: true } => true,
            BoundErrorExpression => true, // Already diagnosed once; don't cascade.
            _ => false,
        };

        if (!isMutableLValue)
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, "left-hand side of assignment is not a mutable variable or property", s.Target.Span));
        }
        else if (target.Type != ErrorType.Instance && value.Type != ErrorType.Instance && !IsAssignable(value.Type, target.Type))
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"cannot assign a value of type '{value.Type}' to a target of type '{target.Type}'",
                s.Span));
        }

        return new BoundAssignment(target, s.Operator, value, s.Span);
    }

    private BoundStatement BindReturn(ReturnStatement s, Scope scope)
    {
        var value = s.Value is null ? null : BindExpression(s.Value, scope);
        var valueType = value?.Type ?? PrimitiveType.Void;

        if (valueType != ErrorType.Instance && _currentReturnType != ErrorType.Instance && !IsAssignable(valueType, _currentReturnType))
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"cannot return a value of type '{valueType}' from a function declared to return '{_currentReturnType}'",
                s.Span));
        }

        return new BoundReturn(value, s.Span);
    }

    private BoundStatement BindIf(IfStatement s, Scope scope)
    {
        var condition = BindExpression(s.Condition, scope);
        RequireBool(condition, s.Condition.Span);

        var thenBody = BindStatements(s.ThenBranch, scope.CreateChild());
        var elseBody = s.ElseBranch is null ? null : BindStatements(s.ElseBranch, scope.CreateChild());

        return new BoundIf(condition, thenBody, elseBody, s.Span);
    }

    private BoundStatement BindWhile(WhileStatement s, Scope scope)
    {
        var condition = BindExpression(s.Condition, scope);
        RequireBool(condition, s.Condition.Span);

        _loopDepth++;
        var body = BindStatements(s.Body, scope.CreateChild());
        _loopDepth--;

        return new BoundWhile(condition, body, s.Span);
    }

    private BoundStatement BindBreak(BreakStatement s)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, "'break' outside of a loop", s.Span));
        }

        return new BoundBreak(s.Span);
    }

    private BoundStatement BindContinue(ContinueStatement s)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, "'continue' outside of a loop", s.Span));
        }

        return new BoundContinue(s.Span);
    }

    private BoundStatement BindSwitch(SwitchStatement s, Scope scope)
    {
        var subject = BindExpression(s.Subject, scope);

        var cases = s.Cases.Select(c =>
        {
            var caseScope = scope.CreateChild();
            var patterns = c.Patterns.Select(p => BindPattern(p, subject.Type, caseScope)).ToList();
            var guard_ = c.Guard is null ? null : BindExpression(c.Guard, caseScope);
            if (guard_ is not null) RequireBool(guard_, c.Guard!.Span);
            var body = BindStatements(c.Body, caseScope);
            return new BoundSwitchCase(patterns, guard_, body, c.Span);
        }).ToList();

        var defaultBody = s.DefaultBody is null ? null : BindStatements(s.DefaultBody, scope.CreateChild());

        // Real switch exhaustiveness checking (every EnumSymbol.Case
        // covered, or a default required) is flagged in
        // Parsing.SwitchStatement's own remarks as a Semantics/
        // question — not implemented yet in this minimal pass; every
        // switch is currently accepted regardless of coverage.

        return new BoundSwitch(subject, cases, defaultBody, s.Span);
    }

    private BoundPattern BindPattern(Pattern pattern, TypeSymbol subjectType, Scope scope) => pattern switch
    {
        WildcardPattern p => new BoundWildcardPattern(p.Span),
        BindingPattern p => BindBindingPattern(p, subjectType, scope),
        EnumCasePattern p => BindEnumCasePattern(p, subjectType, scope),
        ExpressionPattern p => new BoundExpressionPattern(BindExpression(p.Expression, scope), p.Span),
        _ => throw new NotSupportedException($"unknown pattern kind '{pattern.GetType().Name}'"),
    };

    private BoundPattern BindBindingPattern(BindingPattern p, TypeSymbol type, Scope scope)
    {
        var variable = new VariableSymbol(p.Name, type, IsMutable: false);
        if (!scope.TryDeclare(variable))
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{p.Name}' is already declared in this scope", p.Span));
        }

        return new BoundBindingPattern(variable, p.Span);
    }

    private BoundPattern BindEnumCasePattern(EnumCasePattern p, TypeSymbol subjectType, Scope scope)
    {
        if (subjectType is not EnumType enumType)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"pattern '.{p.CaseName}' can't match a value of type '{subjectType}', which isn't an enum",
                p.Span));
            return new BoundEnumCasePattern(new EnumCaseSymbol(p.CaseName, []), [], p.Span);
        }

        var caseSymbol = enumType.Symbol.FindCase(p.CaseName);
        if (caseSymbol is null)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"'{enumType.Symbol.Name}' has no case '{p.CaseName}'",
                p.Span));
            return new BoundEnumCasePattern(new EnumCaseSymbol(p.CaseName, []), [], p.Span);
        }

        if (p.AssociatedValues.Count != caseSymbol.AssociatedValues.Count)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"case '.{p.CaseName}' has {caseSymbol.AssociatedValues.Count} associated value(s), but the pattern provides {p.AssociatedValues.Count}",
                p.Span));
        }

        var boundValues = p.AssociatedValues
            .Zip(caseSymbol.AssociatedValues, (pattern, slot) => BindPattern(pattern, slot.Type, scope))
            .ToList();

        return new BoundEnumCasePattern(caseSymbol, boundValues, p.Span);
    }

    private void RequireBool(BoundExpression expression, SourceSpan span)
    {
        if (expression.Type != ErrorType.Instance && expression.Type != PrimitiveType.Bool)
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"expected 'Bool', found '{expression.Type}'", span));
        }
    }

    // --------------------------------------------------------------
    // Expressions
    // --------------------------------------------------------------

    private BoundExpression BindExpression(Expression expression, Scope scope) => expression switch
    {
        IntegerLiteralExpression e => new BoundIntegerLiteral(e.Value, e.Span),
        FloatLiteralExpression e => new BoundFloatLiteral(e.Value, e.Span),
        BooleanLiteralExpression e => new BoundBooleanLiteral(e.Value, e.Span),
        StringLiteralExpression e => new BoundStringLiteral(e.Value, e.Span),
        InterpolatedStringExpression e => BindInterpolatedString(e, scope),
        IdentifierExpression e => BindIdentifier(e, scope),
        ParenthesizedExpression e => BindExpression(e.Inner, scope),
        CallExpression e => BindCall(e, scope),
        MemberAccessExpression e => BindMemberAccess(e, scope),
        BinaryExpression e => BindBinary(e, scope),
        UnaryExpression e => BindUnary(e, scope),
        ErrorExpression e => new BoundErrorExpression(e.Span),

        // NilLiteralExpression/SelfExpression/SubscriptExpression: all
        // outside ADR-0011's minimal subset (no Optional<T> semantics,
        // no methods to have a `self` inside of, no subscript operator
        // semantics defined yet).
        _ => UnsupportedExpression(expression),
    };

    private BoundExpression UnsupportedExpression(Expression expression)
    {
        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"expression of kind '{expression.GetType().Name}' is not yet supported by Semantics/",
            expression.Span));
        return new BoundErrorExpression(expression.Span);
    }

    private BoundExpression BindInterpolatedString(InterpolatedStringExpression e, Scope scope)
    {
        var segments = e.Segments.Select(segment => segment switch
        {
            InterpolatedStringTextSegment t => (BoundExpression)new BoundStringLiteral(t.Text, t.Span),
            InterpolatedStringExpressionSegment x => BindInterpolationSegment(x, scope),
            _ => throw new NotSupportedException($"unknown interpolation segment kind '{segment.GetType().Name}'"),
        }).ToList();

        return new BoundInterpolatedString(segments, e.Span);
    }

    private BoundExpression BindInterpolationSegment(InterpolatedStringExpressionSegment x, Scope scope)
    {
        var bound = BindExpression(x.Expression, scope);
        if (bound.Type != ErrorType.Instance && bound.Type != PrimitiveType.String)
        {
            // Per ADR-0011's minimal subset there's no
            // CustomStringConvertible-style protocol yet to widen this —
            // only an already-String-typed expression can be embedded.
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"cannot interpolate a value of type '{bound.Type}' — only 'String' is supported so far",
                x.Span));
        }

        return bound;
    }

    private BoundExpression BindIdentifier(IdentifierExpression e, Scope scope)
    {
        if (!scope.TryLookup(e.Name, out var symbol))
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"cannot find '{e.Name}' in scope", e.Span));
            return new BoundErrorExpression(e.Span);
        }

        return symbol switch
        {
            VariableSymbol v => new BoundVariableReference(v, e.Span),

            // A bare struct/enum/function name used as a value (not
            // called) — e.g. `func` used as a first-class value — is
            // outside the minimal subset (no FunctionType values yet).
            _ => UnsupportedIdentifierUse(e, symbol),
        };
    }

    private BoundExpression UnsupportedIdentifierUse(IdentifierExpression e, Symbol symbol)
    {
        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"'{e.Name}' ({symbol.GetType().Name}) cannot be used as a value here — only calling it, or referencing a variable, is supported so far",
            e.Span));
        return new BoundErrorExpression(e.Span);
    }

    /// <summary>
    /// Binds a call expression, which per ADR-0011's minimal subset is
    /// always positional-only. Two things a <c>CallExpression</c> can
    /// mean, told apart by what its callee name resolves to: an ordinary
    /// function call (<see cref="FunctionSymbol"/>), or a struct
    /// construction (<see cref="StructSymbol"/>) — see
    /// <see cref="BoundStructConstruction"/>'s remarks. Anything else as
    /// a callee (calling a variable, a member-access result, etc.) is
    /// outside the minimal subset.
    /// </summary>
    private BoundExpression BindCall(CallExpression e, Scope scope)
    {
        var arguments = e.Arguments.Select(a => BindExpression(a, scope)).ToList();

        if (e.Callee is not IdentifierExpression callee)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                "only calling a plain named function or struct is supported so far — calling an arbitrary expression isn't yet",
                e.Callee.Span));
            return new BoundErrorExpression(e.Span);
        }

        if (!scope.TryLookup(callee.Name, out var symbol))
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"cannot find '{callee.Name}' in scope", callee.Span));
            return new BoundErrorExpression(e.Span);
        }

        return symbol switch
        {
            FunctionSymbol function => BindFunctionCall(function, arguments, e.Span),
            StructSymbol @struct => BindStructConstruction(@struct, arguments, e.Span),
            _ => UnsupportedCallee(callee, symbol, e.Span),
        };
    }

    private BoundExpression UnsupportedCallee(IdentifierExpression callee, Symbol symbol, SourceSpan span)
    {
        _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{callee.Name}' ({symbol.GetType().Name}) is not callable", span));
        return new BoundErrorExpression(span);
    }

    private BoundExpression BindFunctionCall(FunctionSymbol function, IReadOnlyList<BoundExpression> arguments, SourceSpan span)
    {
        CheckPositionalArity(function.Name, function.Parameters.Select(p => (TypeSymbol)p.Type).ToList(), arguments, span);
        return new BoundCall(function, arguments, span);
    }

    private BoundExpression BindStructConstruction(StructSymbol @struct, IReadOnlyList<BoundExpression> arguments, SourceSpan span)
    {
        CheckPositionalArity(@struct.Name, @struct.Properties.Select(p => (TypeSymbol)p.Type).ToList(), arguments, span);
        return new BoundStructConstruction(@struct, arguments, span);
    }

    /// <summary>Shared positional-argument checking for both an
    /// ordinary function call and a struct's implicit memberwise
    /// construction — both bind arguments to a parameter/property list
    /// by position and type, so the arity/type checks are identical.
    /// </summary>
    private void CheckPositionalArity(string calleeName, IReadOnlyList<TypeSymbol> expectedTypes, IReadOnlyList<BoundExpression> arguments, SourceSpan span)
    {
        if (arguments.Count != expectedTypes.Count)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"'{calleeName}' expects {expectedTypes.Count} argument(s), but {arguments.Count} were provided",
                span));
            return;
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Type != ErrorType.Instance && expectedTypes[i] != ErrorType.Instance && !IsAssignable(arguments[i].Type, expectedTypes[i]))
            {
                _diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"argument {i + 1} to '{calleeName}' expects '{expectedTypes[i]}', found '{arguments[i].Type}'",
                    arguments[i].Span));
            }
        }
    }

    private BoundExpression BindMemberAccess(MemberAccessExpression e, Scope scope)
    {
        var target = BindExpression(e.Target, scope);

        if (target.Type == ErrorType.Instance)
        {
            return new BoundErrorExpression(e.Span); // Already diagnosed once; don't cascade.
        }

        if (target.Type is not StructType structType)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"value of type '{target.Type}' has no member '{e.MemberName}' — only struct property access is supported so far",
                e.Span));
            return new BoundErrorExpression(e.Span);
        }

        var property = structType.Symbol.Properties.FirstOrDefault(p => p.Name == e.MemberName);
        if (property is null)
        {
            _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{structType.Symbol.Name}' has no property '{e.MemberName}'", e.Span));
            return new BoundErrorExpression(e.Span);
        }

        return new BoundPropertyAccess(target, property, e.Span);
    }

    private BoundExpression BindBinary(BinaryExpression e, Scope scope)
    {
        var left = BindExpression(e.Left, scope);
        var right = BindExpression(e.Right, scope);

        if (left.Type == ErrorType.Instance || right.Type == ErrorType.Instance)
        {
            return new BoundBinary(left, e.Operator, right, ErrorType.Instance, e.Span);
        }

        var resultType = e.Operator switch
        {
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo
                => CheckArithmetic(e.Operator, left, right, e.Span),

            BinaryOperator.Equal or BinaryOperator.NotEqual
                => CheckEquality(left, right, e.Span),

            BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual
                => CheckComparison(e.Operator, left, right, e.Span),

            BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr
                => CheckLogical(e.Operator, left, right, e.Span),

            // NilCoalescing needs Optional<T> semantics, out of scope
            // for the ADR-0011 minimal subset.
            _ => UnsupportedBinaryOperator(e.Operator, e.Span),
        };

        return new BoundBinary(left, e.Operator, right, resultType, e.Span);
    }

    private TypeSymbol UnsupportedBinaryOperator(BinaryOperator op, SourceSpan span)
    {
        _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"operator '{op}' is not yet supported by Semantics/", span));
        return ErrorType.Instance;
    }

    private TypeSymbol CheckArithmetic(BinaryOperator op, BoundExpression left, BoundExpression right, SourceSpan span)
    {
        if (IsNumeric(left.Type) && left.Type == right.Type)
        {
            return left.Type;
        }

        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"operator '{DescribeOperator(op)}' cannot be applied to operands of type '{left.Type}' and '{right.Type}'",
            span));
        return ErrorType.Instance;
    }

    private TypeSymbol CheckEquality(BoundExpression left, BoundExpression right, SourceSpan span)
    {
        // Per ADR-0011's minimal subset, equality is only checked between
        // identical primitive types — struct/enum Equatable conformance
        // is a protocol-system concern not covered yet.
        if (left.Type == right.Type && left.Type is PrimitiveType)
        {
            return PrimitiveType.Bool;
        }

        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"cannot compare operands of type '{left.Type}' and '{right.Type}' for equality",
            span));
        return ErrorType.Instance;
    }

    private TypeSymbol CheckComparison(BinaryOperator op, BoundExpression left, BoundExpression right, SourceSpan span)
    {
        if (IsNumeric(left.Type) && left.Type == right.Type)
        {
            return PrimitiveType.Bool;
        }

        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"operator '{DescribeOperator(op)}' cannot be applied to operands of type '{left.Type}' and '{right.Type}'",
            span));
        return ErrorType.Instance;
    }

    private TypeSymbol CheckLogical(BinaryOperator op, BoundExpression left, BoundExpression right, SourceSpan span)
    {
        if (left.Type == PrimitiveType.Bool && right.Type == PrimitiveType.Bool)
        {
            return PrimitiveType.Bool;
        }

        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"operator '{DescribeOperator(op)}' requires 'Bool' operands, found '{left.Type}' and '{right.Type}'",
            span));
        return ErrorType.Instance;
    }

    private BoundExpression BindUnary(UnaryExpression e, Scope scope)
    {
        var operand = BindExpression(e.Operand, scope);
        if (operand.Type == ErrorType.Instance)
        {
            return new BoundUnary(e.Operator, operand, ErrorType.Instance, e.Span);
        }

        var resultType = e.Operator switch
        {
            UnaryOperator.Negate when IsNumeric(operand.Type) => operand.Type,
            UnaryOperator.LogicalNot when operand.Type == PrimitiveType.Bool => PrimitiveType.Bool,
            _ => UnsupportedUnaryOperator(e.Operator, operand.Type, e.Span),
        };

        return new BoundUnary(e.Operator, operand, resultType, e.Span);
    }

    private TypeSymbol UnsupportedUnaryOperator(UnaryOperator op, TypeSymbol operandType, SourceSpan span)
    {
        _diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"operator '{DescribeOperator(op)}' cannot be applied to an operand of type '{operandType}'", span));
        return ErrorType.Instance;
    }

    private static bool IsNumeric(TypeSymbol type) => type == PrimitiveType.Int || type == PrimitiveType.Double;

    /// <summary>
    /// Whether a value of <paramref name="from"/> can be used where
    /// <paramref name="to"/> is expected. Per ADR-0011's minimal subset
    /// this is exact-match-only — no implicit widening (e.g. Int to
    /// Double) exists yet, matching Swift's own lack of implicit numeric
    /// conversion. A single named check (rather than every call site
    /// comparing types with <c>==</c> directly) so that widening rule,
    /// when it's added later, only needs to change here.
    /// </summary>
    private static bool IsAssignable(TypeSymbol from, TypeSymbol to) => from == to;

    private static string DescribeOperator(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+", BinaryOperator.Subtract => "-", BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/", BinaryOperator.Modulo => "%",
        BinaryOperator.Equal => "==", BinaryOperator.NotEqual => "!=",
        BinaryOperator.Less => "<", BinaryOperator.LessEqual => "<=",
        BinaryOperator.Greater => ">", BinaryOperator.GreaterEqual => ">=",
        BinaryOperator.LogicalAnd => "&&", BinaryOperator.LogicalOr => "||",
        BinaryOperator.NilCoalescing => "??",
        _ => op.ToString(),
    };

    private static string DescribeOperator(UnaryOperator op) => op switch
    {
        UnaryOperator.Negate => "-",
        UnaryOperator.LogicalNot => "!",
        _ => op.ToString(),
    };
}
