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

Explicitly **not yet** implemented — each of these produces a clear
diagnostic and a recovery node (`UnsupportedStatement`/`ErrorExpression`)
rather than a crash or silently-dropped content, so a file mixing
supported and unsupported constructs still parses as far as it can:
- Explicit `: Type` annotations on `let`/`var` (`let z: Int = 30` — the
  annotation is recognized, warned about, and ignored; the statement
  still parses using just the initializer)
- `let`/`var` with no initializer at all
- Any other declaration (`func`, `struct`, `enum`, `protocol`,
  `extension`, `actor`, ...)
- Any control flow (`if`, `switch`, `for`, `while`, ...)
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

## Next milestone

`func` declarations (params, return type, body) — needs bindings and
operators as building blocks, both of which now exist. After that:
control flow (`if`/`switch`) → `struct`/`enum` → `protocol`/`extension` →
error handling (`throws`) → concurrency (`actor`/`task{}`, saved for last
as the most complex slice). No fixed order is binding — pick whichever
construct unblocks the most useful next test case.
