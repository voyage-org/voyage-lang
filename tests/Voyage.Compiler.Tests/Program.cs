using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Lexing;
using Voyage.Compiler.Parsing;
using Voyage.Compiler.Semantics;

var failures = 0;
var passes = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passes++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"  FAIL  {name}" + (detail is null ? "" : $" — {detail}"));
    }
}

IReadOnlyList<Token> Lex(string source, out InMemoryDiagnosticSink sink)
{
    sink = new InMemoryDiagnosticSink();
    return Lexer.Tokenize(source, sink);
}

// A token kind list with Newline/EOF stripped out, for tests that don't
// care about trailing structural tokens.
List<TokenKind> KindsOnly(IReadOnlyList<Token> tokens) =>
    tokens.Select(t => t.Kind)
        .Where(k => k != TokenKind.EndOfFile)
        .ToList();

// ---------------------------------------------------------------------
// 1. The actual hello world milestone from ADR-0003 / ADR-0010
// ---------------------------------------------------------------------
Console.WriteLine("=== Milestone: samples/hello.voy ===");
{
    var samplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "hello.voy");
    samplePath = Path.GetFullPath(samplePath);
    var source = File.ReadAllText(samplePath);
    var tokens = Lex(source, out var sink);

    foreach (var t in tokens)
    {
        Console.WriteLine($"    {t}");
    }

    Check("no diagnostics", sink.Diagnostics.Count == 0,
        string.Join("; ", sink.Diagnostics));
    Check("token count is print ( \"...\" ) NEWLINE EOF",
        tokens.Count == 6,
        $"got {tokens.Count}");
    Check("token[0] is Identifier 'print'",
        tokens[0].Kind == TokenKind.Identifier && tokens[0].Text == "print");
    Check("token[1] is LParen",
        tokens[1].Kind == TokenKind.LParen);
    Check("token[2] is StringLiteral 'Hello, Voyage.'",
        tokens[2].Kind == TokenKind.StringLiteral && (string?)tokens[2].LiteralValue == "Hello, Voyage.");
    Check("token[3] is RParen",
        tokens[3].Kind == TokenKind.RParen);
    Check("token[4] is Newline",
        tokens[4].Kind == TokenKind.Newline);
    Check("token[5] is EndOfFile",
        tokens[5].Kind == TokenKind.EndOfFile);
}

// ---------------------------------------------------------------------
// 2. Keywords, including the ones added across every ADR
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Keywords ===");
{
    var source = "func struct enum protocol extension actor associatedtype " +
                 "async await task spawn join atomic " +
                 "throws throw try catch do " +
                 "using defer weak some any Self";
    var kinds = KindsOnly(Lex(source, out var sink)).Where(k => k != TokenKind.Newline).ToList();

    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("all keyword tokens recognized (none fell through to Identifier)",
        kinds.All(k => k != TokenKind.Identifier),
        string.Join(",", kinds));
}

// ---------------------------------------------------------------------
// 3. Attributes: @main, @syncSafe, and an unknown future attribute
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Attributes ===");
{
    var tokens = Lex("@main @syncSafe @future", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("@main -> AtMain", tokens[0].Kind == TokenKind.AtMain);
    Check("@syncSafe -> AtSyncSafe", tokens[1].Kind == TokenKind.AtSyncSafe);
    Check("@future -> AtUnknown carrying name 'future'",
        tokens[2].Kind == TokenKind.AtUnknown && (string?)tokens[2].LiteralValue == "future");
}

// ---------------------------------------------------------------------
// 4. Operators: maximal munch (longest match wins)
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Operator maximal munch ===");
{
    (string source, TokenKind expected)[] cases =
    [
        ("..<", TokenKind.RangeHalfOpen),
        ("...", TokenKind.RangeClosed),
        ("??", TokenKind.QuestionQuestion),
        ("?", TokenKind.Question),
        ("->", TokenKind.Arrow),
        ("::", TokenKind.ColonColon),
        ("&&", TokenKind.AmpAmp),
        ("||", TokenKind.PipePipe),
        ("<=", TokenKind.LessEqual),
        (">=", TokenKind.GreaterEqual),
        ("==", TokenKind.EqualEqual),
        ("!=", TokenKind.BangEqual),
        ("+=", TokenKind.PlusEqual),
    ];

    foreach (var (source, expected) in cases)
    {
        var tokens = Lex(source, out var sink);
        Check($"'{source}' -> {expected}",
            sink.Diagnostics.Count == 0 && tokens[0].Kind == expected,
            $"got {tokens[0].Kind}");
    }
}

// ---------------------------------------------------------------------
// 5. Numeric literals: decimal, hex, binary, octal, float, exponent
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Numeric literals ===");
{
    (string source, TokenKind kind, object expected)[] cases =
    [
        ("42", TokenKind.IntegerLiteral, 42L),
        ("0x2A", TokenKind.IntegerLiteral, 42L),
        ("0b101010", TokenKind.IntegerLiteral, 42L),
        ("0o52", TokenKind.IntegerLiteral, 42L),
        ("3.14", TokenKind.FloatLiteral, 3.14),
        ("1.0e10", TokenKind.FloatLiteral, 1.0e10),
    ];

    foreach (var (source, kind, expected) in cases)
    {
        var tokens = Lex(source, out var sink);
        var ok = sink.Diagnostics.Count == 0
                 && tokens[0].Kind == kind
                 && Equals(tokens[0].LiteralValue, expected);
        Check($"'{source}' -> {kind} ({expected})", ok,
            $"got {tokens[0].Kind} ({tokens[0].LiteralValue})");
    }
}

// ---------------------------------------------------------------------
// 6. String interpolation, including a nested call inside \( ... )
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== String interpolation ===");
{
    var tokens = Lex("\"Hello, \\(name)!\"", out var sink);
    foreach (var t in tokens) Console.WriteLine($"    {t}");

    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("[0] InterpolationStringStart 'Hello, '",
        tokens[0].Kind == TokenKind.InterpolationStringStart && (string?)tokens[0].LiteralValue == "Hello, ");
    Check("[1] Identifier 'name'",
        tokens[1].Kind == TokenKind.Identifier && tokens[1].Text == "name");
    Check("[2] InterpolationStringEnd '!'",
        tokens[2].Kind == TokenKind.InterpolationStringEnd && (string?)tokens[2].LiteralValue == "!");
}
{
    // A call expression *inside* the interpolation must not close the
    // interpolation at its own inner ')' — this is the specific case
    // the _interpolationParenDepth tracking exists for.
    var tokens = Lex("\"Result: \\(f(x, y))!\"", out var sink);
    foreach (var t in tokens) Console.WriteLine($"    {t}");

    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var kinds = tokens.Select(t => t.Kind).ToList();
    var expected = new List<TokenKind>
    {
        TokenKind.InterpolationStringStart, // "Result: "
        TokenKind.Identifier,               // f
        TokenKind.LParen,                   // (
        TokenKind.Identifier,               // x
        TokenKind.Comma,                    // ,
        TokenKind.Identifier,               // y
        TokenKind.RParen,                   // ) -- inner, must NOT close string
        TokenKind.InterpolationStringEnd,   // "!"
        TokenKind.EndOfFile,
    };
    Check("nested call inside interpolation does not prematurely close it",
        kinds.SequenceEqual(expected),
        $"got [{string.Join(", ", kinds)}]");
}

// ---------------------------------------------------------------------
// 7. Comments: line and nested block
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Comments ===");
{
    var tokens = Lex("let x = 1 // trailing comment\nlet y = 2", out var sink);
    var kinds = KindsOnly(tokens);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("line comment fully skipped (no stray tokens from it)",
        kinds.Count(k => k == TokenKind.KwLet) == 2,
        string.Join(",", kinds));
}
{
    var tokens = Lex("let /* outer /* nested */ still outer */ x = 1", out var sink);
    Check("no diagnostics for nested block comment", sink.Diagnostics.Count == 0,
        string.Join("; ", sink.Diagnostics));
    var kinds = KindsOnly(tokens);
    Check("nested block comment fully skipped",
        kinds is [TokenKind.KwLet, TokenKind.Identifier, TokenKind.Equal, TokenKind.IntegerLiteral],
        string.Join(",", kinds));
}
{
    var tokens = Lex("let x = /* unterminated", out var sink);
    Check("unterminated block comment reports a diagnostic",
        sink.Diagnostics.Count == 1 && sink.HasErrors);
}

// ---------------------------------------------------------------------
// 8. Newline handling: significant, but collapses blank-line runs
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Newline handling ===");
{
    var tokens = Lex("let x = 1\n\n\nlet y = 2", out var sink);
    var newlineCount = tokens.Count(t => t.Kind == TokenKind.Newline);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("three consecutive blank lines collapse into one Newline token",
        newlineCount == 1,
        $"got {newlineCount} newline tokens");
}

// ---------------------------------------------------------------------
// 9. Parsing/ milestone: samples/hello.voy -> AST -> dump
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Parsing/ Milestone: samples/hello.voy -> AST ===");
{
    var samplePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "hello.voy");
    samplePath = Path.GetFullPath(samplePath);
    var source = File.ReadAllText(samplePath);

    var lexSink = new InMemoryDiagnosticSink();
    var tokens = Lexer.Tokenize(source, lexSink);

    var parseSink = new InMemoryDiagnosticSink();
    var unit = Parser.Parse(tokens, parseSink);

    var dump = AstPrinter.Print(unit);
    Console.WriteLine(dump);

    Check("no lexer diagnostics", lexSink.Diagnostics.Count == 0);
    Check("no parser diagnostics", parseSink.Diagnostics.Count == 0,
        string.Join("; ", parseSink.Diagnostics));
    Check("CompilationUnit has exactly one statement",
        unit.Statements.Count == 1, $"got {unit.Statements.Count}");

    var stmt = unit.Statements[0];
    Check("statement is an ExpressionStatement", stmt is ExpressionStatement);

    if (stmt is ExpressionStatement { Expression: CallExpression call })
    {
        Check("call's callee is IdentifierExpression 'print'",
            call.Callee is IdentifierExpression { Name: "print" });
        Check("call has exactly one argument", call.Arguments.Count == 1,
            $"got {call.Arguments.Count}");
        Check("argument is StringLiteralExpression 'Hello, Voyage.'",
            call.Arguments is [StringLiteralExpression { Value: "Hello, Voyage." }]);
    }
    else
    {
        Check("statement's expression is a CallExpression", false,
            $"got {stmt}");
    }
}

// ---------------------------------------------------------------------
// 10. Parser: multi-argument calls, nested calls, literals, parens
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Parser: expressions beyond hello world ===");

CompilationUnit ParseSource(string source, out InMemoryDiagnosticSink sink)
{
    var lexSink = new InMemoryDiagnosticSink();
    var tokens = Lexer.Tokenize(source, lexSink);
    sink = new InMemoryDiagnosticSink();
    return Parser.Parse(tokens, sink);
}

{
    var unit = ParseSource("add(1, 2, 3)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("multi-arg call has 3 arguments", call?.Arguments.Count == 3,
        $"got {call?.Arguments.Count}");
}
{
    // A call expression whose argument is itself a call expression —
    // exercises ParsePostfix/ParseExpression recursion.
    var unit = ParseSource("outer(inner(1))", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var outer = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    var innerArgOk = outer?.Arguments is [CallExpression { Callee: IdentifierExpression { Name: "inner" } }];
    Check("nested call expression parses correctly", innerArgOk);
}
{
    var unit = ParseSource("f(42, 3.14, true, false, nil)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    var argsOk = call?.Arguments is
    [
        IntegerLiteralExpression { Value: 42 },
        FloatLiteralExpression { Value: 3.14 },
        BooleanLiteralExpression { Value: true },
        BooleanLiteralExpression { Value: false },
        NilLiteralExpression,
    ];
    Check("all literal kinds parse correctly as call arguments", argsOk);
}
{
    var unit = ParseSource("f((1))", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    var ok = call?.Arguments is
        [ParenthesizedExpression { Inner: IntegerLiteralExpression { Value: 1 } }];
    Check("parenthesized expression parses correctly", ok);
}

// ---------------------------------------------------------------------
// 11. Parser: graceful degradation on not-yet-supported constructs
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Parser: not-yet-supported constructs degrade gracefully ===");
{
    // `actor` is real, valid voyage-lang (ADR-0002/ADR-0006) but this
    // milestone's parser doesn't implement it yet (`struct`/`enum`/
    // `protocol`/`extension` are now all supported — see the sections
    // below — but `actor` isn't). It should report a diagnostic and
    // produce an UnsupportedStatement rather than crashing or silently
    // losing the rest of the file.
    var unit = ParseSource("actor Counter {}\nprint(\"after\")", out var sink);
    Check("exactly one diagnostic reported (the 'actor' warning)",
        sink.Diagnostics.Count == 1, string.Join("; ", sink.Diagnostics));
    Check("two statements produced despite the unsupported first one",
        unit.Statements.Count == 2, $"got {unit.Statements.Count}");
    Check("statement[0] is UnsupportedStatement",
        unit.Statements[0] is UnsupportedStatement);
    Check("statement[1] still parses correctly (parser recovered)",
        unit.Statements[1] is ExpressionStatement
        {
            Expression: CallExpression
            {
                Callee: IdentifierExpression { Name: "print" },
                Arguments: [StringLiteralExpression { Value: "after" }],
            },
        });
}
{
    // String interpolation now genuinely works — this exact case (a
    // single embedded identifier as a call argument) was the "known
    // unsupported" example throughout every earlier milestone; it's now
    // a real, positively-tested feature.
    var unit = ParseSource("print(\"Hello, \\(name)!\")", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("interpolated string parses with the expected text/expression segments",
        call?.Arguments is
        [
            InterpolatedStringExpression
            {
                Segments:
                [
                    InterpolatedStringTextSegment { Text: "Hello, " },
                    InterpolatedStringExpressionSegment { Expression: IdentifierExpression { Name: "name" } },
                    InterpolatedStringTextSegment { Text: "!" },
                ],
            },
        ]);
}

// ---------------------------------------------------------------------
// 12. Binding statements: let/var
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Binding statements ===");
{
    var unit = ParseSource("let x = 10", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("'let x = 10' parses as an immutable BindingStatement",
        unit.Statements is [BindingStatement { IsMutable: false, Name: "x", Initializer: IntegerLiteralExpression { Value: 10 } }]);
}
{
    var unit = ParseSource("var y = 20", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("'var y = 20' parses as a mutable BindingStatement",
        unit.Statements is [BindingStatement { IsMutable: true, Name: "y", Initializer: IntegerLiteralExpression { Value: 20 } }]);
}
{
    // Type annotations are now real — should parse a genuine TypeNode
    // and use it, with no diagnostics.
    var unit = ParseSource("let z: Int = 30", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("annotation parses as a real TypeNode alongside the initializer",
        unit.Statements is
        [
            BindingStatement
            {
                IsMutable: false,
                Name: "z",
                DeclaredType: NamedTypeNode { Name: "Int", IsOptional: false },
                Initializer: IntegerLiteralExpression { Value: 30 },
            },
        ]);
}
{
    // Annotation-only bindings (no initializer) are now valid too — this
    // is exactly the struct-property shape (`var x: Double`).
    var unit = ParseSource("var x: Double", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("annotation-only binding (no initializer) parses correctly",
        unit.Statements is
        [
            BindingStatement
            {
                IsMutable: true,
                Name: "x",
                DeclaredType: NamedTypeNode { Name: "Double", IsOptional: false },
                Initializer: null,
            },
        ]);
}
{
    // Optional type annotation: the trailing '?' sugar.
    var unit = ParseSource("var name: String?", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("optional type annotation ('?') parses correctly",
        unit.Statements is [BindingStatement { DeclaredType: NamedTypeNode { Name: "String", IsOptional: true } }]);
}
{
    // No initializer at all isn't supported yet — should degrade
    // gracefully (error + UnsupportedStatement), same recovery contract
    // as every other not-yet-supported construct, and the parser should
    // still pick back up correctly on the next line.
    var unit = ParseSource("let x\nprint(\"after\")", out var sink);
    Check("exactly one error about the missing initializer",
        sink.Diagnostics.Count == 1 && sink.HasErrors,
        string.Join("; ", sink.Diagnostics));
    Check("two statements produced, parser recovered on line 2",
        unit.Statements is
        [
            UnsupportedStatement,
            ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "print" } } },
        ]);
}

// ---------------------------------------------------------------------
// 13. Binary/unary operators: precedence and associativity
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Operator precedence and associativity ===");
{
    // Multiplication binds tighter than addition: 1 + 2 * 3 == 1 + (2 * 3)
    var unit = ParseSource("f(1 + 2 * 3)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'1 + 2 * 3' parses as Add(1, Multiply(2, 3))",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.Add,
                Left: IntegerLiteralExpression { Value: 1 },
                Right: BinaryExpression
                {
                    Operator: BinaryOperator.Multiply,
                    Left: IntegerLiteralExpression { Value: 2 },
                    Right: IntegerLiteralExpression { Value: 3 },
                },
            },
        ]);
}
{
    // Explicit parens override precedence: (1 + 2) * 3
    var unit = ParseSource("f((1 + 2) * 3)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'(1 + 2) * 3' parses as Multiply(Paren(Add(1,2)), 3)",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.Multiply,
                Left: ParenthesizedExpression { Inner: BinaryExpression { Operator: BinaryOperator.Add } },
                Right: IntegerLiteralExpression { Value: 3 },
            },
        ]);
}
{
    // Addition is left-associative: 1 + 2 + 3 == (1 + 2) + 3
    var unit = ParseSource("f(1 + 2 + 3)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'1 + 2 + 3' is left-associative: Add(Add(1,2), 3)",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.Add,
                Left: BinaryExpression { Operator: BinaryOperator.Add, Left: IntegerLiteralExpression { Value: 1 }, Right: IntegerLiteralExpression { Value: 2 } },
                Right: IntegerLiteralExpression { Value: 3 },
            },
        ]);
}
{
    // Nil-coalescing is right-associative: a ?? b ?? c == a ?? (b ?? c)
    var unit = ParseSource("f(a ?? b ?? c)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'a ?? b ?? c' is right-associative: NilCoalescing(a, NilCoalescing(b,c))",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.NilCoalescing,
                Left: IdentifierExpression { Name: "a" },
                Right: BinaryExpression { Operator: BinaryOperator.NilCoalescing, Left: IdentifierExpression { Name: "b" }, Right: IdentifierExpression { Name: "c" } },
            },
        ]);
}
{
    // || binds looser than &&: true && false || true == (true && false) || true
    var unit = ParseSource("f(true && false || true)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'&&' binds tighter than '||'",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.LogicalOr,
                Left: BinaryExpression { Operator: BinaryOperator.LogicalAnd },
                Right: BooleanLiteralExpression { Value: true },
            },
        ]);
}
{
    // Unary binds tighter than binary: -x + 1 == (-x) + 1
    var unit = ParseSource("f(-x + 1)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'-x + 1' parses as Add(Negate(x), 1)",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.Add,
                Left: UnaryExpression { Operator: UnaryOperator.Negate, Operand: IdentifierExpression { Name: "x" } },
                Right: IntegerLiteralExpression { Value: 1 },
            },
        ]);
}
{
    var unit = ParseSource("f(!flag && ready)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'!flag && ready' parses as And(Not(flag), ready)",
        call?.Arguments is
        [
            BinaryExpression
            {
                Operator: BinaryOperator.LogicalAnd,
                Left: UnaryExpression { Operator: UnaryOperator.LogicalNot, Operand: IdentifierExpression { Name: "flag" } },
                Right: IdentifierExpression { Name: "ready" },
            },
        ]);
}
{
    var unit = ParseSource("let sum = 1 + 2", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    Check("binding initializer can be a full binary expression",
        unit.Statements is
        [
            BindingStatement
            {
                Name: "sum",
                Initializer: BinaryExpression { Operator: BinaryOperator.Add, Left: IntegerLiteralExpression { Value: 1 }, Right: IntegerLiteralExpression { Value: 2 } },
            },
        ]);
}

// ---------------------------------------------------------------------
// 14. Function declarations: params, return type, body, return statements
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Function declarations ===");
{
    // The grammar.md Section 3 basic example: typed params, a return
    // type, and an explicit `return` in a multi-... well, single-
    // statement body.
    var unit = ParseSource("func add(a: Int, b: Int) -> Int { return a + b }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("'func add(a: Int, b: Int) -> Int { return a + b }' parses as expected",
        unit.Statements is
        [
            FunctionDeclaration
            {
                Name: "add",
                Parameters:
                [
                    Parameter { Name: "a", Type: NamedTypeNode { Name: "Int", IsOptional: false } },
                    Parameter { Name: "b", Type: NamedTypeNode { Name: "Int", IsOptional: false } },
                ],
                ReturnType: NamedTypeNode { Name: "Int", IsOptional: false },
                Body:
                [
                    ReturnStatement
                    {
                        Value: BinaryExpression
                        {
                            Operator: BinaryOperator.Add,
                            Left: IdentifierExpression { Name: "a" },
                            Right: IdentifierExpression { Name: "b" },
                        },
                    },
                ],
            },
        ]);
}
{
    // Single-expression body: no `return` keyword needed at the parse
    // level (ADR-0005 implicit return is a Lowering concern) — the
    // body is just an ExpressionStatement, same as any other statement.
    // Also exercises the "no trailing newline before `}`" terminator
    // relaxation, and a `String?` optional return type. (Plain string
    // literal, not interpolated — string interpolation is still a
    // separate not-yet-supported parser feature; see section 11.)
    var unit = ParseSource("func greet(name: String) -> String? { \"Hello there!\" }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("single-expression body with optional return type parses",
        unit.Statements is
        [
            FunctionDeclaration
            {
                Name: "greet",
                Parameters: [Parameter { Name: "name", Type: NamedTypeNode { Name: "String", IsOptional: false } }],
                ReturnType: NamedTypeNode { Name: "String", IsOptional: true },
                Body: [ExpressionStatement],
            },
        ]);
}
{
    // No params, no return type -> implicit Void, empty body.
    var unit = ParseSource("func doNothing() {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("'func doNothing() {}' parses with no params, no return type, empty body",
        unit.Statements is
        [
            FunctionDeclaration
            {
                Name: "doNothing",
                Parameters: [],
                ReturnType: null,
                Body: [],
            },
        ]);
}
{
    // Bare `return` with no value.
    var unit = ParseSource("func noop() {\n    return\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("bare 'return' with no value parses as ReturnStatement { Value: null }",
        unit.Statements is
        [
            FunctionDeclaration { Body: [ReturnStatement { Value: null }] },
        ]);
}
{
    // Functions nest inside function bodies as ordinary statements —
    // no special-casing needed since ParseBlockStatements just calls
    // back into ParseStatement.
    var unit = ParseSource("func outer() {\n    func inner() -> Int { return 1 }\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("a nested 'func' inside a function body parses correctly",
        unit.Statements is
        [
            FunctionDeclaration
            {
                Name: "outer",
                Body: [FunctionDeclaration { Name: "inner", ReturnType: NamedTypeNode { Name: "Int" } }],
            },
        ]);
}
{
    // AST dump sanity check via AstPrinter, same style as section 9's
    // hello.voy dump — exercises every new node kind's Write() case.
    var unit = ParseSource("func add(a: Int, b: Int) -> Int { return a + b }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0);
    var dump = AstPrinter.Print(unit);
    Check("AST dump mentions FunctionDeclaration 'add'", dump.Contains("FunctionDeclaration 'add'"));
    Check("AST dump mentions both Parameter nodes", dump.Contains("name='a'") && dump.Contains("name='b'"));
    Check("AST dump mentions the Int TypeNodes", dump.Contains("TypeNode 'Int'"));
    Check("AST dump mentions ReturnStatement", dump.Contains("ReturnStatement"));
}

// ---------------------------------------------------------------------
// 14. Control flow: if / else / else-if, while, break, continue
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Control flow: if/else, while, break, continue ===");
{
    var unit = ParseSource("if x { print(\"yes\") }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("plain 'if' with no else parses correctly",
        unit.Statements is
        [
            IfStatement
            {
                Condition: IdentifierExpression { Name: "x" },
                ThenBranch: [ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "print" } } }],
                ElseBranch: null,
            },
        ]);
}
{
    var unit = ParseSource("if x { print(\"yes\") } else { print(\"no\") }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("'if'/'else' both parse correctly",
        unit.Statements is
        [
            IfStatement
            {
                ThenBranch: [ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "print" } } }],
                ElseBranch: [ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "print" } } }],
            },
        ]);
}
{
    // else-if desugars to else { if ... }: a single-element ElseBranch
    // holding another IfStatement, per Ast.cs's IfStatement remarks.
    var unit = ParseSource("if a { x() } else if b { y() } else { z() }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var outer = unit.Statements[0] as IfStatement;
    Check("'else if' desugars to a single-element else-branch holding a nested IfStatement",
        outer?.ElseBranch is
        [
            IfStatement
            {
                Condition: IdentifierExpression { Name: "b" },
                ThenBranch: [ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "y" } } }],
                ElseBranch: [ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "z" } } }],
            },
        ]);
}
{
    var unit = ParseSource("while running { tick() }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("'while' parses correctly",
        unit.Statements is
        [
            WhileStatement
            {
                Condition: IdentifierExpression { Name: "running" },
                Body: [ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "tick" } } }],
            },
        ]);
}
{
    var unit = ParseSource("while true { if done { break } continue }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var whileStmt = unit.Statements[0] as WhileStatement;
    Check("'break' inside a nested 'if' inside a 'while' body parses correctly",
        whileStmt?.Body is
        [
            IfStatement { ThenBranch: [BreakStatement] },
            ContinueStatement,
        ]);
}
{
    // A condition itself exercises the operator precedence ladder —
    // confirms if/while conditions aren't special-cased away from the
    // full expression grammar.
    var unit = ParseSource("if a + b > c && ready { go() }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var ifStmt = unit.Statements[0] as IfStatement;
    Check("'if' condition can be a full binary-operator expression",
        ifStmt?.Condition is BinaryExpression
        {
            Operator: BinaryOperator.LogicalAnd,
            Left: BinaryExpression { Operator: BinaryOperator.Greater, Left: BinaryExpression { Operator: BinaryOperator.Add } },
            Right: IdentifierExpression { Name: "ready" },
        });
}
{
    // 'for'/'guard' remain explicitly unsupported — confirms adding
    // if/while/switch didn't accidentally widen the
    // UnsupportedStatementStarts gap or otherwise change this recovery
    // contract.
    var unit = ParseSource("for x in y {}\nprint(\"after\")", out var sink);
    Check("'for' is still reported as unsupported",
        sink.Diagnostics.Count == 1 && sink.HasErrors == false, // warning, not error
        string.Join("; ", sink.Diagnostics));
    Check("parser still recovers after an unsupported 'for'",
        unit.Statements is
        [
            UnsupportedStatement,
            ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "print" } } },
        ]);
}

// ---------------------------------------------------------------------
// 15. struct / enum / case declarations
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== struct / enum / case declarations ===");
{
    var unit = ParseSource("struct Point {\n    var x: Double\n    var y: Double\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("struct with two properties parses correctly",
        unit.Statements is
        [
            StructDeclaration
            {
                Name: "Point",
                Members:
                [
                    BindingStatement { IsMutable: true, Name: "x", DeclaredType: NamedTypeNode { Name: "Double" }, Initializer: null },
                    BindingStatement { IsMutable: true, Name: "y", DeclaredType: NamedTypeNode { Name: "Double" }, Initializer: null },
                ],
            },
        ]);
}
{
    // A struct method — proves member parsing genuinely reuses
    // ParseBlockStatements (func dispatch works inside a struct body
    // exactly like it does inside a function body), not a separate
    // struct-only member grammar. Uses a single-expression body (not a
    // mutating assignment like `x = 0`) since assignment expressions
    // aren't implemented at all yet — see Parsing/README.md.
    var unit = ParseSource("struct Point {\n    var x: Double\n    func describe() -> String {\n        \"a point\"\n    }\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var structDecl = unit.Statements[0] as StructDeclaration;
    Check("struct with a property and a method both parse correctly",
        structDecl?.Members is
        [
            BindingStatement { Name: "x" },
            FunctionDeclaration { Name: "describe", Body: [ExpressionStatement { Expression: StringLiteralExpression { Value: "a point" } }] },
        ]);
}
{
    var unit = ParseSource(
        "enum Shape {\n" +
        "    case circle(radius: Double)\n" +
        "    case rectangle(width: Double, height: Double)\n" +
        "    case triangle\n" +
        "}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var enumDecl = unit.Statements[0] as EnumDeclaration;
    Check("enum with associated-value and bare cases parses correctly",
        enumDecl?.Members is
        [
            CaseDeclaration
            {
                Name: "circle",
                AssociatedValues: [AssociatedValue { Label: "radius", Type: NamedTypeNode { Name: "Double" } }],
            },
            CaseDeclaration
            {
                Name: "rectangle",
                AssociatedValues:
                [
                    AssociatedValue { Label: "width", Type: NamedTypeNode { Name: "Double" } },
                    AssociatedValue { Label: "height", Type: NamedTypeNode { Name: "Double" } },
                ],
            },
            CaseDeclaration { Name: "triangle", AssociatedValues: [] },
        ]);
}
{
    var unit = ParseSource("struct Empty {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("empty struct body parses correctly",
        unit.Statements is [StructDeclaration { Name: "Empty", Members: [] }]);
}
{
    // Confirms struct/enum/protocol/extension didn't accidentally widen
    // the unsupported set further: for/guard/actor should all still be
    // rejected.
    var unit = ParseSource("for y in z {}\nactor Counter {}\nprint(\"after\")", out var sink);
    Check("both 'for' and 'actor' report exactly one diagnostic each",
        sink.Diagnostics.Count == 2, string.Join("; ", sink.Diagnostics));
    Check("parser still recovers to the trailing print statement",
        unit.Statements is
        [
            UnsupportedStatement,
            UnsupportedStatement,
            ExpressionStatement { Expression: CallExpression { Callee: IdentifierExpression { Name: "print" } } },
        ]);
}
{
    // Assignment expressions are now implemented — `x = 0` mutates an
    // existing binding (Target is a full Expression, not just an
    // identifier, so this naturally extends to `self.x = 0` etc. once
    // member access exists, per AssignmentStatement's remarks in Ast.cs).
    var unit = ParseSource("x = 0", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("plain assignment parses correctly",
        unit.Statements is
        [
            AssignmentStatement
            {
                Target: IdentifierExpression { Name: "x" },
                Operator: AssignmentOperator.Assign,
                Value: IntegerLiteralExpression { Value: 0 },
            },
        ]);
}
{
    (string source, AssignmentOperator expected)[] cases =
    [
        ("x += 1", AssignmentOperator.AddAssign),
        ("x -= 1", AssignmentOperator.SubtractAssign),
        ("x *= 2", AssignmentOperator.MultiplyAssign),
        ("x /= 2", AssignmentOperator.DivideAssign),
    ];
    foreach (var (source, expected) in cases)
    {
        var unit = ParseSource(source, out var sink);
        Check($"'{source}' -> {expected}, no diagnostics",
            sink.Diagnostics.Count == 0 && unit.Statements is [AssignmentStatement { Operator: var op }] && op == expected,
            string.Join("; ", sink.Diagnostics));
    }
}
{
    // A mutating assignment inside a struct method now genuinely works.
    var unit = ParseSource("struct Point {\n    var x: Double\n    func reset() {\n        x = 0\n    }\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var structDecl = unit.Statements[0] as StructDeclaration;
    Check("a mutating assignment inside a struct method now parses correctly",
        structDecl?.Members is
        [
            BindingStatement { Name: "x" },
            FunctionDeclaration { Name: "reset", Body: [AssignmentStatement { Target: IdentifierExpression { Name: "x" }, Value: IntegerLiteralExpression { Value: 0 } }] },
        ]);
}

// ---------------------------------------------------------------------
// 16. protocol / extension declarations
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== protocol / extension declarations ===");
{
    // grammar.md Section 5's own protocol example: a bodyless func
    // requirement — proves FunctionDeclaration.Body is genuinely null
    // (not an empty list) for a requirement with no implementation.
    var unit = ParseSource("protocol Drawable {\n    func draw() -> String\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("protocol with a bodyless requirement parses, Body is genuinely null",
        unit.Statements is
        [
            ProtocolDeclaration
            {
                Name: "Drawable",
                InheritedProtocols: [],
                Members: [FunctionDeclaration { Name: "draw", ReturnType: NamedTypeNode { Name: "String" }, Body: null }],
            },
        ]);
}
{
    // grammar.md Section 5's own extension example: conformance clause +
    // a real single-expression-body implementation (via implicit
    // return, ADR-0005).
    var unit = ParseSource("extension Point: Drawable {\n    func draw() -> String {\n        \"a point\"\n    }\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("extension with conformance clause and a real implementation parses correctly",
        unit.Statements is
        [
            ExtensionDeclaration
            {
                ExtendedType: "Point",
                ConformedProtocols: ["Drawable"],
                Members: [FunctionDeclaration { Name: "draw", Body: [ExpressionStatement { Expression: StringLiteralExpression { Value: "a point" } }] }],
            },
        ]);
}
{
    // Multiple comma-separated conformances, and confirms struct/enum's
    // previously-unparsed conformance clause now works too (bonus fix
    // that fell out of building the shared ParseConformanceClause).
    var unit = ParseSource("struct Point: Drawable, Equatable {\n    var x: Double\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("struct with a multi-protocol conformance clause parses correctly",
        unit.Statements is [StructDeclaration { Name: "Point", ConformedProtocols: ["Drawable", "Equatable"] }]);
}
{
    var unit = ParseSource("protocol P2: P1 {\n    func f()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("protocol inheritance clause ('protocol P2: P1') parses correctly",
        unit.Statements is [ProtocolDeclaration { Name: "P2", InheritedProtocols: ["P1"] }]);
}
{
    // A protocol requirement with parameters and no explicit newline
    // before the next member — confirms ExpectStatementTerminator's
    // handling of a bodyless func ending at a newline (not a '}').
    var unit = ParseSource("protocol Greeter {\n    func greet(name: String) -> String\n    func farewell(name: String) -> String\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var protocolDecl = unit.Statements[0] as ProtocolDeclaration;
    Check("two consecutive bodyless requirements both parse correctly",
        protocolDecl?.Members is
        [
            FunctionDeclaration { Name: "greet", Body: null },
            FunctionDeclaration { Name: "farewell", Body: null },
        ]);
}

// ---------------------------------------------------------------------
// 17. Generics: <T>, <T: Protocol>, where clauses, parameter labels
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Generics ===");
{
    // Swift's real identity function signature — the simplest possible
    // generic function, no constraints.
    var unit = ParseSource("func identity<T>(value: T) -> T {\n    value\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("bare '<T>' generic parameter parses correctly",
        unit.Statements is
        [
            FunctionDeclaration
            {
                Name: "identity",
                GenericParameters: [TypeConstraint { TypeName: "T", ConformedProtocols: [] }],
                Parameters: [Parameter { Name: "value", Type: NamedTypeNode { Name: "T" } }],
                ReturnType: NamedTypeNode { Name: "T" },
            },
        ]);
}
{
    // '_ value: T' — suppressed external label, exactly as in
    // type-system.md's own firstMatch/merge examples.
    var unit = ParseSource("func identity<T>(_ value: T) -> T {\n    value\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'_' suppresses the external label (ExternalLabel is null)",
        fn?.Parameters is [Parameter { ExternalLabel: null, Name: "value" }]);
}
{
    // Explicit distinct external/internal labels — the general case the
    // same lookahead logic also happens to support.
    var unit = ParseSource("func move(to destination: Int) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("distinct external/internal labels ('to destination') parse correctly",
        fn?.Parameters is [Parameter { ExternalLabel: "to", Name: "destination" }]);
}
{
    // A plain 'name: Type' parameter still defaults ExternalLabel to the
    // same value as Name (Swift's implicit default) — confirms the
    // generics work didn't regress the non-generic parameter case.
    var unit = ParseSource("func add(a: Int, b: Int) -> Int {\n    a + b\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("plain 'a: Int' still defaults ExternalLabel to 'a'",
        fn?.Parameters is [Parameter { ExternalLabel: "a", Name: "a" }, Parameter { ExternalLabel: "b", Name: "b" }]);
}
{
    // Inline constraint: <T: Equatable>
    var unit = ParseSource("func f<T: Equatable>(a: T, b: T) -> Bool {\n    a == b\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("inline '<T: Equatable>' constraint parses correctly",
        fn?.GenericParameters is [TypeConstraint { TypeName: "T", ConformedProtocols: ["Equatable"] }]);
}
{
    // Protocol composition via '&': <T: Drawable & Equatable>
    var unit = ParseSource("func f<T: Drawable & Equatable>(a: T) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'&'-composed inline constraint parses correctly",
        fn?.GenericParameters is [TypeConstraint { TypeName: "T", ConformedProtocols: ["Drawable", "Equatable"] }]);
}
{
    // type-system.md's own merge<T> example: a trailing where clause.
    var unit = ParseSource("func merge<T>(a: T, b: T) -> T where T: Equatable {\n    a\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("trailing 'where T: Equatable' clause parses correctly",
        fn?.WhereConstraints is [TypeConstraint { TypeName: "T", ConformedProtocols: ["Equatable"] }]);
}
{
    // Multiple generic parameters, comma-separated.
    var unit = ParseSource("func pair<K, V>(key: K, value: V) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("multiple comma-separated generic parameters parse correctly",
        fn?.GenericParameters is
        [
            TypeConstraint { TypeName: "K", ConformedProtocols: [] },
            TypeConstraint { TypeName: "V", ConformedProtocols: [] },
        ]);
}
{
    // type-system.md's own Stack<T>: Container example — generic type
    // declaration with a conformance clause.
    var unit = ParseSource("struct Stack<T>: Container {\n    var count: Int\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("generic struct with a conformance clause parses correctly",
        unit.Statements is
        [
            StructDeclaration
            {
                Name: "Stack",
                GenericParameters: [TypeConstraint { TypeName: "T" }],
                ConformedProtocols: ["Container"],
            },
        ]);
}
{
    // extension Array where Element: Equatable — a where clause with no
    // '<...>' list of its own, constraining an already-generic type.
    var unit = ParseSource("extension Array where Element: Equatable {\n    func f() {}\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("extension where-clause with no generic parameter list parses correctly",
        unit.Statements is
        [
            ExtensionDeclaration
            {
                ExtendedType: "Array",
                GenericParameters: [],
                WhereConstraints: [TypeConstraint { TypeName: "Element", ConformedProtocols: ["Equatable"] }],
            },
        ]);
}
{
    // Confirms generics didn't break '<'/'>' as ordinary comparison
    // operators in expression position — no ambiguity in practice since
    // generic parsing only triggers at declaration call sites.
    var unit = ParseSource("if a < b && c > d { go() }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("'<' and '>' still parse as comparison operators in expression position",
        unit.Statements is
        [
            IfStatement
            {
                Condition: BinaryExpression
                {
                    Operator: BinaryOperator.LogicalAnd,
                    Left: BinaryExpression { Operator: BinaryOperator.Less },
                    Right: BinaryExpression { Operator: BinaryOperator.Greater },
                },
            },
        ]);
}

// ---------------------------------------------------------------------
// 18. Member access ('.'), subscripting ('[]'), and 'self'
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Member access, subscripting, self ===");
{
    var unit = ParseSource("f(point.x)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'point.x' parses as a MemberAccessExpression",
        call?.Arguments is [MemberAccessExpression { MemberName: "x", Target: IdentifierExpression { Name: "point" } }]);
}
{
    var unit = ParseSource("f(a.b.c)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("chained member access 'a.b.c' nests correctly (outer target is itself a MemberAccessExpression)",
        call?.Arguments is
        [
            MemberAccessExpression
            {
                MemberName: "c",
                Target: MemberAccessExpression { MemberName: "b", Target: IdentifierExpression { Name: "a" } },
            },
        ]);
}
{
    var unit = ParseSource("f(items[0])", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("'items[0]' parses as a SubscriptExpression",
        call?.Arguments is
        [
            SubscriptExpression
            {
                Target: IdentifierExpression { Name: "items" },
                Arguments: [IntegerLiteralExpression { Value: 0 }],
            },
        ]);
}
{
    // Multiple comma-separated subscript arguments, e.g. matrix[row, col].
    var unit = ParseSource("f(matrix[row, col])", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("multi-argument subscript 'matrix[row, col]' parses correctly",
        call?.Arguments is
        [
            SubscriptExpression
            {
                Target: IdentifierExpression { Name: "matrix" },
                Arguments: [IdentifierExpression { Name: "row" }, IdentifierExpression { Name: "col" }],
            },
        ]);
}
{
    // Interleaved postfix forms: a.b[0].c() — member access, subscript,
    // member access, and a call, all chaining correctly in sequence.
    var unit = ParseSource("a.b[0].c()", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var expr = ((ExpressionStatement)unit.Statements[0]).Expression;
    Check("interleaved '.'/'[]'/'()' postfix forms all chain correctly",
        expr is CallExpression
        {
            Callee: MemberAccessExpression
            {
                MemberName: "c",
                Target: SubscriptExpression
                {
                    Arguments: [IntegerLiteralExpression { Value: 0 }],
                    Target: MemberAccessExpression { MemberName: "b", Target: IdentifierExpression { Name: "a" } },
                },
            },
        });
}
{
    var unit = ParseSource("f(self)", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("bare 'self' parses as SelfExpression", call?.Arguments is [SelfExpression]);
}
{
    // The actual real-world case this milestone exists for: a struct
    // method genuinely mutating its own state via 'self.x = 0', not the
    // bare-identifier 'x = 0' workaround used before member access existed.
    var unit = ParseSource("struct Point {\n    var x: Double\n    func reset() {\n        self.x = 0\n    }\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var structDecl = unit.Statements[0] as StructDeclaration;
    Check("'self.x = 0' inside a struct method parses correctly",
        structDecl?.Members is
        [
            BindingStatement { Name: "x" },
            FunctionDeclaration
            {
                Name: "reset",
                Body:
                [
                    AssignmentStatement
                    {
                        Target: MemberAccessExpression { MemberName: "x", Target: SelfExpression },
                        Value: IntegerLiteralExpression { Value: 0 },
                    },
                ],
            },
        ]);
}
{
    // Subscript assignment: items[0] = 1.
    var unit = ParseSource("items[0] = 1", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("'items[0] = 1' (subscript assignment target) parses correctly",
        unit.Statements is
        [
            AssignmentStatement
            {
                Target: SubscriptExpression { Target: IdentifierExpression { Name: "items" }, Arguments: [IntegerLiteralExpression { Value: 0 }] },
                Value: IntegerLiteralExpression { Value: 1 },
            },
        ]);
}
{
    // Confirms member access participates correctly in the full
    // precedence ladder, not just isolated as a call argument.
    var unit = ParseSource("if point.x > 0 && point.y > 0 { go() }", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var ifStmt = unit.Statements[0] as IfStatement;
    Check("member access inside a full binary-operator condition parses correctly",
        ifStmt?.Condition is BinaryExpression
        {
            Operator: BinaryOperator.LogicalAnd,
            Left: BinaryExpression { Left: MemberAccessExpression { MemberName: "x" } },
            Right: BinaryExpression { Left: MemberAccessExpression { MemberName: "y" } },
        });
}

// ---------------------------------------------------------------------
// 19. Richer type syntax: array sugar, function types, any/some, Self,
//     generic type arguments at use sites
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Richer type syntax ===");
{
    var unit = ParseSource("func f(items: [Int]) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("array sugar '[Int]' parses as ArrayTypeNode",
        fn?.Parameters is [Parameter { Type: ArrayTypeNode { ElementType: NamedTypeNode { Name: "Int" } } }]);
}
{
    var unit = ParseSource("func f(items: [Int]?) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("optional array sugar '[Int]?' parses correctly (IsOptional on the ArrayTypeNode itself)",
        fn?.Parameters is [Parameter { Type: ArrayTypeNode { ElementType: NamedTypeNode { Name: "Int" }, IsOptional: true } }]);
}
{
    var unit = ParseSource("func f(predicate: (Int) -> Bool) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("function type '(Int) -> Bool' parses as FunctionTypeNode",
        fn?.Parameters is
        [
            Parameter
            {
                Type: FunctionTypeNode
                {
                    ParameterTypes: [NamedTypeNode { Name: "Int" }],
                    ReturnType: NamedTypeNode { Name: "Bool" },
                },
            },
        ]);
}
{
    // Zero-parameter function type — a real, valid Swift-style form.
    var unit = ParseSource("func f(action: () -> Void) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("zero-parameter function type '() -> Void' parses correctly",
        fn?.Parameters is [Parameter { Type: FunctionTypeNode { ParameterTypes: [], ReturnType: NamedTypeNode { Name: "Void" } } }]);
}
{
    // type-system.md's own firstMatch<T> example, now fully parseable
    // end-to-end for the first time — both the [T] array sugar and the
    // (T) -> Bool function type were the exact gap flagged when generics
    // first landed.
    var unit = ParseSource(
        "func firstMatch<T>(_ items: [T], predicate: (T) -> Bool) -> T? where T: Equatable {\n" +
        "    items[0]\n" +
        "}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("type-system.md's firstMatch<T> example now fully parses end-to-end",
        fn is
        {
            Name: "firstMatch",
            GenericParameters: [TypeConstraint { TypeName: "T" }],
            Parameters:
            [
                Parameter { ExternalLabel: null, Name: "items", Type: ArrayTypeNode { ElementType: NamedTypeNode { Name: "T" } } },
                Parameter { Name: "predicate", Type: FunctionTypeNode { ParameterTypes: [NamedTypeNode { Name: "T" }], ReturnType: NamedTypeNode { Name: "Bool" } } },
            ],
            ReturnType: NamedTypeNode { Name: "T", IsOptional: true },
            WhereConstraints: [TypeConstraint { TypeName: "T", ConformedProtocols: ["Equatable"] }],
        });
}
{
    // Generic type arguments at a use site: Stack<Int>.
    var unit = ParseSource("func f(s: Stack<Int>) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'Stack<Int>' generic type argument at a use site parses correctly",
        fn?.Parameters is [Parameter { Type: NamedTypeNode { Name: "Stack", GenericArguments: [NamedTypeNode { Name: "Int" }] } }]);
}
{
    // any P — existential.
    var unit = ParseSource("func render(shapes: [any Drawable]) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'[any Drawable]' (existential inside array sugar) parses correctly",
        fn?.Parameters is [Parameter { Type: ArrayTypeNode { ElementType: ExistentialTypeNode { Protocols: ["Drawable"] } } }]);
}
{
    // any P & Q — existential with protocol composition.
    var unit = ParseSource("func describe(item: any Drawable & Comparable) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'any Drawable & Comparable' protocol composition parses correctly",
        fn?.Parameters is [Parameter { Type: ExistentialTypeNode { Protocols: ["Drawable", "Comparable"] } }]);
}
{
    // some P — opaque return type.
    var unit = ParseSource("func makeStack() -> some Container {\n    x\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'-> some Container' opaque return type parses correctly",
        fn?.ReturnType is OpaqueTypeNode { Protocols: ["Container"] });
}
{
    // Self as a return type — the protocol-requirement use case.
    var unit = ParseSource("protocol Cloneable {\n    func clone() -> Self\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var protocolDecl = unit.Statements[0] as ProtocolDeclaration;
    Check("'Self' return type on a protocol requirement parses correctly",
        protocolDecl?.Members is [FunctionDeclaration { Name: "clone", ReturnType: SelfTypeNode, Body: null }]);
}
{
    // Nested richer types: an array of functions returning optionals.
    var unit = ParseSource("func f(handlers: [(Int) -> String?]) {}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("nested richer types ('[(Int) -> String?]') parse correctly",
        fn?.Parameters is
        [
            Parameter
            {
                Type: ArrayTypeNode
                {
                    ElementType: FunctionTypeNode
                    {
                        ParameterTypes: [NamedTypeNode { Name: "Int" }],
                        ReturnType: NamedTypeNode { Name: "String", IsOptional: true },
                    },
                },
            },
        ]);
}
{
    // Confirms the AST dump renders richer types in a readable,
    // source-like compact form rather than deep tree nesting.
    var unit = ParseSource("func f(items: [Int], g: (Int) -> Bool) {}", out var sink);
    var dump = AstPrinter.Print(unit);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("AST dump renders array/function types compactly",
        dump.Contains("[Int]") && dump.Contains("(Int) -> Bool"));
}

// ---------------------------------------------------------------------
// 20. String interpolation
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== String interpolation ===");
{
    // grammar.md Section 1's own example: two interpolations in one string.
    var unit = ParseSource("print(\"Hello, \\(name)! You are \\(age) years old.\")", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("grammar.md's own two-interpolation example parses correctly",
        call?.Arguments is
        [
            InterpolatedStringExpression
            {
                Segments:
                [
                    InterpolatedStringTextSegment { Text: "Hello, " },
                    InterpolatedStringExpressionSegment { Expression: IdentifierExpression { Name: "name" } },
                    InterpolatedStringTextSegment { Text: "! You are " },
                    InterpolatedStringExpressionSegment { Expression: IdentifierExpression { Name: "age" } },
                    InterpolatedStringTextSegment { Text: " years old." },
                ],
            },
        ]);
}
{
    // A nested call inside the interpolation — the specific case the
    // lexer's paren-depth tracking exists for (Lexing/Lexer.cs), now
    // exercised through the parser too: the inner ')' from f(x, y) must
    // not be mistaken for the interpolation's own closing ')'.
    var unit = ParseSource("print(\"Result: \\(f(x, y))!\")", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("nested call expression inside an interpolation parses correctly",
        call?.Arguments is
        [
            InterpolatedStringExpression
            {
                Segments:
                [
                    InterpolatedStringTextSegment { Text: "Result: " },
                    InterpolatedStringExpressionSegment
                    {
                        Expression: CallExpression
                        {
                            Callee: IdentifierExpression { Name: "f" },
                            Arguments: [IdentifierExpression { Name: "x" }, IdentifierExpression { Name: "y" }],
                        },
                    },
                    InterpolatedStringTextSegment { Text: "!" },
                ],
            },
        ]);
}
{
    // Two adjacent interpolations with no text between them — exercises
    // the empty-text-segment case (InterpolationStringMiddle with "").
    var unit = ParseSource("print(\"\\(a)\\(b)\")", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("adjacent interpolations with empty text segments in between parse correctly",
        call?.Arguments is
        [
            InterpolatedStringExpression
            {
                Segments:
                [
                    InterpolatedStringTextSegment { Text: "" },
                    InterpolatedStringExpressionSegment { Expression: IdentifierExpression { Name: "a" } },
                    InterpolatedStringTextSegment { Text: "" },
                    InterpolatedStringExpressionSegment { Expression: IdentifierExpression { Name: "b" } },
                    InterpolatedStringTextSegment { Text: "" },
                ],
            },
        ]);
}
{
    // A full binary expression embedded in the interpolation, not just
    // a bare identifier — confirms the parser calls ordinary
    // ParseExpression (full precedence ladder) for the embedded part,
    // not some restricted sub-grammar.
    var unit = ParseSource("print(\"Total: \\(a + b * c)\")", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("a full binary expression embeds correctly inside an interpolation",
        call?.Arguments is
        [
            InterpolatedStringExpression
            {
                Segments:
                [
                    InterpolatedStringTextSegment { Text: "Total: " },
                    InterpolatedStringExpressionSegment
                    {
                        Expression: BinaryExpression
                        {
                            Operator: BinaryOperator.Add,
                            Right: BinaryExpression { Operator: BinaryOperator.Multiply },
                        },
                    },
                    InterpolatedStringTextSegment { Text: "" },
                ],
            },
        ]);
}
{
    // Member access inside an interpolation — self.x-style, the
    // realistic case a Description-style method would actually use.
    var unit = ParseSource("func describe() -> String {\n    \"Point(\\(self.x), \\(self.y))\"\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("grammar.md Section 5's own 'Point(\\(x), \\(y))'-style dump interpolation parses correctly",
        fn?.Body is
        [
            ExpressionStatement
            {
                Expression: InterpolatedStringExpression
                {
                    Segments:
                    [
                        InterpolatedStringTextSegment { Text: "Point(" },
                        InterpolatedStringExpressionSegment { Expression: MemberAccessExpression { MemberName: "x", Target: SelfExpression } },
                        InterpolatedStringTextSegment { Text: ", " },
                        InterpolatedStringExpressionSegment { Expression: MemberAccessExpression { MemberName: "y", Target: SelfExpression } },
                        InterpolatedStringTextSegment { Text: ")" },
                    ],
                },
            },
        ]);
}
{
    // A plain, non-interpolated string is completely unaffected —
    // confirms StringLiteralExpression and InterpolatedStringExpression
    // are cleanly distinguished by the lexer with no parser-side
    // ambiguity between the two.
    var unit = ParseSource("print(\"just plain text\")", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("a plain string with no interpolation still parses as StringLiteralExpression",
        call?.Arguments is [StringLiteralExpression { Value: "just plain text" }]);
}

// ---------------------------------------------------------------------
// 21. switch statements and case patterns
// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== switch statements and case patterns ===");
{
    // Real bug found while writing the nested-pattern test below:
    // type-system.md's own canonical Optional<T> definition (Section 3)
    // uses `case some(T)` / `case none` as real case names — but `some`
    // lexes as the KwSome keyword (for opaque types `some P`), not
    // Identifier, so this failed to parse via ParseCaseDeclaration's
    // plain Expect(Identifier) before ExpectIdentifierLike existed.
    // This is type-system.md's actual Optional<T> body, verbatim.
    var unit = ParseSource("enum Optional<T> {\n    case some(T)\n    case none\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var enumDecl = unit.Statements[0] as EnumDeclaration;
    Check("type-system.md's own Optional<T> definition ('case some(T)') now parses correctly",
        enumDecl?.Members is
        [
            CaseDeclaration { Name: "some", AssociatedValues: [AssociatedValue { Label: null, Type: NamedTypeNode { Name: "T" } }] },
            CaseDeclaration { Name: "none", AssociatedValues: [] },
        ]);
}
{
    // Same collision, at the pattern-matching call site: '.some(...)'
    // and '.none' as patterns, matching against a real Optional value.
    var unit = ParseSource("switch value {\ncase .some(let x):\n    use(x)\ncase .none:\n    skip()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("'.some(let x)' / '.none' patterns (matching real Optional case names) parse correctly",
        switchStmt?.Cases is
        [
            SwitchCase { Patterns: [EnumCasePattern { CaseName: "some", AssociatedValues: [BindingPattern { Name: "x" }] }] },
            SwitchCase { Patterns: [EnumCasePattern { CaseName: "none", AssociatedValues: [] }] },
        ]);
}
{
    // grammar.md Section 6's own switch example, verbatim: enum-case
    // patterns with associated-value bindings, plus a default clause.
    var unit = ParseSource(
        "switch shape {\n" +
        "case .circle(let radius):\n" +
        "    describe()\n" +
        "case .rectangle(let w, let h):\n" +
        "    describe()\n" +
        "default:\n" +
        "    describe()\n" +
        "}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("grammar.md's own switch example parses correctly",
        unit.Statements is
        [
            SwitchStatement
            {
                Subject: IdentifierExpression { Name: "shape" },
                Cases:
                [
                    SwitchCase
                    {
                        Patterns: [EnumCasePattern { CaseName: "circle", AssociatedValues: [BindingPattern { Name: "radius" }] }],
                        Guard: null,
                    },
                    SwitchCase
                    {
                        Patterns:
                        [
                            EnumCasePattern
                            {
                                CaseName: "rectangle",
                                AssociatedValues: [BindingPattern { Name: "w" }, BindingPattern { Name: "h" }],
                            },
                        ],
                    },
                ],
                DefaultBody: [ExpressionStatement],
            },
        ]);
}
{
    // A bare enum case with no associated values.
    var unit = ParseSource("switch x {\ncase .none:\n    f()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("bare enum case pattern (no associated values) parses correctly",
        switchStmt?.Cases is [SwitchCase { Patterns: [EnumCasePattern { CaseName: "none", AssociatedValues: [] }] }]);
}
{
    // Multiple comma-separated value patterns on one case, plus the
    // wildcard pattern.
    var unit = ParseSource("switch x {\ncase 1, 2:\n    a()\ncase _:\n    b()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("comma-separated value patterns and wildcard pattern parse correctly",
        switchStmt?.Cases is
        [
            SwitchCase { Patterns: [ExpressionPattern { Expression: IntegerLiteralExpression { Value: 1 } }, ExpressionPattern { Expression: IntegerLiteralExpression { Value: 2 } }] },
            SwitchCase { Patterns: [WildcardPattern] },
        ]);
}
{
    // A 'where' guard clause on a case.
    var unit = ParseSource("switch x {\ncase let n where n > 0:\n    positive()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("'where' guard clause on a case parses correctly",
        switchStmt?.Cases is
        [
            SwitchCase
            {
                Patterns: [BindingPattern { Name: "n" }],
                Guard: BinaryExpression { Operator: BinaryOperator.Greater, Left: IdentifierExpression { Name: "n" } },
            },
        ]);
}
{
    // Nested pattern: an enum case pattern as an associated-value slot
    // of another enum case pattern — falls out for free from ParsePattern
    // being used recursively for associated-value slots.
    var unit = ParseSource("switch x {\ncase .some(.circle(let radius)):\n    f()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("nested enum-case pattern ('.some(.circle(let radius))') parses correctly",
        switchStmt?.Cases is
        [
            SwitchCase
            {
                Patterns:
                [
                    EnumCasePattern
                    {
                        CaseName: "some",
                        AssociatedValues: [EnumCasePattern { CaseName: "circle", AssociatedValues: [BindingPattern { Name: "radius" }] }],
                    },
                ],
            },
        ]);
}
{
    // A switch with no default clause at all — the parser doesn't
    // enforce exhaustiveness (that's Semantics/'s job for enum subjects).
    var unit = ParseSource("switch x {\ncase 1:\n    a()\n}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("switch with no default clause has a null DefaultBody, not an empty list",
        switchStmt?.DefaultBody is null);
}
{
    // A case body with more than one statement — confirms ParseCaseBody
    // genuinely stops at the next 'case'/'default'/'}' rather than
    // needing brace delimiters, and doesn't consume neighboring cases.
    var unit = ParseSource(
        "switch x {\n" +
        "case 1:\n" +
        "    let a = 1\n" +
        "    print(\"one\")\n" +
        "case 2:\n" +
        "    print(\"two\")\n" +
        "}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = unit.Statements[0] as SwitchStatement;
    Check("multi-statement case body parses correctly and doesn't bleed into the next case",
        switchStmt?.Cases is
        [
            SwitchCase
            {
                Body:
                [
                    BindingStatement { Name: "a" },
                    ExpressionStatement { Expression: CallExpression { Arguments: [StringLiteralExpression { Value: "one" }] } },
                ],
            },
            SwitchCase
            {
                Body: [ExpressionStatement { Expression: CallExpression { Arguments: [StringLiteralExpression { Value: "two" }] } }],
            },
        ]);
}
{
    // switch as a statement inside a function body — confirms it
    // composes with everything else via ParseBlockStatements, the same
    // way if/while do.
    var unit = ParseSource(
        "func describe(shape: Shape) -> String {\n" +
        "    switch shape {\n" +
        "    case .circle(let radius):\n" +
        "        return \"circle\"\n" +
        "    default:\n" +
        "        return \"other\"\n" +
        "    }\n" +
        "}", out var sink);
    Check("no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var fn = unit.Statements[0] as FunctionDeclaration;
    Check("'switch' composes correctly as a statement inside a function body",
        fn?.Body is [SwitchStatement { Cases: [SwitchCase { Body: [ReturnStatement] }], DefaultBody: [ReturnStatement] }]);
}

// ---------------------------------------------------------------------
// Semantics/ — ADR-0011 minimal pipeline scope
// ---------------------------------------------------------------------

BoundCompilationUnit BindSource(string source, out InMemoryDiagnosticSink sink)
{
    var unit = ParseSource(source, out var parseSink);
    Check("no parse diagnostics for semantics test input", parseSink.Diagnostics.Count == 0, string.Join("; ", parseSink.Diagnostics));
    sink = new InMemoryDiagnosticSink();
    return SemanticAnalyzer.Analyze(unit, sink);
}

{
    // hello.voy itself — the actual sample this whole minimal pipeline
    // exists to eventually run. Confirms the built-in `print` signature
    // and top-level statement binding both work together.
    var bound = BindSource("print(\"Hello, Voyage.\")", out var sink);
    Check("hello.voy: no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("hello.voy: binds to one top-level call statement",
        bound.TopLevelStatements is [BoundExpressionStatement { Expression: BoundCall { Function.Name: "print" } }]);
}

{
    // A function calling another function declared *later* in the file
    // — the concrete forward-reference case DeclarationBinder's two-pass
    // split exists for.
    var bound = BindSource(
        "func a() -> Int { return b() }\n" +
        "func b() -> Int { return 42 }", out var sink);
    Check("forward reference: no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("forward reference: both functions bound", bound.Functions.Count == 2);
}

{
    // Arithmetic type-checking: matching numeric types succeed, and a
    // real type mismatch (Int + Bool) is caught with ErrorType rather
    // than silently accepted or crashing.
    var bound = BindSource("func add(a: Int, b: Int) -> Int { return a + b }", out var sink);
    Check("arithmetic: well-typed function has no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var body = bound.Functions[0].Body;
    Check("arithmetic: 'a + b' binds with Int result type",
        body is [BoundReturn { Value: BoundBinary { Operator: BinaryOperator.Add, Type: PrimitiveType { Name: "Int" } } }]);
}

{
    var unit = ParseSource("func bad() -> Int { return 1 + true }", out var parseSink);
    Check("type mismatch: parses fine (Semantics/'s job to catch it)", parseSink.Diagnostics.Count == 0);
    var sink = new InMemoryDiagnosticSink();
    SemanticAnalyzer.Analyze(unit, sink);
    Check("type mismatch: 'Int + Bool' is reported as a diagnostic", sink.Diagnostics.Count > 0);
}

{
    // let/var, if/while, break/continue — the imperative core of the
    // minimal subset, all composing inside one function body.
    var bound = BindSource(
        "func countdown(n: Int) -> Int {\n" +
        "    var i = n\n" +
        "    while i > 0 {\n" +
        "        if i == 5 {\n" +
        "            break\n" +
        "        }\n" +
        "        i = i - 1\n" +
        "    }\n" +
        "    return i\n" +
        "}", out var sink);
    Check("imperative core: no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
}

{
    // break/continue outside a loop is caught, not silently accepted.
    var unit = ParseSource("func f() { break }", out var parseSink);
    var sink = new InMemoryDiagnosticSink();
    SemanticAnalyzer.Analyze(unit, sink);
    Check("'break' outside a loop is diagnosed", sink.Diagnostics.Count > 0);
}

{
    // Struct construction (implicit positional memberwise init) and
    // stored-property read via member access.
    var bound = BindSource(
        "struct Point {\n" +
        "    var x: Int\n" +
        "    var y: Int\n" +
        "}\n" +
        "func magnitude(p: Point) -> Int {\n" +
        "    return p.x + p.y\n" +
        "}\n" +
        "let origin = Point(0, 0)", out var sink);
    Check("struct: no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    Check("struct: construction binds as BoundStructConstruction",
        bound.TopLevelStatements is [BoundVariableDeclaration { Initializer: BoundStructConstruction { Struct.Name: "Point" } }]);
    Check("struct: property access binds to the right PropertySymbol",
        bound.Functions[0].Body is [BoundReturn { Value: BoundBinary { Left: BoundPropertyAccess { Property.Name: "x" } } }]);
}

{
    // Wrong arity/type on struct construction is caught.
    var unit = ParseSource(
        "struct Point { var x: Int\n var y: Int }\n" +
        "let bad = Point(1)", out var parseSink);
    var sink = new InMemoryDiagnosticSink();
    SemanticAnalyzer.Analyze(unit, sink);
    Check("struct: wrong argument count is diagnosed", sink.Diagnostics.Count > 0);
}

{
    // enum + switch/pattern matching, including a binding pattern
    // extracting an associated value.
    var bound = BindSource(
        "enum Shape {\n" +
        "    case circle(radius: Double)\n" +
        "    case square(side: Double)\n" +
        "}\n" +
        "func area(shape: Shape) -> Double {\n" +
        "    switch shape {\n" +
        "    case .circle(let radius):\n" +
        "        return radius\n" +
        "    case .square(let side):\n" +
        "        return side\n" +
        "    }\n" +
        "}", out var sink);
    Check("enum/switch: no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
    var switchStmt = bound.Functions[0].Body[0] as BoundSwitch;
    Check("enum/switch: subject binds with EnumType",
        switchStmt?.Subject.Type is EnumType { Symbol.Name: "Shape" });
    Check("enum/switch: case pattern resolves to the right EnumCaseSymbol",
        switchStmt?.Cases[0].Patterns[0] is BoundEnumCasePattern { Case.Name: "circle" });
}

{
    // Unknown enum case in a pattern is caught rather than silently
    // matching nothing.
    var unit = ParseSource(
        "enum Shape { case circle(radius: Double) }\n" +
        "func f(s: Shape) -> Double {\n" +
        "    switch s {\n" +
        "    case .triangle(let x):\n" +
        "        return x\n" +
        "    }\n" +
        "}", out var parseSink);
    var sink = new InMemoryDiagnosticSink();
    SemanticAnalyzer.Analyze(unit, sink);
    Check("unknown enum case in pattern is diagnosed", sink.Diagnostics.Count > 0);
}

{
    // String interpolation type-checks its embedded expressions.
    var bound = BindSource(
        "func greet(name: String) -> String {\n" +
        "    return \"Hello, \\(name)!\"\n" +
        "}", out var sink);
    Check("interpolation: no diagnostics", sink.Diagnostics.Count == 0, string.Join("; ", sink.Diagnostics));
}

{
    // Undeclared name is caught with a clear diagnostic, not a crash.
    var unit = ParseSource("func f() -> Int { return undeclaredName }", out var parseSink);
    var sink = new InMemoryDiagnosticSink();
    SemanticAnalyzer.Analyze(unit, sink);
    Check("undeclared identifier is diagnosed", sink.Diagnostics.Count > 0);
}

{
    // Deferred-feature diagnostics: a bare `for`-in (UnsupportedStatement
    // from Parsing/) is reported by Semantics/ too, rather than
    // Semantics/ crashing on a node kind it's never seen.
    var unit = ParseSource("func f() { for x in y { } }", out var parseSink);
    var sink = new InMemoryDiagnosticSink();
    SemanticAnalyzer.Analyze(unit, sink);
    Check("an unparseable construct reaching Semantics/ is diagnosed, not a crash", sink.Diagnostics.Count > 0);
}

// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"=== {passes} passed, {failures} failed ===");
return failures == 0 ? 0 : 1;
