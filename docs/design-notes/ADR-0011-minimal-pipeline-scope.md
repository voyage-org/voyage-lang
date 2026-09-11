# ADR-0011: Minimal Pipeline Scope

- Status: Accepted
- Date: 2026-09-07
- Related: ADR-0010 (five-phase pipeline architecture, and its deferral of
  `CodeGen/`'s design); ADR-0005 (implicit return); ADR-0009 (struct/class
  → CLR value/reference type mapping, needed by `CodeGen/` even at
  minimal scope)

## Context

`Parsing/` has reached 248 passing checks but still has real, tracked
gaps: labeled call arguments (in progress), generic-call-site
disambiguation, attributes, declaration modifiers, `associatedtype`
dispatch, computed-property accessors, and array/bracket literals — all
found via the spec-example sweep and listed in `Parsing/`'s own
tracking, not this document. `Semantics/`, `Lowering/`, and `CodeGen/`
remain entirely unimplemented; their `README.md`s say "Not yet
implemented," and ADR-0010 explicitly deferred `CodeGen/`'s architecture
as premature at the time.

The implicit assumption so far has been sequential: finish `Parsing/`
completely, then start `Semantics/`. That assumption was never actually
decided — it's just the order ADR-0010 happened to describe. Two risks
follow from not deciding this explicitly:

- Every later-phase design decision (what shape does the semantic AST
  need; what does `Lowering/` actually hand to `CodeGen/`; can
  `CodeGen/` even emit what `Lowering/` produces) stays purely
  theoretical until a full pipeline exists to test it against. Integration
  problems between phases are the kind of bug that a phase-by-phase,
  parser-first order surfaces last, not first.
- Several popular languages (Go, TypeScript, early-stage Rust) didn't
  build a complete parser before standing up the rest of their pipeline —
  they shipped a deliberately small language subset end-to-end first,
  then grew the subset. voyage-lang has been spec-first and
  phase-ordered by architecture so far, but nothing about that discipline
  requires finishing one phase's full grammar coverage before another
  phase starts to exist.

This ADR decides to change that order, and — because doing so requires
`CodeGen/` to produce *something* runnable — makes the minimal slice of
the `CodeGen/` decision ADR-0010 deferred, without attempting the full
`CodeGen/` architecture (generic monomorphization in particular stays
deferred, per `type-system.md`'s open items).

## Decision

### Build a full lexer → parser → semantics → lowering → codegen slice now, on a deliberately reduced language subset

Rather than completing every parser gap before starting `Semantics/`,
implement all five phases end-to-end against a barebones subset of
voyage-lang, sufficient to compile and run a real program (starting from
the corrected `hello.voy` per ADR-0003, then growing to small
struct/function/control-flow programs). Broaden phase-by-phase only after
this slice works, not before.

**In scope for the minimal pipeline (already parses today):**
- Functions with positional (unlabeled) parameters and calls
- `let`/`var` bindings
- `if`/`else`, `while`, `break`, `continue`
- `struct`/`enum`/`case` with stored properties only
- Basic expressions, operators, string interpolation
- `switch`/pattern matching

**Explicitly deferred past this minimal pipeline** (unchanged from
`Parsing/`'s existing tracked gap list — this ADR does not resolve any of
them, only sequences them after the minimal slice instead of before it):
- Labeled call arguments
- Generic-call-site disambiguation (generic *declarations* may still
  parse; generic *instantiation* is not required to work through
  `Semantics/`/`Lowering/`/`CodeGen/` yet)
- Attributes, declaration modifiers, `associatedtype`, computed
  properties, array/bracket literals
- `actor`/`task{}`/`atomic{}`, `throws`/`try`/`catch`, `for`-in
- The `class`-keyword grammar/memory-model.md inconsistency

### `Semantics/` and `Lowering/` stay C#

Per the earlier decision in this project's history to keep the whole
compiler single-language rather than introduce F# for these phases:
closed `record` hierarchies (`abstract record` base + `sealed record`
cases) plus exhaustive `switch` expressions are used for the semantic
AST and IR, giving pattern-matching ergonomics close to a discriminated
union without a second language or a CLR-crossing boundary. This keeps
faith with the "single runtime, no FFI" lesson from Nova that has shaped
this project from the start.

### `CodeGen/` minimal target: `System.Reflection.Emit`

To get a runnable program out of the minimal pipeline without taking on
an external toolchain dependency, `CodeGen/` emits CIL directly and
in-process via `System.Reflection.Emit`, rather than emitting an IL text
format and shelling out to `ilasm`. This is the smallest decision needed
to unblock the minimal slice — it is not the full `CodeGen/` architecture
ADR-0010 deferred (generic monomorphization strategy in particular is
still out of scope here, since generics aren't part of the minimal
subset above).

## Consequences

- **Positive:** Integration problems between `Semantics/`, `Lowering/`,
  and `CodeGen/` surface while the language subset is still small enough
  to debug easily, instead of after the full grammar is implemented.
- **Positive:** Produces a genuinely runnable `voyagec` end-to-end before
  the harder parser gaps (labeled arguments, `atomic{}`, generics) are
  tackled, giving a real prerelease milestone distinct from "N parser
  checks pass."
- **Positive:** `Semantics/`/`Lowering/` staying C# means no new project,
  no cross-language AST boundary, and no NuGet-audit workaround needed
  in this network-restricted environment.
- **Negative / accepted trade-off:** Features already partly built
  against in the parser (e.g. the in-progress labeled-call-arguments
  work) don't get exercised by `Semantics/`/`Lowering/`/`CodeGen/` until
  a later broadening pass — that work isn't wasted, but its payoff is
  deferred.
- **Follow-up:** Once the minimal pipeline runs a real program end-to-end,
  broaden one deferred feature at a time (parser gap → semantics rule →
  lowering rule → codegen support), re-running the full build-verify-test
  loop after each. `CodeGen/`'s full architecture — including generic
  monomorphization — still needs its own dedicated ADR when generics
  instantiation re-enters scope.

## Alternatives Considered

- **Finish `Parsing/` completely before starting `Semantics/`** —
  rejected as the default-by-omission ordering that this ADR replaces;
  defers all integration risk to the point where the grammar is largest
  and hardest to debug against.
- **Emit IL text and shell out to `ilasm`** — rejected for the minimal
  `CodeGen/` target; adds an external toolchain dependency this
  network-restricted, single-runtime project doesn't otherwise need.
- **Design `CodeGen/`'s full architecture (including generic
  monomorphization) now** — rejected as premature; the minimal subset
  above deliberately excludes generics instantiation, so that decision
  can wait for its own ADR, consistent with ADR-0010's original
  deferral.