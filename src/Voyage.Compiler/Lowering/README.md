# Lowering/

Implements two rewrites over a `Semantics.BoundCompilationUnit`, both
required by ADR-0011's minimal pipeline subset:

1. **Implicit-return injection** (ADR-0005) — a function whose entire
   body is one bare expression statement gets that expression wrapped in
   an explicit `BoundReturn`.
2. **Switch/pattern-match desugaring** — a `BoundSwitch` is rewritten
   into an equivalent chain of ordinary `BoundIf` statements, so
   `CodeGen/` never needs to know what a pattern is — only how to emit
   an `if`.

## Design note: no separate IR type

This phase deliberately does **not** introduce a full parallel IR
hierarchy mirroring `Semantics/BoundAst.cs`. For ADR-0011's minimal
subset, the two rewrites above are the only things that actually need to
change anything — arithmetic, calls, struct construction, property
access, `if`/`while`, and variable declarations all pass through
unchanged. Building a second tree type to carry the ~90% of nodes that
need no transformation would be pure duplication with no payoff at this
scope, so `Lowerer` transforms a `BoundCompilationUnit` in place and
hands `CodeGen/` that same type back.

Two node kinds do genuinely not exist before lowering, since pattern
matching against an enum's case has no equivalent operation in
`Semantics/` (enums have no "tag" or associated-value layout until
`CodeGen/` picks a concrete CLR representation for one — an open
decision, see `CodeGen/README.md`):

- **`LoweredEnumTagCheck`** — "is this value currently case X?"
- **`LoweredAssociatedValueAccess`** — "read associated-value slot N,
  assuming the tag check for that case already passed"

Both live in `LoweredAst.cs`, as `BoundExpression` subtypes so they slot
directly into the existing tree rather than needing a parallel one.

## Files

- **`LoweredAst.cs`** — the two node kinds above.
- **`Lowerer.cs`** — the transformation:
  - `Lower(BoundCompilationUnit) -> BoundCompilationUnit` — the phase
    entry point.
  - `InjectImplicitReturn` — the ADR-0005 rewrite.
  - `LowerStatements`/`LowerStatement` — recurses into `if`/`while`
    bodies so a `switch` nested inside either still desugars (not just
    one directly at a function's top level).
  - `BuildCaseChain` — builds the `BoundIf` chain for a switch's cases,
    with the guard-fallthrough handling documented in its own remarks
    (a failing `where` guard falls through to the *next case*, not
    straight to the switch's `default` — which means the "rest of the
    chain" is deliberately reused by *reference* in two places, rather
    than flattened into one boolean test).
  - `CompilePattern`/`CompileEnumCasePattern` — turns one bound pattern
    into a boolean test plus any bindings it introduces, recursing for
    an enum case's associated-value slots.

## What's still open

- **No general fall-off-the-end return checking.** Implicit-return
  injection only recognizes the exact single-bare-expression-body shape
  `Parsing/` already documents for it — it does not perform reachability
  analysis to catch a multi-statement, non-`Void` function that fails to
  return on every path. This is not caught anywhere in the pipeline yet;
  tracked here as an open gap until either this phase or `Semantics/`
  grows real control-flow analysis.
- **Ambiguous multi-pattern bindings are diagnosed here, not in
  `Semantics/`.** A case like `case .circle(let x), .square(let y):`
  binds a different name depending on which alternative matched — real
  Swift requires identical bindings across every comma-separated
  pattern, which `Binder` doesn't check yet (see `Semantics/README.md`'s
  own open-items list). `Lowerer` catches the case where *different*
  names are bound (this file's own diagnostic); the case where the
  *same* name is bound twice happens to already fail earlier, at
  `Binder`'s ordinary same-scope-redeclaration check, since both
  patterns are bound into one shared case scope.
- **No switch exhaustiveness checking** — unchanged from `Semantics/`'s
  own open item; a non-exhaustive switch with no `default` simply
  produces a `BoundIf` chain whose final `else` is an empty statement
  list (silently doing nothing) rather than being flagged anywhere.
- **`atomic{}` flat-nesting flattening (ADR-0008 Rule 1)** — not
  implemented; depends on `actor`/`task{}`/`atomic{}` parsing at all,
  which ADR-0011 defers past the minimal pipeline.

Depends on `Semantics/` having already validated the bound tree;
`CodeGen/` is the next phase downstream, still unimplemented (and still
needs its own ADR before implementation starts, per its own README).
