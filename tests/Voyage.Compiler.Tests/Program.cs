using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Lexing;
using Voyage.Compiler.Parsing;

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
    // `struct` is real, valid voyage-lang (grammar.md Section 4) but
    // this milestone's parser doesn't implement it yet (`let`/`var`
    // bindings and `func` declarations are now supported — see the
    // sections below — but `struct` and other type declarations
    // aren't). It should report a diagnostic and produce an
    // UnsupportedStatement rather than crashing or silently losing the
    // rest of the file.
    var unit = ParseSource("struct Point {}\nprint(\"after\")", out var sink);
    Check("exactly one diagnostic reported (the 'struct' warning)",
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
    // String interpolation is real, valid voyage-lang (grammar.md
    // Section 10) but explicitly out of scope for this parser
    // milestone too — same graceful-degradation contract.
    var unit = ParseSource("print(\"Hello, \\(name)!\")", out var sink);
    Check("interpolation reports exactly one diagnostic",
        sink.Diagnostics.Count == 1, string.Join("; ", sink.Diagnostics));
    var call = ((ExpressionStatement)unit.Statements[0]).Expression as CallExpression;
    Check("interpolated argument becomes an ErrorExpression, not a crash",
        call?.Arguments is [ErrorExpression]);
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
    // Type annotations aren't implemented yet — should warn, ignore the
    // annotation, and still produce a correct binding from the initializer.
    var unit = ParseSource("let z: Int = 30", out var sink);
    Check("exactly one warning about the unsupported type annotation",
        sink.Diagnostics.Count == 1, string.Join("; ", sink.Diagnostics));
    Check("annotation ignored, binding still parses correctly",
        unit.Statements is [BindingStatement { IsMutable: false, Name: "z", Initializer: IntegerLiteralExpression { Value: 30 } }]);
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
                    Parameter { Name: "a", Type: TypeNode { Name: "Int", IsOptional: false } },
                    Parameter { Name: "b", Type: TypeNode { Name: "Int", IsOptional: false } },
                ],
                ReturnType: TypeNode { Name: "Int", IsOptional: false },
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
                Parameters: [Parameter { Name: "name", Type: TypeNode { Name: "String", IsOptional: false } }],
                ReturnType: TypeNode { Name: "String", IsOptional: true },
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
                Body: [FunctionDeclaration { Name: "inner", ReturnType: TypeNode { Name: "Int" } }],
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
    Check("AST dump mentions both Parameter nodes", dump.Contains("Parameter 'a'") && dump.Contains("Parameter 'b'"));
    Check("AST dump mentions the Int TypeNodes", dump.Contains("TypeNode 'Int'"));
    Check("AST dump mentions ReturnStatement", dump.Contains("ReturnStatement"));
}

// ---------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"=== {passes} passed, {failures} failed ===");
return failures == 0 ? 0 : 1;
