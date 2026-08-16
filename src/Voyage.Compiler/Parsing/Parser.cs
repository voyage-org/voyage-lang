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
        TokenKind.KwActor,
        TokenKind.KwGuard, TokenKind.KwSwitch,
        TokenKind.KwFor, TokenKind.KwRepeat,
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

    /// <summary>
    /// Looks ahead without consuming. Used by <see cref="ParseParameter"/>
    /// to distinguish `name: Type` from `externalLabel name: Type` (or
    /// `_ name: Type`) — the only place in this parser that currently
    /// needs more than one token of lookahead. If this stops being the
    /// only caller, that's fine; it was deliberately removed once before
    /// (as genuinely dead code) and re-added only once a real use
    /// existed, rather than kept "just in case."
    /// </summary>
    private Token PeekAt(int offset)
    {
        var i = _pos + offset;
        return i < _tokens.Count ? _tokens[i] : _tokens[^1]; // ^1 is EndOfFile
    }

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

        if (Check(TokenKind.KwStruct))
        {
            return ParseStructDeclaration();
        }

        if (Check(TokenKind.KwEnum))
        {
            return ParseEnumDeclaration();
        }

        if (Check(TokenKind.KwCase))
        {
            return ParseCaseDeclaration();
        }

        if (Check(TokenKind.KwProtocol))
        {
            return ParseProtocolDeclaration();
        }

        if (Check(TokenKind.KwExtension))
        {
            return ParseExtensionDeclaration();
        }

        if (Check(TokenKind.KwReturn))
        {
            return ParseReturnStatement();
        }

        if (Check(TokenKind.KwIf))
        {
            return ParseIfStatement();
        }

        if (Check(TokenKind.KwWhile))
        {
            return ParseWhileStatement();
        }

        if (Check(TokenKind.KwBreak))
        {
            return ParseSimpleKeywordStatement(span => new BreakStatement(span));
        }

        if (Check(TokenKind.KwContinue))
        {
            return ParseSimpleKeywordStatement(span => new ContinueStatement(span));
        }

        if (UnsupportedStatementStarts.Contains(Current.Kind))
        {
            return ParseUnsupportedStatement();
        }

        var start = Current.Span.Start;
        var expr = ParseExpression();

        if (TryGetAssignmentOperator(Current.Kind, out var assignOp))
        {
            Advance(); // consume the assignment operator
            var value = ParseExpression();
            var assignEnd = Current.Span.Start;
            ExpectStatementTerminator();
            return new AssignmentStatement(expr, assignOp, value, new SourceSpan(start, assignEnd));
        }

        var end = Current.Span.Start; // position right after the expression
        ExpectStatementTerminator();
        return new ExpressionStatement(expr, new SourceSpan(start, end));
    }

    private static bool TryGetAssignmentOperator(TokenKind kind, out AssignmentOperator op)
    {
        switch (kind)
        {
            case TokenKind.Equal: op = AssignmentOperator.Assign; return true;
            case TokenKind.PlusEqual: op = AssignmentOperator.AddAssign; return true;
            case TokenKind.MinusEqual: op = AssignmentOperator.SubtractAssign; return true;
            case TokenKind.StarEqual: op = AssignmentOperator.MultiplyAssign; return true;
            case TokenKind.SlashEqual: op = AssignmentOperator.DivideAssign; return true;
            default: op = default; return false;
        }
    }

    /// <summary>
    /// Parses `let`/`var` NAME [: Type] [= expr]. At least one of the
    /// type annotation or the initializer must be present — a bare
    /// `let x` with neither has no way to determine its type and is
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

        TypeNode? declaredType = null;
        if (Match(TokenKind.Colon))
        {
            declaredType = ParseType();
        }

        Expression? initializer = null;
        if (Match(TokenKind.Equal))
        {
            initializer = ParseExpression();
        }

        if (declaredType is null && initializer is null)
        {
            _diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                "Expected ':' or '=' — a binding needs either a type annotation " +
                "or an initializer (or both) so its type can be determined.",
                SourceSpan.At(Current.Span.Start)));

            while (!Check(TokenKind.Newline) && !IsAtEnd)
            {
                Advance();
            }
            return new UnsupportedStatement(new SourceSpan(start, Current.Span.Start));
        }

        var end = Current.Span.Start;
        ExpectStatementTerminator();
        return new BindingStatement(isMutable, name, declaredType, initializer, new SourceSpan(start, end));
    }

    /// <summary>
    /// Parses `func` NAME `(` [PARAM (`,` PARAM)*] `)` [`->` Type] `{` STATEMENT* `}`.
    /// Generic parameters (`&lt;T&gt;`/`where`) are not yet recognized — see
    /// FunctionDeclaration's remarks and Parsing/README.md. A missing
    /// return type is fine (implicit Void); a single-expression body is
    /// just parsed as one ExpressionStatement, with implicit-return
    /// lowering left to a later phase per ADR-0005.
    /// </summary>
    /// <summary>
    /// Parses `func` NAME [`&lt;` GENERICS `&gt;`] `(` PARAM, ... `)`
    /// [`->` Type] [`where` CONSTRAINTS] [`{` STATEMENT* `}`]. The body
    /// is optional — its absence (`Body == null`) means a protocol
    /// requirement (`func draw() -> String` with nothing after it); its
    /// presence, even as `{}`, means a real (possibly empty)
    /// implementation.
    /// </summary>
    private Statement ParseFunctionDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'func'

        var nameToken = Expect(TokenKind.Identifier, "Expected a function name after 'func'.");
        var name = nameToken.Text;
        var genericParameters = ParseGenericParameterList();

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

        var whereConstraints = ParseWhereClause();

        if (Check(TokenKind.LBrace))
        {
            Advance(); // consume '{'
            var body = ParseBlockStatements();
            var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to close the function body.");
            return new FunctionDeclaration(name, genericParameters, parameters, returnType, whereConstraints, body, new SourceSpan(start, closeBrace.Span.End));
        }

        // No '{' — this is a bodyless protocol requirement. Still needs a
        // proper statement terminator, same as any other statement.
        var end = Current.Span.Start;
        ExpectStatementTerminator();
        return new FunctionDeclaration(name, genericParameters, parameters, returnType, whereConstraints, null, new SourceSpan(start, end));
    }

    /// <summary>
    /// Parses an optional `: A, B, ...` conformance/inheritance clause,
    /// shared by `struct`/`enum`/`protocol`/`extension`. Returns an empty
    /// list when no `:` is present.
    /// </summary>
    private List<string> ParseConformanceClause()
    {
        var names = new List<string>();
        if (Match(TokenKind.Colon))
        {
            names.Add(Expect(TokenKind.Identifier, "Expected a protocol name.").Text);
            while (Match(TokenKind.Comma))
            {
                names.Add(Expect(TokenKind.Identifier, "Expected a protocol name.").Text);
            }
        }
        return names;
    }

    /// <summary>
    /// Parses a single `TypeConstraint`: NAME [`:` PROTOCOL (`&amp;` PROTOCOL)*].
    /// Shared shape for both an inline `&lt;T: P&gt;` generic-parameter entry
    /// and a `where T: P` clause entry — see `TypeConstraint`'s remarks in
    /// Ast.cs for why one node/parse method covers both.
    /// </summary>
    private TypeConstraint ParseTypeConstraint()
    {
        var start = Current.Span.Start;
        var nameToken = Expect(TokenKind.Identifier, "Expected a type name.");
        var protocols = new List<string>();
        if (Match(TokenKind.Colon))
        {
            protocols.Add(Expect(TokenKind.Identifier, "Expected a protocol name.").Text);
            while (Match(TokenKind.Amp))
            {
                protocols.Add(Expect(TokenKind.Identifier, "Expected a protocol name.").Text);
            }
        }
        var end = Current.Span.Start;
        return new TypeConstraint(nameToken.Text, protocols, new SourceSpan(start, end));
    }

    /// <summary>
    /// Parses an optional `&lt;T, U: Protocol, ...&gt;` generic-parameter
    /// list. Returns an empty list when no `&lt;` is present. Since `&lt;`/
    /// `&gt;` are also the comparison operators, this only works safely
    /// because every call site is in a declaration position (right after
    /// a name, before `(`/`{`) rather than inside expression parsing —
    /// there's no real ambiguity to resolve at those call sites.
    /// </summary>
    private List<TypeConstraint> ParseGenericParameterList()
    {
        var result = new List<TypeConstraint>();
        if (Match(TokenKind.Less))
        {
            result.Add(ParseTypeConstraint());
            while (Match(TokenKind.Comma))
            {
                result.Add(ParseTypeConstraint());
            }
            Expect(TokenKind.Greater, "Expected '>' to close the generic parameter list.");
        }
        return result;
    }

    /// <summary>
    /// Parses an optional trailing `where T: Protocol, U: Protocol, ...`
    /// clause. Returns an empty list when no `where` is present.
    /// </summary>
    private List<TypeConstraint> ParseWhereClause()
    {
        var result = new List<TypeConstraint>();
        if (Match(TokenKind.KwWhere))
        {
            result.Add(ParseTypeConstraint());
            while (Match(TokenKind.Comma))
            {
                result.Add(ParseTypeConstraint());
            }
        }
        return result;
    }

    /// <summary>
    /// Parses `struct` NAME [`&lt;` GENERICS `&gt;`] [`: ` CONFORMANCE]
    /// [`where` CONSTRAINTS] `{` MEMBER* `}`. Members reuse
    /// `ParseBlockStatements` directly — `var`/`let` properties and
    /// `func` methods are both just statements already, so no new
    /// member-parsing infrastructure was needed.
    /// </summary>
    private Statement ParseStructDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'struct'

        var nameToken = Expect(TokenKind.Identifier, "Expected a struct name after 'struct'.");
        var name = nameToken.Text;
        var genericParameters = ParseGenericParameterList();
        var conformances = ParseConformanceClause();
        var whereConstraints = ParseWhereClause();

        Expect(TokenKind.LBrace, "Expected '{' to begin the struct body.");
        var members = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to close the struct body.");

        return new StructDeclaration(name, genericParameters, conformances, whereConstraints, members, new SourceSpan(start, closeBrace.Span.End));
    }

    /// <summary>
    /// Parses `enum` NAME [`&lt;` GENERICS `&gt;`] [`: ` CONFORMANCE]
    /// [`where` CONSTRAINTS] `{` MEMBER* `}`. Same block-statement reuse
    /// as `ParseStructDeclaration` — see `EnumDeclaration`'s remarks for
    /// why the parser doesn't restrict members to only `case`
    /// declarations.
    /// </summary>
    private Statement ParseEnumDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'enum'

        var nameToken = Expect(TokenKind.Identifier, "Expected an enum name after 'enum'.");
        var name = nameToken.Text;
        var genericParameters = ParseGenericParameterList();
        var conformances = ParseConformanceClause();
        var whereConstraints = ParseWhereClause();

        Expect(TokenKind.LBrace, "Expected '{' to begin the enum body.");
        var members = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to close the enum body.");

        return new EnumDeclaration(name, genericParameters, conformances, whereConstraints, members, new SourceSpan(start, closeBrace.Span.End));
    }

    /// <summary>
    /// Parses `protocol` NAME [`&lt;` GENERICS `&gt;`] [`: ` INHERITANCE]
    /// [`where` CONSTRAINTS] `{` MEMBER* `}`. Same block-statement reuse
    /// as `struct`/`enum` — see `ProtocolDeclaration`'s remarks.
    /// </summary>
    private Statement ParseProtocolDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'protocol'

        var nameToken = Expect(TokenKind.Identifier, "Expected a protocol name after 'protocol'.");
        var name = nameToken.Text;
        var genericParameters = ParseGenericParameterList();
        var inherited = ParseConformanceClause();
        var whereConstraints = ParseWhereClause();

        Expect(TokenKind.LBrace, "Expected '{' to begin the protocol body.");
        var members = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to close the protocol body.");

        return new ProtocolDeclaration(name, genericParameters, inherited, whereConstraints, members, new SourceSpan(start, closeBrace.Span.End));
    }

    /// <summary>
    /// Parses `extension` NAME [`&lt;` GENERICS `&gt;`] [`: ` CONFORMANCE]
    /// [`where` CONSTRAINTS] `{` MEMBER* `}`. Same block-statement reuse
    /// as `struct`/`enum`/`protocol` — see `ExtensionDeclaration`'s
    /// remarks, including the real Swift pattern of a `where` clause with
    /// no `&lt;...&gt;` list (`extension Array where Element: Equatable`,
    /// constraining an already-generic extended type).
    /// </summary>
    private Statement ParseExtensionDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'extension'

        var nameToken = Expect(TokenKind.Identifier, "Expected a type name after 'extension'.");
        var extendedType = nameToken.Text;
        var genericParameters = ParseGenericParameterList();
        var conformances = ParseConformanceClause();
        var whereConstraints = ParseWhereClause();

        Expect(TokenKind.LBrace, "Expected '{' to begin the extension body.");
        var members = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to close the extension body.");

        return new ExtensionDeclaration(extendedType, genericParameters, conformances, whereConstraints, members, new SourceSpan(start, closeBrace.Span.End));
    }

    /// <summary>
    /// Parses `case` NAME [`(` PARAM, ... `)`]. The associated-value list
    /// reuses `ParseParameter` directly, since `case circle(radius:
    /// Double)`'s parenthesized list has the exact same shape as a
    /// function parameter list. Swift's comma-separated multi-case
    /// shorthand (`case a, b, c`) is not yet supported.
    /// </summary>
    private Statement ParseCaseDeclaration()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'case'

        var nameToken = Expect(TokenKind.Identifier, "Expected a case name after 'case'.");
        var name = nameToken.Text;
        var end = nameToken.Span.End;

        var associatedValues = new List<Parameter>();
        if (Check(TokenKind.LParen))
        {
            Advance(); // consume '('
            if (!Check(TokenKind.RParen))
            {
                associatedValues.Add(ParseParameter());
                while (Match(TokenKind.Comma))
                {
                    associatedValues.Add(ParseParameter());
                }
            }
            var closeParen = Expect(TokenKind.RParen, "Expected ')' to close the case's associated values.");
            end = closeParen.Span.End;
        }

        ExpectStatementTerminator();
        return new CaseDeclaration(name, associatedValues, new SourceSpan(start, end));
    }

    /// <summary>
    /// Parses a single parameter, in any of three shapes: `name: Type`
    /// (external label defaults to the same as the internal name, per
    /// Swift's implicit-same-label default — applied here so downstream
    /// phases never re-derive it), `_ name: Type` (external label
    /// suppressed — `ExternalLabel` is null), or `external name: Type`
    /// (an explicit, distinct external label). Disambiguated via one
    /// token of lookahead: if the token after the first identifier is
    /// itself an identifier (rather than `:`), the first identifier was
    /// a label, not the parameter's name.
    /// </summary>
    private Parameter ParseParameter()
    {
        var start = Current.Span.Start;
        string? externalLabel;
        string name;

        if (Check(TokenKind.Identifier) && PeekAt(1).Kind == TokenKind.Identifier)
        {
            // Two identifiers in a row: the first is an external label
            // (either a real label, or '_' to suppress one) and the
            // second is the internal name.
            var labelToken = Advance();
            externalLabel = labelToken.Text == "_" ? null : labelToken.Text;
            name = Advance().Text;
        }
        else
        {
            var nameToken = Expect(TokenKind.Identifier, "Expected a parameter name.");
            name = nameToken.Text;
            externalLabel = name; // Swift's implicit default: same as internal name
        }

        Expect(TokenKind.Colon, "Expected ':' after parameter name.");
        var type = ParseType();
        return new Parameter(externalLabel, name, type, new SourceSpan(start, type.Span.End));
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
    /// Parses `if` COND `{` STATEMENT* `}` [`else` (`{` STATEMENT* `}` | `if` ...)].
    /// `if let`/`if case` conditional binding forms (grammar.md Section 6)
    /// are not yet recognized — only a plain boolean-valued condition
    /// expression. `else` must directly follow the `if` body's closing
    /// `}` with no newline in between (matching common Swift style);
    /// `else` on its own line is not yet supported.
    /// </summary>
    private Statement ParseIfStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'if'

        var condition = ParseExpression();
        Expect(TokenKind.LBrace, "Expected '{' to begin the 'if' body.");
        var thenBranch = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to end the 'if' body.");
        var end = closeBrace.Span.End;

        List<Statement>? elseBranch = null;
        if (Check(TokenKind.KwElse))
        {
            Advance(); // consume 'else'
            if (Check(TokenKind.KwIf))
            {
                var nestedIf = ParseIfStatement();
                elseBranch = [nestedIf];
                end = nestedIf.Span.End;
            }
            else
            {
                Expect(TokenKind.LBrace, "Expected '{' to begin the 'else' body.");
                elseBranch = ParseBlockStatements();
                var elseCloseBrace = Expect(TokenKind.RBrace, "Expected '}' to end the 'else' body.");
                end = elseCloseBrace.Span.End;
            }
        }

        return new IfStatement(condition, thenBranch, elseBranch, new SourceSpan(start, end));
    }

    /// <summary>Parses `while` COND `{` STATEMENT* `}`.</summary>
    private Statement ParseWhileStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'while'

        var condition = ParseExpression();
        Expect(TokenKind.LBrace, "Expected '{' to begin the 'while' body.");
        var body = ParseBlockStatements();
        var closeBrace = Expect(TokenKind.RBrace, "Expected '}' to end the 'while' body.");

        return new WhileStatement(condition, body, new SourceSpan(start, closeBrace.Span.End));
    }

    /// <summary>
    /// Shared implementation for bare, argument-less keyword statements
    /// (`break`, `continue`). Labeled variants (`break outerLoop`) aren't
    /// supported yet — voyage-lang has no loop labels yet — so this
    /// always consumes exactly one token. Caller is expected to have
    /// already checked which keyword is current via `Check(...)`.
    /// </summary>
    private Statement ParseSimpleKeywordStatement(Func<SourceSpan, Statement> build)
    {
        var token = Advance();
        var span = token.Span;
        ExpectStatementTerminator();
        return build(span);
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
