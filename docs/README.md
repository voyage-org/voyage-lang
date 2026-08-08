# voyage-lang Documentation

This directory holds voyage-lang's language specification and design
rationale. Nothing here is implemented yet — `Voyage.Compiler` work
follows a spec-before-code discipline, so these documents are the source
of truth for what the language is supposed to do before any of it is
built.

## How to use this

- **New to the language design?** Start with `spec/grammar.md`, then
  `spec/type-system.md`, then `spec/memory-model.md`.
- **Wondering why something is the way it is?** Check `design-notes/` —
  every non-trivial decision in the spec has a matching ADR explaining
  the reasoning, the trade-offs, and what alternatives were rejected and
  why.
- **Implementing a compiler feature?** Read the relevant spec section
  *and* its linked ADR before writing code — the ADR usually contains
  constraints (compiler-enforced rules, lowering behavior, performance
  rationale) that the spec section only summarizes.

## `spec/`

The language specification, organized by concern:

| File | Covers |
|---|---|
| [`grammar.md`](spec/grammar.md) | Lexical structure, declarations (`func`/`struct`/`enum`/`protocol`/`extension`/`actor`), control flow, error handling, concurrency, modules/imports, entry points, the full token appendix |
| [`type-system.md`](spec/type-system.md) | Generics, associated types, variance, `Optional<T>`, existential (`any`) vs. opaque (`some`) protocol types, type inference, primitive-to-CLR type mapping |
| [`memory-model.md`](spec/memory-model.md) | Value vs. reference semantics, garbage collection, `Disposable`/`using`, `defer`, actor memory, ownership (deferred), weak references |

All three are living drafts (currently v0.1) — each has its own "Open
Items for Next Pass" section tracking what's still undecided. Check there
before assuming a gap in the spec is an oversight rather than a
deliberately tracked follow-up.

## `design-notes/`

Architecture Decision Records (ADRs), numbered in the order they were
accepted. Each documents context, decision, consequences, and rejected
alternatives — not just the "what," but the "why," so a decision isn't
silently re-litigated later without someone seeing why it was made this
way the first time.

| ADR | Decision |
|---|---|
| [0001](design-notes/ADR-0001-swift-inspired-syntax.md) | Adopt Swift-inspired syntax, superseding the earlier unverified "Aurelia-inspired" sketch |
| [0002](design-notes/ADR-0002-concurrency-model.md) | `actor` (isolation) and `task{}`/`spawn{}`/`async let` (structured concurrency) as complementary layers, not competing alternatives |
| [0003](design-notes/ADR-0003-entry-point-conventions.md) | Support both top-level-statement and `@main`-attribute entry points, matching Swift |
| [0004](design-notes/ADR-0004-untyped-throws-fallback.md) | Support untyped `throws` alongside typed throws; untyped is the default recommendation |
| [0005](design-notes/ADR-0005-implicit-return.md) | Implicit return for single-expression bodies only (including `if`/`switch` expressions), matching Swift's deliberate scope limit |
| [0006](design-notes/ADR-0006-actor-reentrancy-model.md) | Fully reentrant actors via a lock-free `Channel<T>`-backed mailbox loop; `atomic{}`'s golden rule (no `await` inside) |
| [0007](design-notes/ADR-0007-module-import-syntax.md) | Module/import syntax resolves directly against CLR assembly metadata — no ClangImporter-style bridging layer needed for C# interop |
| [0008](design-notes/ADR-0008-atomic-block-full-syntax.md) | Full `atomic{}` rule set: flat/no-op nesting, synchronous-call constraints, whole-compilation-unit transitive-await detection, `@syncSafe` for external-assembly exemptions |
| [0009](design-notes/ADR-0009-tracing-gc-not-arc.md) | Reference-type memory management uses the CLR's tracing GC, not Swift's ARC |

### A note on ADR-0006 and ADR-0008/ADR-0009

These three form a recurring pattern worth naming explicitly, since it's
the throughline for most of the harder decisions in this project: Swift
solves a problem (reentrant actors, memory management) using a runtime
mechanism (a custom cooperative scheduler, ARC) that has no equivalent on
the CLR. Rather than force-porting Swift's mechanism, each of these ADRs
asks "what does the CLR actually give us, and how do we hit the same
*design goal* using that instead" — `Channel<T>` + `ThreadPool` instead of
a custom scheduler, tracing GC instead of retain/release. Expect this
pattern to recur in future ADRs whenever a Swift-inspired feature meets a
CLR-shaped constraint.

## Numbering and status

ADRs are numbered sequentially and never renumbered, even if a later ADR
revises or narrows an earlier one (see ADR-0006 → ADR-0008 for an example
of a later ADR resolving a follow-up an earlier one explicitly left open).
Each ADR's `Status` field reflects its current standing — `Accepted`
unless a future ADR explicitly supersedes it, in which case both the old
and new ADR should cross-reference each other.

## What's not here yet

- `docs/spec/` doesn't yet cover: property wrappers/result builders,
  `@testable import`/re-export syntax, or a package/project manifest
  format — all tracked as deferred or out-of-scope-for-language-design in
  the relevant spec file's open items.
- No ADRs yet exist for `Voyage.Compiler`'s internal architecture (parser
  strategy, pass structure, generic specialization/monomorphization
  approach) — these are implementation-phase decisions, expected once
  compiler work begins in earnest.
