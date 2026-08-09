# Parsing/

Not yet implemented.

Converts the token stream from `Lexing/` into an AST, via hand-written
recursive descent (ADR-0010 — no parser generator). Registers structural
nodes like `AtomicStatement` (ADR-0008) as first-class AST node types so
`Semantics/` can find them by type rather than re-inspecting syntax.

**First milestone** (per ADR-0010): parse `print("Hello, Voyage.")`
(`samples/hello.voy`) into a minimal AST — a top-level call-expression
statement — and confirm it round-trips through an AST dump. No other
grammar construct needs to parse before that milestone is reached.
