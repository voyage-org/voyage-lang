# Parsing/

Hand-written recursive descent (ADR-0010 — no parser generator).
Converts the token stream from `Lexing/` into an AST.

`samples/hello.voy` (`print("Hello, Voyage.")`) parses to:

```
CompilationUnit @ 1:1-2:1
  ExpressionStatement @ 1:1-2:1
    CallExpression @ 1:1-1:24
      callee:
        IdentifierExpression 'print' @ 1:1-1:6
      arguments:
        StringLiteralExpression "Hello, Voyage." @ 1:7-1:23
```

## Current scope

Implemented:
- Top-level expression statements
- `let`/`var` binding statements with an initializer (`let x = 10`) —
  explicit `: Type` annotations are recognized but not yet parsed (see
  below)
- Call expressions: `callee(arg, arg, ...)`, including nested calls
- Binary and unary operators, per `grammar.md`'s "Operator Precedence and
  Associativity" table (itself adapted from Swift's real
  `precedencegroup` chain): `||`, `&&`, `==`/`!=`/`<`/`<=`/`>`/`>=`,
  `??`, `+`/`-`, `*`/`/`/`%`, unary `-`/`!`
- Primary expressions: identifiers, string/integer/float/boolean/nil
  literals, parenthesized expressions
- `func` declarations: `func name(a: Int, b: Int) -> Int { ... }` —
  params, an optional `-> Type` return type (omitted means implicit
  Void), and a `{ ... }` body of ordinary statements (so `return`,
  nested `func`s, `let`/`var`, etc. all just work inside a body with no
  special-casing)
- `return` statements, with or without a value
- Minimal type references for param/return types: a bare identifier
  with an optional trailing `?` (`Int`, `String?`) — see below for what
  this deliberately excludes
- `if`/`else`/`else-if` — `else if` desugars to a single-element else
  branch holding a nested `IfStatement` (the standard desugaring, so
  `Semantics/`/`Lowering/` don't need a separate "else-if" concept).
  `else` must directly follow the `if` body's closing `}` with no
  newline in between; `else` on its own line isn't yet supported.
- `while` loops
- Bare `break`/`continue` (no loop labels — voyage-lang doesn't have
  loop labels yet)

Explicitly **not yet** implemented — each of these produces a clear
diagnostic and a recovery node (`UnsupportedStatement`/`ErrorExpression`)
rather than a crash or silently-dropped content, so a file mixing
supported and unsupported constructs still parses as far as it can:
- Explicit `: Type` annotations on `let`/`var` (`let z: Int = 30` — the
  annotation is recognized, warned about, and ignored; the statement
  still parses using just the initializer)
- `let`/`var` with no initializer at all
- Generic function parameters and `where` clauses (`func identity<T>(_
  value: T) -> T`) — nor the Swift-style external parameter labels
  (`_ value: T`) that generic examples in `grammar.md` use; parameters
  are currently just `name: Type`
- Richer type syntax: generic type arguments (`Array<T>`), array sugar
  (`[T]`), function types (`(Int) -> String`), and keyword-spelled type
  forms (`Self`, `any P`, `some P`) — only bare-identifier types (plus
  trailing `?`) are parsed so far
- `if let`/`if case` conditional binding forms — only a plain
  boolean-valued condition expression is recognized
- `switch`, `for`-`in`, `guard`, `repeat`-`while`
- Any other declaration (`struct`, `enum`, `protocol`, `extension`,
  `actor`, ...)
- String interpolation (`"\(...)"`)
- Member access (`.`), subscripting, ternary, `as`-casting
- Range operators (`..<`, `...`) — recognized by the lexer, not yet wired
  into the expression grammar since nothing consumes them yet (`for`-`in`
  doesn't exist)

## Structural node registration

Registers structural nodes like `AtomicStatement` (ADR-0008) as
first-class AST node types so `Semantics/` can find them by type rather
than re-inspecting syntax — not yet needed at this milestone's scope, but
the `AstNode` base type and record-per-construct pattern in `Ast.cs` is
set up to extend this way as later grammar constructs are added.

## Known limitations

**Malformed declarations can produce multiple redundant diagnostics for
a single error.** Every `Expect()` call that fails reports a diagnostic
without consuming the unexpected token (by design — it lets the *next*
caller decide how to recover), but when several `Expect()` calls chain
together (e.g. parsing a malformed `func` signature), each one can hit
the same still-unconsumed token and report its own "expected X" error.
Confirmed this never causes an infinite loop — `ParsePrimary`'s
default case always advances past an unrecognized token as the ultimate
forward-progress guarantee — so this is a diagnostic-*quality* issue
(noisy output for bad input), not a correctness or safety one. Worth
addressing once error-recovery UX becomes a priority; not blocking for
now since every currently-supported construct's happy path is unaffected
and covered by tests.

## Next milestone

`struct`/`enum` — needs no new expression/statement infrastructure
(bodies reuse the same block-statement machinery), just new declaration
syntax. After that: `protocol`/`extension` → generics (`<T>`/`where`,
unblocking the external-parameter-label form of `func` params too) →
`switch` (a real design question, since meaningful pattern matching needs
`enum` cases to match against first) → `for`-`in` (needs the range
operators already lexed but not yet wired into any grammar construct) →
error handling (`throws`) → concurrency (`actor`/`task{}`, saved for last
as the most complex slice). No fixed order is binding — pick whichever
construct unblocks the most useful next test case.
