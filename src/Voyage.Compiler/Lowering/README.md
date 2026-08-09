# Lowering/

Not yet implemented.

AST → IR. Where implicit-return injection (ADR-0005) and `atomic{}`
flat-nesting flattening (ADR-0008 Rule 1) happen — both are "the AST says
one thing, the lowered form says the equivalent thing more explicitly"
transformations, not semantic checks (those belong in `Semantics/`).

Depends on `Semantics/` having already validated the AST.
