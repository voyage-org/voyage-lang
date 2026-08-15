using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Lexing;

namespace Voyage.Compiler.Parsing;

/// <summary>
/// Hand-written recursive descent parser for voyage-lang, per ADR-0010
/// (no parser generator). Converts a token stream (from `Lexing/`) into
/// an AST.
///
/// SCOPE — this parser's grammar coverage grows incrementally; see
/// src/Voyage.Compiler/Parsing/README.md for the authoritative current
/// scope and what's next. As of this revision, supported:
///   - Top-level expression statements
///   - `let`/`var` binding statements with an initializer (no explicit
///     `: Type` annotation yet — recognized and reported, not silently
///     dropped)
///   - Call expressions: `callee(arg, arg, ...)`
///   - Binary/unary operators per grammar.md's "Operator Precedence and
///     Associativity" table (||, &&, comparison, ??, +/-, */%, unary
///     -/!) — range operators (`..&lt;`, `...`) are recognized by the
///     lexer but not yet wired into this precedence ladder, since no
///     grammar construct (`for`-`in`) consumes them yet
///   - Primary expressions: identifiers, string/integer/float/boolean/nil
///     literals, and parenthesized expressions `(expr)`
///   - `func` declarations: `func NAME(NAME: Type, ...) [-&gt; Type] { ... }`
///     — bodies are just nested statement lists (so `return`, nested
///     `func`s, etc. all just work), but generic parameters (`&lt;T&gt;`/
///     `where`) and Swift-style external parameter labels are not yet
///     parsed (see FunctionDeclaration/Parameter/TypeNode remarks)
///   - `return` statements, with or without a value
///
/// Explicitly NOT yet supported (each reported as a diagnostic + an
/// `UnsupportedStatement`/`ErrorExpression` recovery node, not a crash):
///   - Any other declaration (`struct`, `enum`, `protocol`, `extension`,
///     `actor`, ...)
///   - Any other control flow (`if`, `switch`, `for`, `while`, ...)
///   - String interpolation (`"\(...)"`)
///   - Member access (`.`), subscripting, ternary, `as`-casting
/// Every one of these is a real next-milestone item, not an oversight —
/// growing this parser outward from here is the expected path.
/// </summary>
public sealed class Parser
{
    // Statement-leading keywords the grammar defines but this milestone's
    // parser doesn't yet handle. Listed explicitly (rather than inferred
    // from "not a valid expression start") so the diagnostic can name
    // the construct the parser recognized-but-can't-parse-yet, which is
    // a much more useful message than a generic parse error.
    private static readonly HashSet<TokenKind> UnsupportedStatementStarts =
    [
        TokenKind.KwStruct, TokenKind.KwEnum,
        TokenKind.KwProtocol, TokenKind.KwExtension, TokenKind.KwActor,
        TokenKind.KwIf, TokenKind.KwGuard, TokenKind.KwSwitch,
        TokenKind.KwFor, TokenKind.KwWhile, TokenKind.KwRepeat,
        TokenKind.KwBreak, TokenKind.KwContinue,
        TokenKind.KwThrow, TokenKind.KwDo, TokenKind.KwImport,
        TokenKind.KwDefer, TokenKind.KwUsing, TokenKind.KwAtomic,
    ];

    private readonly IReadOnlyList<Token> _tokens;
    private readonly IDiagnosticSink _diagnostics;
    private int _pos;

    private Parser(IReadOnlyList<Token> tokens, IDiagnosticSink diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public static CompilationUnit Parse(IReadOnlyList<Token> tokens, IDiagnosticSink diagnostics)
    {
        var parser = new Parser(tokens, diagnostics);
        return parser.ParseCompilationUnit();
    }

    // ----------------------------------------------------------------
    // Token cursor helpers
    // ----------------------------------------------------------------

    private Token Current => _tokens[_pos];

    private bool IsAtEnd => Current.Kind == TokenKind.EndOfFile;

    private Token Advance()
    {
        var t = Current;
        if (!IsAtEnd)
        {
            _pos++;
        }
        return t;
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;

    private bool Match(TokenKind kind)
    {
        if (!Check(kind))
        {
            return false;
        }
        Advance();
        return true;
    }

    private Token Expect(TokenKind kind, string message)
    {
        if (Check(kind))
        {
            return Advance();
        }

        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            message,
            SourceSpan.At(Current.Span.Start)));

        // Recovery: don't consume the unexpected token, so the caller's
        // own loop can decide what to do with it (e.g. treat it as the
        // start of the next statement) rather than this call silently
        // eating something unrelated.
        return Current;
    }

    private void SkipNewlines()
    {
        while (Check(TokenKind.Newline))
        {
            Advance();
        }
    }

    // ----------------------------------------------------------------
    // Compilation unit / statements
    // ----------------------------------------------------------------

    private CompilationUnit ParseCompilationUnit()
    {
        var start = Current.Span.Start;
        var statements = new List<Statement>();

        SkipNewlines();
        while (!IsAtEnd)
        {
            statements.Add(ParseStatement());
            SkipNewlines();
        }

        var end = Current.Span.End; // EndOfFile's span
        return new CompilationUnit(statements, new SourceSpan(start, end));
    }

    private Statement ParseStatement()
    {
        if (Check(TokenKind.KwLet) || Check(TokenKind.KwVar))
        {
            return ParseBindingStatement();
        }

        if (Check(TokenKind.KwFunc))
        {
            return ParseFunctionDeclaration();
        }

        if (Check(TokenKind.KwReturn))
        {
            return ParseReturnStatement();
        }

        if (UnsupportedStatementStarts.Contains(Current.Kind))
        {
            return ParseUnsupportedStatement();
        }

        var start = Current.Span.Start;
        var expr = ParseExpression();
        var end = Current.Span.Start; // position right after the expression
        ExpectStatementTerminator();
        return new ExpressionStatement(expr, new SourceSpan(start, end));
    }

    /// <summary>
    /// Parses `let`/`var` NAME [: Type] = expr. The `: Type` annotation
    /// form is recognized (so it doesn't get misread as something else)
    /// but not yet implemented — see grammar.md's "Parser implementation
    /// note" under Section 2. A binding with no initializer at all is
    /// treated as an unsupported statement, same recovery contract as
    /// every other not-yet-supported construct.
    /// </summary>
    private Statement ParseBindingStatement()
    {
        var start = Current.Span.Start;
        var isMutable = Current.Kind == TokenKind.KwVar;
        Advance(); // consume 'let' / 'var'

        var nameToken = Expect(TokenKind.Identifier, "Expected a name after 'let'/'var'.");
        var name = nameToken.Text;

        if (Check(TokenKind.Colon))
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Warning,
                "Explicit type annotations on let/var bindings are not yet supported " +
                "by this parser milestone (see grammar.md Section 2 and " +
                "src/Voyage.Compiler/Parsing/README.md). The annotation is being ignored; " +
                "the binding's type will still need to be inferred from its initializer.",
                SourceSpan.At(Current.Span.Start)));

            Advance(); // consume ':'
            while (!Check(TokenKind.Equal) && !Check(TokenKind.Newline) && !IsAtEnd)
            {
                Advance();
            }
        }

        if (!Check(TokenKind.Equal))
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                "Expected '=' — bindings without an initializer are not yet supported " +
                "by this parser milestone.",
                SourceSpan.At(Current.Span.Start)));

            while (!Check(TokenKind.Newline) && !IsAtEnd)
            {
                Advance();
            }
            return new UnsupportedStatement(new SourceSpan(start, Current.Span.Start));
        }

        Advance(); // consume '='
        var initializer = ParseExpression();
        var end = Current.Span.Start;
        ExpectStatementTerminator();
        return new BindingStatement(isMutable, name, initializer, new SourceSpan(start, end));
    }

    /// <summary>
    /// Parses `func` NAME `(` [PARAM (`,` PARAM)*] `)` [`->` Type] `{` STATEMENT* `}`.
    /// Generic parameters (`&lt;T&gt;`/`where`) are not yet recognized — see
    /// FunctionDeclaration's remarks and Parsing/README.md. A missing
    /// return type is fine (implicit Void); a single-expression body is
    /// just parsed as one ExpressionStatement, with implicit-return
    /// lowering left to a later phase per ADR-0005.
    /// </summary>
    private Statement ParseFunctionDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'func'

        var nameToken = Expect(TokenKind.Identifier, "Expected a function name after 'func'.");
        var name = nameToken.Text;

        Expect(TokenKind.LParen, "Expected '(' to begin the parameter list.");
        var parameters = new List<Parameter>();
        if (!Check(TokenKind.RParen))
        {
            parameters.Add(ParseParameter());
            while (Match(TokenKind.Comma))
            {
                parameters.Add(ParseParameter());
            }
        }
        Expect(TokenKind.RParen, "Expected ')' to close the parameter list.");

        TypeNode? returnType = null;
        if (Match(TokenKind.Arrow))
        {
            returnType = ParseType();
        }

        Expect(TokenKind.LBrace, "Expected '{' to begin the function body.");
        var body = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to close the function body.");

        return new FunctionDeclaration(name, parameters, returnType, body, new SourceSpan(start, closeBrace.Span.End));
    }

    /// <summary>
    /// Parses a single "NAME: Type" parameter. Swift-style external
    /// labels (a separate external name, or `_` to suppress it) are not
    /// yet recognized — see Parameter's remarks.
    /// </summary>
    private Parameter ParseParameter()
    {
        var nameToken = Expect(TokenKind.Identifier, "Expected a parameter name.");
        Expect(TokenKind.Colon, "Expected ':' after parameter name.");
        var type = ParseType();
        return new Parameter(nameToken.Text, type, new SourceSpan(nameToken.Span.Start, type.Span.End));
    }

    /// <summary>
    /// Parses a minimal type reference: a bare identifier with an
    /// optional trailing `?`. See TypeNode's remarks for what's
    /// deliberately not yet handled here.
    /// </summary>
    private TypeNode ParseType()
    {
        var nameToken = Expect(TokenKind.Identifier, "Expected a type name.");
        if (Check(TokenKind.Question))
        {
            var question = Advance();
            return new TypeNode(nameToken.Text, true, new SourceSpan(nameToken.Span.Start, question.Span.End));
        }
        return new TypeNode(nameToken.Text, false, nameToken.Span);
    }

    /// <summary>
    /// Parses the statements inside a `{ ... }` block, stopping at the
    /// closing brace (left for the caller to Expect, so the caller's
    /// span calculation can include it). Shared by function bodies now;
    /// intended to be shared by if/while/for/etc. bodies later.
    /// </summary>
    private List<Statement> ParseBlockStatements()
    {
        var statements = new List<Statement>();
        SkipNewlines();
        while (!Check(TokenKind.RBrace) && !IsAtEnd)
        {
            statements.Add(ParseStatement());
            SkipNewlines();
        }
        return statements;
    }

    /// <summary>
    /// Parses `return` [expr]. A bare `return` (no value) is recognized
    /// when the next token can't start an expression on the same
    /// statement — i.e. it's immediately a newline, semicolon, closing
    /// brace, or EOF.
    /// </summary>
    private Statement ParseReturnStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'return'

        Expression? value = null;
        if (!Check(TokenKind.Newline) && !Check(TokenKind.Semicolon) &&
            !Check(TokenKind.RBrace) && !IsAtEnd)
        {
            value = ParseExpression();
        }

        var end = Current.Span.Start;
        ExpectStatementTerminator();
        return new ReturnStatement(value, new SourceSpan(start, end));
    }

    /// <summary>
    /// A statement ends at a newline, EOF, an optional semicolon
    /// (grammar.md: semicolons are an optional same-line separator), or
    /// a closing brace (so the last statement in a `{ ... }` block —
    /// e.g. a compact single-expression function body — doesn't need a
    /// trailing newline before `}`). Shared by every statement kind so
    /// the terminator contract stays uniform as more statement kinds
    /// are added.
    /// </summary>
    private void ExpectStatementTerminator()
    {
        if (!Check(TokenKind.Newline) && !IsAtEnd &&
            !Check(TokenKind.Semicolon) && !Check(TokenKind.RBrace))
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Expected end of statement, found '{Current.Text}'.",
                SourceSpan.At(Current.Span.Start)));
        }
        Match(TokenKind.Semicolon);
    }

    /// <summary>
    /// Reports a diagnostic for a recognized-but-not-yet-parseable
    /// statement, then skips tokens up to the next newline/EOF so the
    /// parser can keep going with whatever follows, rather than
    /// aborting the whole file over one not-yet-supported construct.
    /// </summary>
    private UnsupportedStatement ParseUnsupportedStatement()
    {
        var start = Current.Span.Start;
        var keyword = Current.Text;

        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Warning,
            $"'{keyword}' statements are not yet supported by this parser milestone " +
            "(see src/Voyage.Compiler/Parsing/README.md). Skipping to the next line.",
            SourceSpan.At(Current.Span.Start)));

        while (!Check(TokenKind.Newline) && !IsAtEnd)
        {
            Advance();
        }

        var end = Current.Span.Start;
        return new UnsupportedStatement(new SourceSpan(start, end));
    }

    // ----------------------------------------------------------------
    // Expressions — precedence ladder
    // ----------------------------------------------------------------
    // Mirrors grammar.md's "Operator Precedence and Associativity"
    // table, itself adapted from Swift's real precedencegroup chain
    // (stdlib/public/core/Policy.swift). Each level parses everything
    // at higher precedence first, then loops/recurses for its own
    // operator(s) — the standard hand-written-recursive-descent pattern
    // for expression precedence (ADR-0010: no parser generator, no
    // separate precedence-table-driven Pratt parser either — this ladder
    // is the straightforward hand-written equivalent).

    private Expression ParseExpression() => ParseNilCoalescing();

    /// <summary>`??` — right-associative, per grammar.md.</summary>
    private Expression ParseNilCoalescing()
    {
        var left = ParseLogicalOr();
        if (Match(TokenKind.QuestionQuestion))
        {
            var right = ParseNilCoalescing(); // recurse (not loop) for right-associativity
            return new BinaryExpression(left, BinaryOperator.NilCoalescing, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Match(TokenKind.PipePipe))
        {
            var right = ParseLogicalAnd();
            left = new BinaryExpression(left, BinaryOperator.LogicalOr, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseLogicalAnd()
    {
        var left = ParseComparison();
        while (Match(TokenKind.AmpAmp))
        {
            var right = ParseComparison();
            left = new BinaryExpression(left, BinaryOperator.LogicalAnd, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    /// <summary>
    /// Comparison operators are non-chaining in voyage-lang, matching
    /// Swift (`a &lt; b &lt; c` is not valid) — parsed with `if`, not
    /// `while`, so at most one comparison operator applies per level.
    /// </summary>
    private Expression ParseComparison()
    {
        var left = ParseAdditive();
        if (TryGetComparisonOperator(Current.Kind, out var op))
        {
            Advance();
            var right = ParseAdditive();
            return new BinaryExpression(left, op, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Current.Kind == TokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryExpression(left, op, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Check(TokenKind.Star) || Check(TokenKind.Slash) || Check(TokenKind.Percent))
        {
            var op = Current.Kind switch
            {
                TokenKind.Star => BinaryOperator.Multiply,
                TokenKind.Slash => BinaryOperator.Divide,
                _ => BinaryOperator.Modulo,
            };
            Advance();
            var right = ParseUnary();
            left = new BinaryExpression(left, op, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    /// <summary>
    /// Unary `-` (negation) and `!` (logical not), binding tighter than
    /// every binary operator above. Postfix forms (calls) bind tighter
    /// still — see <see cref="ParsePostfix"/>.
    /// </summary>
    private Expression ParseUnary()
    {
        if (Check(TokenKind.Minus) || Check(TokenKind.Bang))
        {
            var start = Current.Span.Start;
            var op = Current.Kind == TokenKind.Minus ? UnaryOperator.Negate : UnaryOperator.LogicalNot;
            Advance();
            var operand = ParseUnary(); // allows stacking, e.g. `!!flag`, `--x` (as double negation)
            return new UnaryExpression(op, operand, new SourceSpan(start, operand.Span.End));
        }
        return ParsePostfix(ParsePrimary());
    }

    private static bool TryGetComparisonOperator(TokenKind kind, out BinaryOperator op)
    {
        switch (kind)
        {
            case TokenKind.EqualEqual: op = BinaryOperator.Equal; return true;
            case TokenKind.BangEqual: op = BinaryOperator.NotEqual; return true;
            case TokenKind.Less: op = BinaryOperator.Less; return true;
            case TokenKind.LessEqual: op = BinaryOperator.LessEqual; return true;
            case TokenKind.Greater: op = BinaryOperator.Greater; return true;
            case TokenKind.GreaterEqual: op = BinaryOperator.GreaterEqual; return true;
            default: op = default; return false;
        }
    }

    /// <summary>
    /// Handles postfix constructs applied to a primary expression. Only
    /// call expressions are implemented this milestone; member access
    /// (`.`) and subscripting (`[...]`) are explicit next-milestone
    /// items, not silently ignored — they simply aren't reached here
    /// yet, and a `.`/`[` after a primary expression will surface as an
    /// "expected end of statement" diagnostic from the caller instead of
    /// being parsed, which is an accurate (if not yet friendly) signal
    /// that the construct isn't supported yet.
    /// </summary>
    private Expression ParsePostfix(Expression expr)
    {
        while (Check(TokenKind.LParen))
        {
            expr = ParseCallExpression(expr);
        }
        return expr;
    }

    private CallExpression ParseCallExpression(Expression callee)
    {
        var start = callee.Span.Start;
        Advance(); // consume '('

        var arguments = new List<Expression>();
        if (!Check(TokenKind.RParen))
        {
            arguments.Add(ParseExpression());
            while (Match(TokenKind.Comma))
            {
                arguments.Add(ParseExpression());
            }
        }

        var closeParen = Expect(TokenKind.RParen, "Expected ')' to close the argument list.");
        return new CallExpression(callee, arguments, new SourceSpan(start, closeParen.Span.End));
    }

    private Expression ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.Identifier:
                Advance();
                return new IdentifierExpression(token.Text, token.Span);

            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpression((string)token.LiteralValue!, token.Span);

            case TokenKind.IntegerLiteral:
                Advance();
                return new IntegerLiteralExpression((long)token.LiteralValue!, token.Span);

            case TokenKind.FloatLiteral:
                Advance();
                return new FloatLiteralExpression((double)token.LiteralValue!, token.Span);

            case TokenKind.BooleanLiteral:
                Advance();
                return new BooleanLiteralExpression((bool)token.LiteralValue!, token.Span);

            case TokenKind.NilLiteral:
                Advance();
                return new NilLiteralExpression(token.Span);

            case TokenKind.LParen:
                return ParseParenthesizedExpression();

            case TokenKind.InterpolationStringStart:
                return ParseUnsupportedInterpolatedString();

            default:
                _diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"Expected an expression, found '{token.Text}'.",
                    SourceSpan.At(token.Span.Start)));
                // Advance past the offending token so callers don't spin
                // forever re-parsing the same non-expression token.
                if (!IsAtEnd)
                {
                    Advance();
                }
                return new ErrorExpression(token.Span);
        }
    }

    private Expression ParseParenthesizedExpression()
    {
        var start = Current.Span.Start;
        Advance(); // consume '('
        var inner = ParseExpression();
        var closeParen = Expect(TokenKind.RParen, "Expected ')' to close the parenthesized expression.");
        return new ParenthesizedExpression(inner, new SourceSpan(start, closeParen.Span.End));
    }

    /// <summary>
    /// String interpolation parsing is explicitly out of scope for this
    /// milestone (grammar.md Section 10 defines it; this parser doesn't
    /// implement it yet). Rather than mis-parsing the interpolation's
    /// structural tokens as something else, this reports a clear
    /// diagnostic and skips to the matching InterpolationStringEnd.
    /// </summary>
    private Expression ParseUnsupportedInterpolatedString()
    {
        var start = Current.Span.Start;
        _diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            "String interpolation is not yet supported by this parser milestone " +
            "(see src/Voyage.Compiler/Parsing/README.md).",
            SourceSpan.At(start)));

        Advance(); // consume InterpolationStringStart
        var depth = 1;
        while (depth > 0 && !IsAtEnd)
        {
            switch (Current.Kind)
            {
                case TokenKind.InterpolationStringStart:
                    depth++;
                    break;
                case TokenKind.InterpolationStringEnd:
                    depth--;
                    break;
            }
            Advance();
        }

        return new ErrorExpression(new SourceSpan(start, Current.Span.Start));
    }
}
