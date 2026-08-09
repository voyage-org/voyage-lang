# Semantics/

Not yet implemented.

Type checking, name resolution, and general inference
(`type-system.md` Section 5). Also where several compile-time-only
guarantees from the design-notes get enforced:

- The `HasSuspensionPoints` effects pass and transitive-await call-graph
  crawl for `atomic{}` (ADR-0008).
- The post-`await` actor-state-mutation diagnostic (ADR-0006).

Depends on `Parsing/` producing an AST first.
