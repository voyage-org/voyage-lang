using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Lexing;

namespace Voyage.Compiler.Parsing;

/// <summary>
/// Hand-written recursive descent parser for voyage-lang, per ADR-0010
/// (no parser generator). Converts a token stream (from `Lexing/`) into
/// an AST.
///
/// SCOPE — this is intentionally the *first milestone only*, per
/// Parsing/README.md: enough grammar to parse `print("Hello, Voyage.")`
/// and nothing more. Concretely, this parser currently supports:
///   - Top-level expression statements
///   - Call expressions: `callee(arg, arg, ...)`
///   - Primary expressions: identifiers, string/integer/float/boolean/nil
///     literals, and parenthesized expressions `(expr)`
///
/// Explicitly NOT yet supported (each reported as a diagnostic + an
/// `UnsupportedStatement`/`ErrorExpression` recovery node, not a crash):
///   - Any declaration (`let`, `var`, `func`, `struct`, `enum`,
///     `protocol`, `extension`, `actor`, ...)
///   - Any control flow (`if`, `switch`, `for`, `while`, ...)
///   - String interpolation (`"\(...)"`)
///   - Binary/unary operators, member access (`.`), subscripting
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
        TokenKind.KwLet, TokenKind.KwVar,
        TokenKind.KwFunc, TokenKind.KwStruct, TokenKind.KwEnum,
        TokenKind.KwProtocol, TokenKind.KwExtension, TokenKind.KwActor,
        TokenKind.KwIf, TokenKind.KwGuard, TokenKind.KwSwitch,
        TokenKind.KwFor, TokenKind.KwWhile, TokenKind.KwRepeat,
        TokenKind.KwReturn, TokenKind.KwBreak, TokenKind.KwContinue,
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

    private Token PeekAt(int offset)
    {
        var i = _pos + offset;
        return i < _tokens.Count ? _tokens[i] : _tokens[^1]; // ^1 is EndOfFile
    }

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
        if (UnsupportedStatementStarts.Contains(Current.Kind))
        {
            return ParseUnsupportedStatement();
        }

        var start = Current.Span.Start;
        var expr = ParseExpression();
        var end = Current.Span.Start; // position right after the expression

        // A statement ends at a newline, EOF, or an optional semicolon
        // (grammar.md: semicolons are an optional same-line separator).
        if (!Check(TokenKind.Newline) && !IsAtEnd && !Check(TokenKind.Semicolon))
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Expected end of statement, found '{Current.Text}'.",
                SourceSpan.At(Current.Span.Start)));
        }
        Match(TokenKind.Semicolon);

        return new ExpressionStatement(expr, new SourceSpan(start, end));
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
    // Expressions
    // ----------------------------------------------------------------

    private Expression ParseExpression() => ParsePostfix(ParsePrimary());

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
