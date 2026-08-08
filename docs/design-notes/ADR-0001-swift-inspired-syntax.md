# ADR-0001: Adopt Swift-Inspired Syntax for voyage-lang

- Status: Accepted
- Date: 2026-08-08
- Supersedes: informal "Aurelia-inspired" syntax sketch (fun/variant/concept/extend)

## Context

voyage-lang is a standalone, curiosity-driven language under voyage-org,
targeting CIL/.NET (CLR) rather than Rust, specifically to avoid the
multi-language FFI failure mode that stalled Nova (C/Rust/Java/Python split
that never wired end-to-end — see Nova post-mortem).

Two syntax directions were on the table:

1. **"Aurelia-inspired"** — an early sketch using `fun`, `variant`,
   `concept`, `extend`, assumed to echo Aurelia's C++23/LLVM/MLIR-core
   language. This sketch was never checked against Aurelia's actual source.
2. **Swift-inspired** — adopting Swift's declaration and control-flow
   vocabulary (`func`, `struct`, `enum`, `protocol`, `extension`, `switch`,
   `if let`/`guard let`), while keeping voyage-lang's own decisions on
   target runtime, async model, and toolchain.

Two verification passes changed the picture before this decision was locked
in:

- Aurelia's real source turned out to be Rust/Python-hybrid — `fn`, `let`,
  `match`, `|>` pipe operator, `@decorator` annotations, first-class
  `tensor<[Dims], dtype>` types. Nothing like the `fun`/`variant`/`concept`/
  `extend` sketch. That sketch was an unverified inference, not a real
  reflection of Aurelia.
- A pass over the actual language grammar confirmed current Swift syntax
  (typed throws, `actor` isolation, `borrowing`/`consuming` ownership
  modifiers) rather than relying on possibly-stale training recall.

## Decision

voyage-lang adopts Swift-inspired syntax as its primary direction:

- `func` (full keyword, not shortened to `fun`)
- `struct` / `enum` (with associated values) for product/sum types
- `protocol` for interfaces (avoids the C++20 `concept` keyword collision
  that the discarded Aurelia-guess direction would have invited)
- `extension` for retroactive conformance / impl blocks
- `\(...)` string interpolation
- `switch`/`case`, `if let`/`guard let` for control flow and optional
  binding
- Typed throws (`func f() throws(MyError) -> T`) instead of Aurelia's
  `Result<T, E>` pattern, for ergonomic propagation with compile-time-checked
  error types

This is a deliberate divergence from Aurelia, not a coincidence: voyage-lang
and Aurelia are meant to read as distinct sibling languages in the Deepcomet
AI language portfolio, not syntax twins with different runtimes.

## Consequences

- **Positive:** Swift's syntax is mature, well-documented, and has strong
  editor/tooling precedent (LSP patterns, syntax highlighting grammars)
  that `Voyage.LanguageServer` can draw on.
- **Positive:** Clear differentiation from Aurelia avoids voyage-lang being
  perceived as "Aurelia's syntax with a CLR backend."
- **Negative / open risk:** Swift's full grammar includes features
  (property wrappers, result builders, macros) not yet evaluated for
  voyage-lang — risk of scope creep if all of Swift's surface area is
  chased. Mitigated by keeping an explicit "not yet decided" list in
  `grammar.md` rather than silently adopting everything Swift has.
- **Negative:** Ownership modifiers (`borrowing`/`consuming`) are
  meaningful in Swift's ARC/value-semantics model but don't map cleanly
  onto the CLR's GC-based runtime. Deferred rather than adopted outright —
  see open item in `grammar.md`.

## Alternatives Considered

- **Keep "Aurelia-inspired" sketch** — rejected once Aurelia's real syntax
  was confirmed as Rust/Python-hybrid; the sketch no longer had a real
  language to be "inspired by" and was based on incorrect assumptions.
- **Fully novel syntax** (the earlier journey-vocabulary direction —
  route/craft/manifest/orbit/hull) — rejected as too gimmicky/thematic
  over substance in earlier discussion.
- **Mirror Aurelia's real Rust/Python-hybrid syntax** — rejected in favor
  of differentiation; there's little value in two siblings sharing near-
  identical surface syntax with different backends.