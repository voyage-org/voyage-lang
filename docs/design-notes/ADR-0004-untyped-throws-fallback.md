# ADR-0004: Support Untyped `throws` Alongside Typed Throws

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0001 (Swift-inspired syntax), grammar.md Section 7

## Context

grammar.md Section 7 adopted Swift's typed throws (`throws(ErrorType)`) as
voyage-lang's error-handling model, diverging from Aurelia's `Result<T, E>`
pattern. The draft left one question explicitly open: whether plain,
untyped `throws` (propagating `any Error`) remains available as a fallback,
or whether typed throws is mandatory everywhere.

Making typed throws mandatory would cripple ergonomics for a modern,
general-purpose language. Every throwing function would need a concrete,
named error type at the declaration site, every call site chaining multiple
differently-typed throwers would need to collapse back down to a common
type anyway (usually `any Error`), and every library's public API would be
locked into a breaking-change hazard the moment its error set needs to
grow.

Swift's own history is instructive here and directly informed this
decision. Plain `throws` has existed since Swift 2.0 (2014–2015). Typed
throws didn't arrive until Swift 6.0, via SE-0413 — accepted December 2023,
shipped September 2024. That's roughly nine years between the two. Swift's
error-handling rationale (`docs/ErrorHandlingRationale.md` in the Swift
source) explicitly frames the *general* case as one where "errors are
generally propagated and rendered, but rarely handled exhaustively, and are
prone to changing over time in a way that types are not" — untyped `throws`
was a deliberate, considered default, not an oversight later patched by
typed throws. When typed throws finally landed, it landed *alongside*
untyped throws as an additional, opt-in tool for a narrower set of cases,
not as a replacement.

## Decision

voyage-lang supports both untyped and typed `throws`, matching Swift's
current state exactly:

```voyage
enum ConfigError: Error {
    case fileNotFound(path: String)
    case parseFailure(reason: String)
}

// Typed — precise, exhaustive catch, best for a fixed, known error surface
func loadConfig(path: String) throws(ConfigError) -> Config { ... }

// Untyped — default choice for app code, evolving library APIs, or
// functions composing several differently-typed throwers
func run() throws {
    let config = try loadConfig(path: "voyage.toml")
}
```

**Guidance, not compiler-enforced:** typed throws for performance-critical
or embedded-style code paths with a small, fixed error surface (mirroring
Swift's own stated sweet spot); untyped `throws` as the default everywhere
else, especially for public library APIs whose error sets may grow over
time — since a typed-throws signature change is a breaking change in a way
an untyped one is not.

## Consequences

- **Positive:** Ergonomics stay intact for the common case — most
  functions, most of the time, don't need to think about typed throws at
  all.
- **Positive:** Public library API authors get a real choice with a real
  trade-off (precision vs. future-proofing) instead of being forced into
  one or the other.
- **Positive:** Directly reuses a battle-tested precedent — Swift shipped
  this exact split after years of real-world experience with the
  untyped-only model, so voyage-lang isn't guessing at the right default.
- **Negative / open risk:** Two error-declaration styles for the same
  underlying concept means `catch` blocks need to handle both narrow
  (`catch let error as ConfigError`) and exhaustive (`catch`) forms —
  slightly more surface area for `Voyage.LanguageServer` diagnostics and
  for newcomers to learn. Mitigated by the same split existing in Swift,
  so documentation precedent exists to draw on.
- **Follow-up:** Whether `async` functions require a decision here too
  (Swift's `Task<Success, Failure>` reflects the thrown error type in its
  generic signature) is not yet addressed — worth confirming this composes
  cleanly with the `Future<T>`/`actor` concurrency model from ADR-0002
  before `Voyage.Runtime` implementation begins.

## Alternatives Considered

- **Typed throws mandatory everywhere** — rejected outright per the
  ergonomics concern above; would have made voyage-lang stricter than
  Swift itself chose to be after years of consideration, with no
  corresponding benefit.
- **Untyped throws only, drop typed throws entirely** — rejected; loses
  the compile-time-checked, exhaustive-catch benefit that was the whole
  motivation for adopting typed throws over Aurelia's `Result<T, E>` in
  the first place (grammar.md Section 7 / ADR-0001).
- **`Result<T, E>` instead of throws entirely** (Aurelia's approach) —
  already rejected in ADR-0001; not revisited here.
