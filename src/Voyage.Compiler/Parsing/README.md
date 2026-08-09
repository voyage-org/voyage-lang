# Parsing/

**First milestone reached.** Converts the token stream from `Lexing/`
into an AST, via hand-written recursive descent (ADR-0010 — no parser
generator).

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

(Reproduced by `tests/Voyage.Compiler.Tests`, which also exercises
multi-argument and nested calls, every literal kind, and parenthesized
expressions.)

## Current scope

Implemented:
- Top-level expression statements
- Call expressions: `callee(arg, arg, ...)`, including nested calls
- Primary expressions: identifiers, string/integer/float/boolean/nil
  literals, parenthesized expressions

Explicitly **not yet** implemented — each of these produces a clear
diagnostic and a recovery node (`UnsupportedStatement`/`ErrorExpression`)
rather than a crash or silently-dropped content, so a file mixing
supported and unsupported constructs still parses as far as it can:
- Any declaration (`let`, `var`, `func`, `struct`, `enum`, `protocol`,
  `extension`, `actor`, ...)
- Any control flow (`if`, `switch`, `for`, `while`, ...)
- String interpolation (`"\(...)"`)
- Binary/unary operators, member access (`.`), subscripting

## Structural node registration

Registers structural nodes like `AtomicStatement` (ADR-0008) as
first-class AST node types so `Semantics/` can find them by type rather
than re-inspecting syntax — not yet needed at this milestone's scope, but
the `AstNode` base type and record-per-construct pattern in `Ast.cs` is
set up to extend this way as later grammar constructs are added.

## Next milestone

Grow the grammar outward from here, roughly: `let`/`var` bindings → `func`
declarations with typed params/returns → control flow (`if`/`switch`) →
`struct`/`enum` → `protocol`/`extension` → error handling (`throws`) →
concurrency (`actor`/`task{}`, saved for last as the most complex slice).
No fixed order is binding — pick whichever construct unblocks the most
useful next test case.
