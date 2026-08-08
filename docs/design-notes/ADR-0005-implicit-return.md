# ADR-0005: Implicit Return for Single-Expression Bodies

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0001 (Swift-inspired syntax), grammar.md Section 3

## Context

`grammar.md` Section 3 originally left implicit return as an open TBD, with
the `greet()` example carrying a comment flagging the undecided status.

Making `return` mandatory everywhere would add visual noise disproportionate
to its value, especially for the kind of code a modern, expression-oriented
language is meant to make pleasant: simple closures, computed properties,
factory methods, functional data-transformation pipelines. For an
interoperable language targeting the CLR, this also has essentially zero
runtime cost — implicit return is purely a compile-time convenience.

Swift's own history was checked directly rather than assumed, since it
informs the right *scope* for this feature, not just whether to have it:

- **SE-0255** (Swift 5.1, 2019) introduced implicit return for functions,
  computed properties, and subscripts whose entire body is a **single
  expression** — extending a shorthand closures already had.
- **SE-0380** (Swift 5.9, 2023) added `if` and `switch` as *expressions*,
  usable in return position, assignment, and variable declaration. Critically,
  the accepted proposal **explicitly declined** to extend this to
  multi-statement branches: the Swift Language Workgroup's stated reasoning
  was that allowing arbitrary control flow (nested `return`, `break`, etc.)
  out of expression position would be "unexpected and error-prone" and
  harder to reason about, and judged the use cases for going further as
  "fairly niche" relative to the complexity cost.

So the real Swift precedent isn't "implicit return grew over time to cover
arbitrarily complex function bodies." It's "implicit return was deliberately
kept scoped to single-expression bodies, and later work expanded what counts
as a single expression (`if`/`switch` with single-expression branches)
rather than lifting the single-expression restriction itself." Swift's core
team looked directly at going further and chose not to.

## Decision

voyage-lang adopts implicit return, scoped exactly the way Swift scoped it:

- A function, computed property, or subscript whose body is a **single
  expression** matching the declared return type omits `return`.
- `if`/`switch` count as a single expression when **every branch is itself
  a single expression** — mirroring SE-0380's scope precisely.
- Multi-statement bodies always require an explicit `return`. This is a
  permanent scope limit, not a placeholder for a future relaxation.

```voyage
func greet(name: String) -> String {
    "Hello, \(name)!"
}

func rating(for score: Int) -> String {
    if score > 800 {
        "Excellent"
    } else if score > 500 {
        "Good"
    } else {
        "Needs work"
    }
}
```

### Lowering to CIL

This changes nothing about compiled output or performance. During AST
lowering (`Voyage.Compiler/Lowering/`), the compiler checks whether the
final statement of a body is an expression whose type matches the
function's declared return type; if so, it injects the `ret` CIL
instruction implicitly rather than requiring the source to spell out
`return`. Explicit and implicit forms lower to identical CIL — this is a
front-end/parser-level convenience only, with zero cost at the IL or
runtime level.

## Consequences

- **Positive:** Cleaner expression-oriented code for the cases voyage-lang
  is explicitly designed to make pleasant (functional pipelines, factory
  methods, simple computed values) — see the "Clean Functional and
  Declarative Code" rationale.
- **Positive:** Zero runtime or IL-size cost; purely a parser/lowering-phase
  convenience, confirmed by how Swift itself implements it and how
  `Voyage.Compiler/Lowering/` would apply the same check.
- **Positive:** Adopting Swift's exact scope (not a broader "any final
  expression in any body" rule) avoids the specific control-flow ambiguity
  problems Swift's own evolution review identified and designed around —
  voyage-lang doesn't need to rediscover those problems independently.
- **Negative / minor risk:** Newcomers occasionally find the rule
  surprising at first ("why does this function return without `return`,
  but that one needs it?") — mitigated by the rule being simple and
  consistent (single-expression body, full stop) rather than heuristic.

## Alternatives Considered

- **Mandatory explicit `return` everywhere** — rejected; adds ceremony
  disproportionate to the value for exactly the kind of terse, declarative
  code voyage-lang wants to read well.
- **Full multi-statement implicit return** (final expression of any body,
  regardless of statement count, implicitly returned) — rejected. This is
  not what Swift does or has ever done, despite surface appearances; Swift
  explicitly considered and rejected this exact expansion in the SE-0380
  review for concrete, well-reasoned control-flow concerns. Adopting it
  anyway would mean reintroducing a problem Swift's language design
  process already worked through and steered away from, with no
  differentiation benefit to justify the risk.
