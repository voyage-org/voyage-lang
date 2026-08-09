using System.Globalization;
using System.Text;
using Voyage.Compiler.Diagnostics;

namespace Voyage.Compiler.Lexing;

/// <summary>
/// Hand-written, single-pass lexer for voyage-lang, per ADR-0010 (no
/// parser/lexer generator). Converts source text directly into a token
/// stream; performs no parsing or semantic analysis of its own.
///
/// Covers every token kind listed in docs/spec/grammar.md's Appendix,
/// including `\(...)` string interpolation (grammar.md Section 10) via a
/// small internal state stack — this is the one part of lexing that
/// isn't a flat character scan, since an interpolation's contents are
/// themselves ordinary voyage-lang expression tokens that can nest
/// arbitrarily (including further string literals with their own
/// interpolations).
///
/// Known, deliberate scope limits for this first pass (tracked, not
/// oversights):
///   - No Unicode-extended-identifier support yet (ASCII identifiers
///     only) — grammar.md Section 1 calls for Unicode-aware identifiers
///     eventually; deferred until real Unicode test cases exist.
///   - Numeric literal underscores as digit separators (e.g. `1_000`,
///     a Swift convenience) are not yet supported.
///   - No raw/multi-line string literal forms — single-line, double-
///     quoted strings only.
/// </summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["func"] = TokenKind.KwFunc,
        ["struct"] = TokenKind.KwStruct,
        ["enum"] = TokenKind.KwEnum,
        ["protocol"] = TokenKind.KwProtocol,
        ["extension"] = TokenKind.KwExtension,
        ["actor"] = TokenKind.KwActor,
        ["case"] = TokenKind.KwCase,
        ["associatedtype"] = TokenKind.KwAssociatedtype,
        ["let"] = TokenKind.KwLet,
        ["var"] = TokenKind.KwVar,
        ["Self"] = TokenKind.KwSelfType,
        ["any"] = TokenKind.KwAny,
        ["some"] = TokenKind.KwSome,
        ["Optional"] = TokenKind.KwOptional,
        ["if"] = TokenKind.KwIf,
        ["else"] = TokenKind.KwElse,
        ["guard"] = TokenKind.KwGuard,
        ["switch"] = TokenKind.KwSwitch,
        ["default"] = TokenKind.KwDefault,
        ["for"] = TokenKind.KwFor,
        ["in"] = TokenKind.KwIn,
        ["while"] = TokenKind.KwWhile,
        ["repeat"] = TokenKind.KwRepeat,
        ["break"] = TokenKind.KwBreak,
        ["continue"] = TokenKind.KwContinue,
        ["return"] = TokenKind.KwReturn,
        ["fallthrough"] = TokenKind.KwFallthrough,
        ["throw"] = TokenKind.KwThrow,
        ["throws"] = TokenKind.KwThrows,
        ["try"] = TokenKind.KwTry,
        ["catch"] = TokenKind.KwCatch,
        ["do"] = TokenKind.KwDo,
        ["async"] = TokenKind.KwAsync,
        ["await"] = TokenKind.KwAwait,
        ["task"] = TokenKind.KwTask,
        ["spawn"] = TokenKind.KwSpawn,
        ["join"] = TokenKind.KwJoin,
        ["atomic"] = TokenKind.KwAtomic,
        ["public"] = TokenKind.KwPublic,
        ["internal"] = TokenKind.KwInternal,
        ["private"] = TokenKind.KwPrivate,
        ["fileprivate"] = TokenKind.KwFileprivate,
        ["static"] = TokenKind.KwStatic,
        ["mutating"] = TokenKind.KwMutating,
        ["final"] = TokenKind.KwFinal,
        ["override"] = TokenKind.KwOverride,
        ["weak"] = TokenKind.KwWeak,
        ["defer"] = TokenKind.KwDefer,
        ["using"] = TokenKind.KwUsing,
        ["true"] = TokenKind.KwTrue,
        ["false"] = TokenKind.KwFalse,
        ["nil"] = TokenKind.KwNil,
        ["import"] = TokenKind.KwImport,
        ["where"] = TokenKind.KwWhere,
        ["as"] = TokenKind.KwAs,
        ["is"] = TokenKind.KwIs,
        ["self"] = TokenKind.KwSelfValue,
        ["super"] = TokenKind.KwSuper,
    };

    private readonly string _source;
    private readonly IDiagnosticSink _diagnostics;
    private readonly List<Token> _tokens = [];

    private int _pos;
    private int _line = 1;
    private int _column = 1;

    // Tracks nesting for `\( ... )` interpolation resumption: each entry
    // is the paren-depth at which the interpolation's closing `)` will be
    // found, relative to LParen/RParen tokens the normal scanner also
    // emits inside the interpolation expression itself (e.g.
    // `\(f(x))` has a real call inside the interpolation).
    private readonly Stack<int> _interpolationParenDepth = new();
    private int _openParenDepth;

    private Lexer(string source, IDiagnosticSink diagnostics)
    {
        _source = source;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Tokenizes <paramref name="source"/> in full and returns the token
    /// stream, terminated by a single EndOfFile token. Diagnostics for
    /// malformed input (unterminated strings, unterminated block
    /// comments, invalid characters) are reported to <paramref
    /// name="diagnostics"/> rather than thrown, per ADR-0010 — lexing
    /// continues past an error where it safely can, so a single typo
    /// doesn't hide every other issue in the file.
    /// </summary>
    public static IReadOnlyList<Token> Tokenize(string source, IDiagnosticSink diagnostics)
    {
        var lexer = new Lexer(source, diagnostics);
        lexer.Run();
        return lexer._tokens;
    }

    private void Run()
    {
        while (true)
        {
            var token = NextToken();
            _tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
            {
                break;
            }
        }
    }

    // ----------------------------------------------------------------
    // Character cursor helpers
    // ----------------------------------------------------------------

    private bool IsAtEnd => _pos >= _source.Length;

    private char Current => IsAtEnd ? '\0' : _source[_pos];

    private char Peek(int offset = 1) =>
        _pos + offset < _source.Length ? _source[_pos + offset] : '\0';

    private SourceLocation Here => new(_line, _column);

    private char Advance()
    {
        var c = _source[_pos];
        _pos++;
        if (c == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }
        return c;
    }

    private bool Match(char expected)
    {
        if (IsAtEnd || Current != expected)
        {
            return false;
        }
        Advance();
        return true;
    }

    // ----------------------------------------------------------------
    // Main dispatch
    // ----------------------------------------------------------------

    private Token NextToken()
    {
        SkipWhitespaceAndComments(out var sawNewline);
        if (sawNewline)
        {
            // Newline itself is the token; do not also consume the next
            // token's leading character here.
            var start = Here;
            return new Token(TokenKind.Newline, "\n", SourceSpan.At(start));
        }

        if (IsAtEnd)
        {
            return new Token(TokenKind.EndOfFile, "", SourceSpan.At(Here));
        }

        var startLoc = Here;
        var c = Advance();

        if (c == '"')
        {
            return LexStringLiteral(startLoc, isInterpolationResume: false);
        }

        if (char.IsDigit(c))
        {
            return LexNumber(startLoc, c);
        }

        if (IsIdentifierStart(c))
        {
            return LexIdentifierOrKeyword(startLoc, c);
        }

        if (c == '@')
        {
            return LexAttribute(startLoc);
        }

        return LexOperatorOrPunctuation(startLoc, c);
    }

    // ----------------------------------------------------------------
    // Whitespace / comments
    // ----------------------------------------------------------------

    /// <summary>
    /// Consumes spaces, tabs, and comments. Stops (without consuming)
    /// at the first newline it finds, reporting that a newline was seen
    /// so the caller can emit a single Newline token — collapsing
    /// consecutive blank lines into one Newline token is intentional;
    /// Parsing/ only needs to know "a statement boundary may be here",
    /// not how many blank lines separated two statements.
    /// </summary>
    private void SkipWhitespaceAndComments(out bool sawNewline)
    {
        sawNewline = false;
        while (!IsAtEnd)
        {
            var c = Current;
            if (c == '\n')
            {
                Advance();
                sawNewline = true;
                // Keep consuming further blank lines/whitespace so a run
                // of blank lines collapses into a single Newline token.
                while (!IsAtEnd && (Current == '\n' || Current == ' ' || Current == '\t' || Current == '\r'))
                {
                    Advance();
                }
                return;
            }

            if (c is ' ' or '\t' or '\r')
            {
                Advance();
                continue;
            }

            if (c == '/' && Peek() == '/')
            {
                while (!IsAtEnd && Current != '\n')
                {
                    Advance();
                }
                continue;
            }

            if (c == '/' && Peek() == '*')
            {
                SkipBlockComment();
                continue;
            }

            break;
        }
    }

    private void SkipBlockComment()
    {
        var start = Here;
        Advance(); // '/'
        Advance(); // '*'
        var depth = 1;
        while (!IsAtEnd && depth > 0)
        {
            if (Current == '/' && Peek() == '*')
            {
                Advance();
                Advance();
                depth++;
            }
            else if (Current == '*' && Peek() == '/')
            {
                Advance();
                Advance();
                depth--;
            }
            else
            {
                Advance();
            }
        }

        if (depth > 0)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                "Unterminated block comment.",
                new SourceSpan(start, Here)));
        }
    }

    // ----------------------------------------------------------------
    // Identifiers / keywords
    // ----------------------------------------------------------------

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierContinue(char c) => char.IsLetterOrDigit(c) || c == '_';

    private Token LexIdentifierOrKeyword(SourceLocation start, char first)
    {
        var sb = new StringBuilder();
        sb.Append(first);
        while (!IsAtEnd && IsIdentifierContinue(Current))
        {
            sb.Append(Advance());
        }

        var text = sb.ToString();
        var span = new SourceSpan(start, Here);

        if (Keywords.TryGetValue(text, out var kind))
        {
            return kind switch
            {
                TokenKind.KwTrue => new Token(TokenKind.BooleanLiteral, text, span, true),
                TokenKind.KwFalse => new Token(TokenKind.BooleanLiteral, text, span, false),
                TokenKind.KwNil => new Token(TokenKind.NilLiteral, text, span),
                _ => new Token(kind, text, span),
            };
        }

        return new Token(TokenKind.Identifier, text, span);
    }

    // ----------------------------------------------------------------
    // Numeric literals
    // ----------------------------------------------------------------

    private Token LexNumber(SourceLocation start, char first)
    {
        var sb = new StringBuilder();
        sb.Append(first);

        // Hex / binary / octal prefixed integer literals: 0x2A, 0b101010, 0o52
        if (first == '0' && (Current is 'x' or 'X' or 'b' or 'B' or 'o' or 'O'))
        {
            var prefixChar = char.ToLowerInvariant(Current);
            sb.Append(Advance());
            var digitPredicate = prefixChar switch
            {
                'x' => (Func<char, bool>)Uri.IsHexDigit,
                'b' => c => c is '0' or '1',
                'o' => c => c is >= '0' and <= '7',
                _ => throw new InvalidOperationException("unreachable"),
            };

            while (!IsAtEnd && digitPredicate(Current))
            {
                sb.Append(Advance());
            }

            var text = sb.ToString();
            var span = new SourceSpan(start, Here);
            var value = ParsePrefixedInteger(text, prefixChar);
            return new Token(TokenKind.IntegerLiteral, text, span, value);
        }

        while (!IsAtEnd && char.IsDigit(Current))
        {
            sb.Append(Advance());
        }

        var isFloat = false;

        if (Current == '.' && char.IsDigit(Peek()))
        {
            isFloat = true;
            sb.Append(Advance()); // '.'
            while (!IsAtEnd && char.IsDigit(Current))
            {
                sb.Append(Advance());
            }
        }

        if (Current is 'e' or 'E')
        {
            var lookaheadOffset = 1;
            var sign = Peek(lookaheadOffset);
            if (sign is '+' or '-')
            {
                lookaheadOffset++;
            }
            if (char.IsDigit(Peek(lookaheadOffset)))
            {
                isFloat = true;
                sb.Append(Advance()); // 'e'/'E'
                if (Current is '+' or '-')
                {
                    sb.Append(Advance());
                }
                while (!IsAtEnd && char.IsDigit(Current))
                {
                    sb.Append(Advance());
                }
            }
        }

        var finalText = sb.ToString();
        var finalSpan = new SourceSpan(start, Here);

        if (isFloat)
        {
            var value = double.Parse(finalText, CultureInfo.InvariantCulture);
            return new Token(TokenKind.FloatLiteral, finalText, finalSpan, value);
        }
        else
        {
            var value = long.Parse(finalText, CultureInfo.InvariantCulture);
            return new Token(TokenKind.IntegerLiteral, finalText, finalSpan, value);
        }
    }

    private long ParsePrefixedInteger(string text, char prefixChar)
    {
        // text includes the leading "0x"/"0b"/"0o"; strip it for parsing.
        var digits = text[2..];
        var fromBase = prefixChar switch
        {
            'x' => 16,
            'b' => 2,
            'o' => 8,
            _ => throw new InvalidOperationException("unreachable"),
        };
        return digits.Length == 0 ? 0 : Convert.ToInt64(digits, fromBase);
    }

    // ----------------------------------------------------------------
    // String literals, including `\(...)` interpolation
    // ----------------------------------------------------------------

    /// <summary>
    /// Lexes a string literal body starting just after the opening `"`
    /// (or, on resumption after an interpolation's closing `)`, from
    /// wherever the string content continues). Handles escape sequences
    /// and detects `\(` interpolation starts, at which point it returns
    /// an InterpolationStringStart/Middle token and pushes interpolation
    /// state so the *next* calls to NextToken() lex the interpolated
    /// expression as ordinary tokens, until the matching `)` — tracked
    /// via <see cref="_interpolationParenDepth"/> against the normal
    /// paren-depth counter so a call expression inside the interpolation
    /// (e.g. `\(f(x))`) doesn't close the interpolation early.
    /// </summary>
    private Token LexStringLiteral(SourceLocation start, bool isInterpolationResume)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (IsAtEnd)
            {
                _diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "Unterminated string literal.",
                    new SourceSpan(start, Here)));
                break;
            }

            var c = Current;

            if (c == '"')
            {
                Advance();
                var span = new SourceSpan(start, Here);
                var kind = isInterpolationResume ? TokenKind.InterpolationStringEnd : TokenKind.StringLiteral;
                return new Token(kind, sb.ToString(), span, sb.ToString());
            }

            if (c == '\\' && Peek() == '(')
            {
                Advance(); // '\'
                Advance(); // '('
                _interpolationParenDepth.Push(_openParenDepth);
                _openParenDepth++; // the interpolation's own implicit "(" level
                var span = new SourceSpan(start, Here);
                var kind = isInterpolationResume ? TokenKind.InterpolationStringMiddle : TokenKind.InterpolationStringStart;
                return new Token(kind, sb.ToString(), span, sb.ToString());
            }

            if (c == '\\')
            {
                Advance();
                var escaped = IsAtEnd ? '\0' : Advance();
                sb.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    '0' => '\0',
                    _ => escaped, // unrecognized escape: pass the char through
                });
                continue;
            }

            if (c == '\n')
            {
                // voyage-lang strings are single-line (see Lexer summary);
                // an embedded literal newline means the string was never
                // closed on its own line.
                _diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    "Unterminated string literal (reached end of line).",
                    new SourceSpan(start, Here)));
                break;
            }

            sb.Append(Advance());
        }

        var errorSpan = new SourceSpan(start, Here);
        var errorKind = isInterpolationResume ? TokenKind.InterpolationStringEnd : TokenKind.StringLiteral;
        return new Token(errorKind, sb.ToString(), errorSpan, sb.ToString());
    }

    // ----------------------------------------------------------------
    // Attributes: @main, @syncSafe, @Anything
    // ----------------------------------------------------------------

    private Token LexAttribute(SourceLocation start)
    {
        var sb = new StringBuilder("@");
        while (!IsAtEnd && IsIdentifierContinue(Current))
        {
            sb.Append(Advance());
        }

        var text = sb.ToString();
        var span = new SourceSpan(start, Here);
        return text switch
        {
            "@main" => new Token(TokenKind.AtMain, text, span),
            "@syncSafe" => new Token(TokenKind.AtSyncSafe, text, span),
            _ => new Token(TokenKind.AtUnknown, text, span, text[1..]),
        };
    }

    // ----------------------------------------------------------------
    // Operators / punctuation (maximal munch: longest match wins)
    // ----------------------------------------------------------------

    private Token LexOperatorOrPunctuation(SourceLocation start, char c)
    {
        TokenKind kind;
        string text;

        switch (c)
        {
            case '(':
                _openParenDepth++;
                kind = TokenKind.LParen;
                text = "(";
                break;

            case ')':
                kind = TokenKind.RParen;
                text = ")";
                _openParenDepth = Math.Max(0, _openParenDepth - 1);

                // If this ')' closes an active interpolation, resume
                // lexing the surrounding string literal instead of
                // returning a plain RParen token.
                if (_interpolationParenDepth.Count > 0 &&
                    _openParenDepth == _interpolationParenDepth.Peek())
                {
                    _interpolationParenDepth.Pop();
                    return LexStringLiteral(start, isInterpolationResume: true);
                }
                break;

            case '{': kind = TokenKind.LBrace; text = "{"; break;
            case '}': kind = TokenKind.RBrace; text = "}"; break;
            case '[': kind = TokenKind.LBracket; text = "["; break;
            case ']': kind = TokenKind.RBracket; text = "]"; break;
            case ',': kind = TokenKind.Comma; text = ","; break;
            case ';': kind = TokenKind.Semicolon; text = ";"; break;

            case '+':
                if (Match('=')) { kind = TokenKind.PlusEqual; text = "+="; }
                else { kind = TokenKind.Plus; text = "+"; }
                break;

            case '-':
                if (Match('=')) { kind = TokenKind.MinusEqual; text = "-="; }
                else if (Match('>')) { kind = TokenKind.Arrow; text = "->"; }
                else { kind = TokenKind.Minus; text = "-"; }
                break;

            case '*':
                if (Match('=')) { kind = TokenKind.StarEqual; text = "*="; }
                else { kind = TokenKind.Star; text = "*"; }
                break;

            case '/':
                // Comments are already stripped in SkipWhitespaceAndComments;
                // reaching here with '/' means a real division/compound op.
                if (Match('=')) { kind = TokenKind.SlashEqual; text = "/="; }
                else { kind = TokenKind.Slash; text = "/"; }
                break;

            case '%':
                kind = TokenKind.Percent;
                text = "%";
                break;

            case '=':
                if (Match('=')) { kind = TokenKind.EqualEqual; text = "=="; }
                else { kind = TokenKind.Equal; text = "="; }
                break;

            case '!':
                if (Match('=')) { kind = TokenKind.BangEqual; text = "!="; }
                else { kind = TokenKind.Bang; text = "!"; }
                break;

            case '<':
                if (Match('=')) { kind = TokenKind.LessEqual; text = "<="; }
                else { kind = TokenKind.Less; text = "<"; }
                break;

            case '>':
                if (Match('=')) { kind = TokenKind.GreaterEqual; text = ">="; }
                else { kind = TokenKind.Greater; text = ">"; }
                break;

            case '&':
                if (Match('&')) { kind = TokenKind.AmpAmp; text = "&&"; }
                else { kind = TokenKind.Amp; text = "&"; }
                break;

            case '|':
                if (Match('|')) { kind = TokenKind.PipePipe; text = "||"; }
                else { (kind, text) = Invalid(c); }
                break;

            case '?':
                if (Match('?')) { kind = TokenKind.QuestionQuestion; text = "??"; }
                else { kind = TokenKind.Question; text = "?"; }
                break;

            case '.':
                if (Current == '.' && Peek() == '<')
                {
                    Advance(); Advance();
                    kind = TokenKind.RangeHalfOpen; text = "..<";
                }
                else if (Current == '.' && Peek() == '.')
                {
                    Advance(); Advance();
                    kind = TokenKind.RangeClosed; text = "...";
                }
                else
                {
                    kind = TokenKind.Dot; text = ".";
                }
                break;

            case ':':
                if (Match(':')) { kind = TokenKind.ColonColon; text = "::"; }
                else { kind = TokenKind.Colon; text = ":"; }
                break;

            default:
                (kind, text) = Invalid(c);
                break;
        }

        var span = new SourceSpan(start, Here);

        // `as?` / `as!` are lexed as a single KwAs token followed by a
        // Question/Bang token by this scanner (simpler: two tokens, same
        // as Swift's own lexer does — "as" and "?" are separately
        // meaningful tokens that Parsing/ recombines). No special-casing
        // needed here; documented for clarity since grammar.md lists
        // `as?`/`as!` as if they were single operators.

        if (kind == TokenKind.Invalid)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"Unexpected character '{c}'.",
                span));
        }

        return new Token(kind, text, span);

        static (TokenKind, string) Invalid(char ch) => (TokenKind.Invalid, ch.ToString());
    }
}
