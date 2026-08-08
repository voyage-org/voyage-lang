# ADR-0008: Full `atomic{}` Syntax — Nesting, Cross-Actor Calls, Transitive-Await Detection

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0006 (actor reentrancy model; established the no-`await`
  golden rule this ADR builds on), grammar.md Section 8/11

## Context

ADR-0006 settled `atomic{}`'s single non-negotiable rule — no `await`
inside the block — and gave the mechanical reason: the actor worker loop
can only interleave at suspension points, so an `atomic{}` with none is
guaranteed to run start-to-finish as a single unit against actor state.

That ADR explicitly left three things open, all under the umbrella of
"nesting/transitive-call detection specifics":

1. What happens when `atomic{}` blocks are nested inside each other.
2. Whether `atomic{}` can call other methods — on `self` or on other
   actors — and under what constraints.
3. How the compiler catches a developer routing around the no-`await`
   rule indirectly, by calling an innocuous-looking helper function that
   itself (or transitively, several calls deep) contains an `await`.

Because voyage-lang runs natively on CoreCLR/NativeAOT, all of this must be
resolved entirely at compile time in `Voyage.Compiler/Semantics/`. There is
no runtime thread-tracking, no mutex, no dynamic check standing behind
`atomic{}` — the guarantee is a static one, enforced once during
compilation, so the CIL `Voyage.Runtime` executes stays lean and
zero-allocation for this feature. A runtime-checked version of any of
these rules would defeat the entire performance rationale for choosing the
lock-free mailbox design in ADR-0006 in the first place.

## Decision

### Rule 1 — Flat, idempotent nesting

Nesting an `atomic{}` block inside another `atomic{}` block is syntactically
legal but semantically a no-op: it does not create a second, inner
transaction boundary.

```voyage
atomic {
    balance -= amount
    atomic {
        // legal, but boundary-less — flattened into the outer block
        auditLog.append("withdrawal")
    }
}
```

**Lowering behavior** (`Voyage.Compiler/Lowering/`): when the lowering
phase encounters an `AtomicStatement` nested inside another
`AtomicStatement`, it strips the inner block's boundary wrapper and splices
its statements directly into the single enclosing synchronous execution
frame. The generated CIL has exactly one atomic region, not a stack of
nested ones — there's nothing to "re-enter" since the whole point of the
rule from ADR-0006 is that nothing suspends inside the region in the first
place, so nested boundaries would be pure ceremony with no corresponding
runtime concept underneath them.

### Rule 2 — Calling other synchronous methods

An `atomic{}` block may call:
- other synchronous methods on `self` (the same actor), and
- synchronous methods on **other** actors,

**provided** the target method is guaranteed entirely synchronous and
non-suspending. Specifically banned:
- calling a method marked `async`, and
- calling anything that crosses an actor isolation boundary in a way that
  would require an implicit asynchronous dispatch (i.e., a cross-actor
  call that the compiler would otherwise need to route through the
  target's mailbox with an implied `await`).

The second case matters independently of the first: even a method that
isn't textually marked `async` can still be unsafe to call from `atomic{}`
if calling it cross-actor would require dispatch through the target
actor's own mailbox loop, since that's a suspension point in everything
but name.

### Rule 3 — Transitive-await detection (call-graph validation)

The hardest problem: a helper function that looks perfectly synchronous at
the call site inside `atomic{}`, but hides an `await` several calls deep in
its own call graph. `Voyage.Compiler/Semantics/` closes this with an
effects pass, computed once per compilation unit:

1. **Async marking.** The compiler maps every function/method declaration
   in the compilation unit. Any declaration containing an explicit `await`,
   or calling another declaration already tagged as having suspension
   points, has its symbol metadata marked `HasSuspensionPoints = true`.
2. **Transitive crawling.** When checking an `atomic{}` block, an AST
   visitor walks every statement in the block. On each call expression, it
   looks up the target's symbol metadata.
3. **Trailing evaluation.** If the call graph shows the target — or
   *anything the target calls, at any depth* — resolves to
   `HasSuspensionPoints = true`, the compiler flags an implicit atomicity
   escape violation.

This is a standard whole-program (or whole-compilation-unit) effects/
purity-style static analysis — the same shape of problem as, e.g., checked
exceptions or effect systems in other languages, applied specifically to
"does this call graph ever suspend."

### Rule 4 — External assemblies and function-typed values: unverifiable is unsafe

Rule 3's transitive crawl only works because voyage-lang source always
exposes enough information for `Voyage.Compiler/Semantics/` to compute
`HasSuspensionPoints` by walking the AST. Two call shapes break that
assumption, and both are resolved the same way: **if the call graph can't
be statically verified, the call is unsafe by default and banned inside
`atomic{}`.**

**External-assembly calls.** A method imported from a referenced C#
assembly (per ADR-0007, resolved via CLR metadata rather than voyage-lang
source) has no voyage-lang AST for the effects pass to crawl — its IL body
isn't a reliable signal for whether it awaits internally in a form the
compiler can read the same way it reads voyage-lang source. Rather than
guess from a proxy signal (naming conventions, `Task`/`Task<T>`-returning
signatures), voyage-lang treats every external-assembly call from inside
`atomic{}` as unsafe by default:

```voyage
atomic {
    balance -= amount
    externalLedger.recordDebit(amount)  // ❌ compile error by default —
                                          // external call, unverifiable
}
```

An external method may be called from inside `atomic{}` only if it carries
an explicit safety annotation asserting the author has verified it never
suspends — `@syncSafe`, attached to a **scoped import** of that specific
symbol:

```voyage
@syncSafe
import func System.Math.Sqrt          // fine as-is: rarely a real question,
                                       // but the annotation is required anyway
                                       // for consistency and auditability

@syncSafe
import func ExternalLedger.recordDebit
```

`@syncSafe` attaches only to scoped (single-symbol) imports, never to a
whole-namespace `import`, and never at the individual call site — this
keeps the assertion in one auditable place per symbol rather than scattered
across every call, and makes a whole-namespace import of an unaudited
library safe by default (nothing in it is callable from `atomic{}` until
someone deliberately re-imports that one symbol with the annotation).
Instance methods on imported types need a scoped import naming the
member the same way; if the target member can't be named as a standalone
scoped import, the recommended pattern is a small voyage-lang-authored
wrapper function that calls it — since that wrapper is real voyage-lang
source, Rule 3's ordinary transitive crawl verifies it like anything else,
and no `@syncSafe` assertion is needed at all.

Because this annotation is a manual, human-asserted claim rather than
something the compiler verified itself, it is a deliberate trust boundary:
the developer is vouching for an assembly's behavior the compiler cannot
check. This mirrors how `unsafe` blocks work in other CLR languages — the
compiler stops checking and the human takes on the responsibility,
explicitly and visibly at the import site.

**Function-typed values.** Function pointers, delegates, and closures
passed into or captured by an `atomic{}` block break the static call graph
the same way — the actual target isn't known until runtime in the general
case, so there's nothing for the transitive crawl to walk. Unlike external
assemblies, there's no annotation escape hatch here: indirect calls through
unconstrained function-typed values are **banned entirely** inside
`atomic{}`, full stop.

```voyage
atomic {
    let handler: () -> Void = someHandler
    handler()  // ❌ compile error — indirect call, target not statically known
}
```

The asymmetry between the two rules is deliberate: an external assembly is
at least a fixed, inspectable artifact a human can audit once and assert
safety for; a function-typed value's target can vary per call site and per
invocation, so there's no stable thing to annotate as safe in the first
place.

### Compiler pipeline

Purely compile-time, in three phases, with nothing added to
`Voyage.Runtime`:

- **`Parsing/`** — the parser registers `atomic { }` as a structural
  boundary node in the AST (`AtomicStatement`), distinct from an ordinary
  block, so later phases can identify atomic regions without re-parsing.
- **`Semantics/`** — runs the async-marking pass over the whole
  compilation unit, then the transitive crawl described in Rule 3 against
  every `AtomicStatement` found.
- **`Diagnostics/`** — on a violation, compilation halts before CIL
  lowering. The diagnostic surfaces the full offending path, not just the
  immediate call, e.g.:

  ```
  ❌ Compilation Error [Line 12, Column 5]: An `atomic` block cannot
     invoke elements containing latent suspension points.
     • `atomic` block invokes `maliciousHelper()`
     • `maliciousHelper()` transitively executes `networkService.ping()`
       which contains an `await` checkpoint.
  ```

  Surfacing the full downstream path (not just "some call in here is bad")
  is deliberate — the whole difficulty of this bug class is that the
  suspension point is hidden several frames away from the `atomic{}` site
  itself, so the diagnostic needs to do the work of showing the developer
  the path a human wouldn't have traced by eye.

## Consequences

- **Positive:** All three rules are enforced with zero runtime cost —
  no thread tracking, no mutex, no dynamic checks in `Voyage.Runtime`,
  consistent with the lean-CIL goal and with ADR-0006's overall
  compile-time-first philosophy for actor safety.
- **Positive:** Rule 3's effects pass generalizes cleanly — the same
  `HasSuspensionPoints` metadata that guards `atomic{}` is naturally
  reusable if voyage-lang ever wants other suspension-sensitive static
  checks in the future (e.g. a hypothetical `sync`-only interface
  constraint), without inventing a second analysis pass.
- **Negative / open risk:** Whole-compilation-unit call-graph analysis has
  a real cost for `Voyage.Compiler` build times on large codebases,
  especially with recursive or highly generic call graphs. Needs
  benchmarking once implemented; may need incremental/cached effects
  computation rather than a full re-crawl per build.
- **Resolved:** external-assembly calls and indirect calls through
  function-typed values are both handled by Rule 4 below — see that
  section for the full mechanics and rationale. Both were open follow-ups
  in earlier drafts of this ADR and are now closed with conservative,
  ban-by-default resolutions.

## Alternatives Considered

- **Runtime-checked atomicity** (e.g. a re-entrancy guard that throws if an
  `atomic{}` region is somehow entered from within an in-progress one, or
  detects an accidental `await` at runtime) — rejected; defeats the
  entire zero-allocation, zero-thread-tracking performance rationale
  behind choosing the lock-free mailbox design in ADR-0006. If it's
  checkable statically, it should be — that's the whole point of a
  compile-time-only guarantee.
- **True nested atomic transactions** (inner `atomic{}` as a real, separate
  boundary, e.g. with rollback-on-failure semantics) — rejected as
  over-engineering for what `atomic{}` is actually for (ADR-0006: a
  synchronous re-validate-and-apply step, not a general transaction
  system). Flat/no-op nesting is simpler and matches the block's actual
  purpose.
- **Banning all method calls from `atomic{}` entirely** (only plain
  statements/expressions on `self`'s own fields allowed) — rejected as too
  restrictive; would make the `withdraw()`-style example from ADR-0006
  awkward to write for anything beyond trivial field mutation, without a
  corresponding safety benefit over the more precise transitive-await
  check.
- **Proxy-signal detection for external assemblies** (e.g. treating any
  C# method not returning `Task`/`Task<T>` as presumed synchronous) —
  rejected; naming and signature conventions are not a reliable substitute
  for actually knowing whether a method suspends, and a false negative
  here would silently reintroduce the exact hazard `atomic{}` exists to
  prevent. An explicit, human-asserted annotation is a visible trust
  boundary; a proxy heuristic is an invisible one.
- **Allowing function-typed calls with a runtime guard** (check at the
  call site whether the invoked delegate happens to suspend, and fault if
  so) — rejected for the same reason runtime-checked atomicity in general
  was rejected above: it reintroduces runtime cost and a fault that
  could theoretically surface far from its actual cause, instead of a
  compile-time refusal to build.