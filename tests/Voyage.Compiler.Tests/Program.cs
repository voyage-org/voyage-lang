using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Lexing;

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
Console.WriteLine();
Console.WriteLine($"=== {passes} passed, {failures} failed ===");
return failures == 0 ? 0 : 1;
