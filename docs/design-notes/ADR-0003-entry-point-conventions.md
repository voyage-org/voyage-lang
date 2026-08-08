# ADR-0003: Entry Point Conventions — Top-Level Statements and `@main`

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0001 (Swift-inspired syntax)

## Context

The README's hello world sample originally read:

```voyage
fn main() {
    print("Hello, Voyage.")
}
```

This was wrong on two independent counts:

1. `fn` was never a voyage-lang keyword — ADR-0001 settled on `func`
   (full word, not shortened), so the sample had drifted out of sync with
   the language's own syntax decision.
2. More substantively: an explicit wrapping entry function isn't how Swift
   itself handles the simple case. A top-level Swift source file (e.g.
   `main.swift`) executes its statements directly, top to bottom, with no
   `func main()` wrapper required at all. The sample was carrying an
   assumption (entry points always need an explicit function) that doesn't
   match the Swift precedent voyage-lang is otherwise following.

Separately, Swift also supports a second, more structured entry point form
via the `@main` attribute, attached to a type with a static `main()`. This
form was confirmed (via Swift's own test suite) to compose directly with
typed throws: `static func main() throws(Err)` is valid and integrates with
Swift's typed-error propagation rather than requiring a separate untyped
special case.

voyage-lang needed to pick which of these conventions to support — one,
the other, or both — since the answer changes what the correct hello world
sample looks like and what `Voyage.Cli`-scaffolded projects generate by
default.

## Decision

voyage-lang supports both conventions, matching Swift's own split, chosen
by program shape rather than forcing a single universal style:

**Top-level statements** — no wrapper required. Used for single-file
scripts and the `samples/` directory:

```voyage
print("Hello, Voyage.")
```

**`@main` attribute** — for structured, multi-file programs, the expected
default for anything scaffolded by `Voyage.Cli`:

```voyage
struct VoyageMain {
    static func main() throws(AppError) {
        print("Hello, Voyage.")
    }
}
```

`@main` composes with typed throws directly (Section 7 of `grammar.md`) —
no separate untyped-`main` fallback needed for error propagation out of the
entry point.

The corrected hello world sample in `README.md` now uses the top-level
statement form, since it's a single-file introductory example, not a
scaffolded project.

## Consequences

- **Positive:** Matches Swift's precedent exactly rather than inventing a
  voyage-lang-specific convention, keeping the entry-point story
  predictable for anyone coming from Swift.
- **Positive:** `@main`'s direct composition with typed throws avoids a
  special-cased untyped-error path just for `main`, keeping the
  error-handling model (ADR-0001 / grammar.md Section 7) uniform across
  the whole language rather than carving out an exception for entry
  points.
- **Positive:** Single-file samples stay minimal (no boilerplate wrapper),
  which matters for `samples/` and onboarding material like the README.
- **Negative / open risk:** Two valid entry-point shapes for the same
  underlying concept is marginally more surface area than picking one
  and forcing it everywhere. Mitigated by tying the choice to program
  shape (single-file vs. scaffolded project) rather than leaving it as an
  arbitrary stylistic choice — `Voyage.Cli` project scaffolding should
  default to `@main` and never generate bare top-level `main.voy` files,
  keeping the ambiguity contained to hand-written samples only.
- **Follow-up:** `Voyage.Cli`'s project template needs to actually enforce
  the `@main` default described here once scaffolding is implemented —
  not yet built, tracked as an implementation task rather than a further
  design decision.

## Alternatives Considered

- **Require `@main` everywhere, including single-file samples** — rejected;
  adds unnecessary ceremony to the simplest possible program, which is
  exactly the case (hello world, onboarding) where minimal syntax matters
  most.
- **Top-level statements everywhere, no `@main`** — rejected; loses a
  clear, typed-throws-compatible entry point convention for structured
  multi-file programs, and would have required inventing a separate
  mechanism for typed error propagation out of program entry.
- **Invent a voyage-lang-specific entry keyword** (e.g. a dedicated
  `entry` or `program` block distinct from both Swift conventions) —
  rejected; no clear benefit over reusing Swift's already-precedented
  approach, and adds surface area with no differentiation value (unlike
  the deliberate divergences already made elsewhere, e.g. typed throws
  over `Result<T,E>`).
