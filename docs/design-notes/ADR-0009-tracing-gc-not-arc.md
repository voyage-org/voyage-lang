# ADR-0009: Tracing GC, Not ARC, for Reference-Type Memory Management

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0001 (Swift-inspired syntax), ADR-0006 (established the
  precedent of adapting a Swift runtime concept to the CLR rather than
  porting it verbatim), `docs/spec/memory-model.md`

## Context

`memory-model.md` needed a foundational decision before anything else in
it could be written: how does voyage-lang manage the lifetime of
reference-type (`class`/`actor`) instances?

Swift's answer is ARC (automatic reference counting): the compiler inserts
retain/release calls at every reference copy, and an object is deallocated
the instant its count reaches zero. This gives reasonably deterministic
cleanup timing and is central to how idiomatic Swift code is written and
reasoned about — including the `weak`/`unowned` vocabulary that exists
specifically to break ARC retain cycles.

The CLR has no equivalent primitive. It manages reference-type lifetime
with a **tracing garbage collector** — the same GC used by C#, F#, and
every other CLR language, in both standard CoreCLR and NativeAOT (which
embeds the same GC rather than a separate lightweight one; Workstation and
Server GC modes are both available under NativeAOT exactly as under
ordinary CoreCLR). There is no retain/release call for a compiler to emit
against, because the CLR's object model was never designed around
reference counting.

This is structurally the same situation ADR-0006 already worked through
for actor reentrancy: a Swift runtime concept (there, cooperative
actor scheduling; here, ARC) has no CLR equivalent to port to, so the
right question isn't "how do we replicate Swift's exact mechanism" but
"how do we achieve a comparable outcome using what the CLR actually
provides."

## Decision

voyage-lang manages `class`/`actor` reference-type lifetime with the CLR's
own tracing garbage collector. No ARC-style retain/release model is
built on top of it, and none is simulated underneath it.

`struct` remains a value type exactly as in Swift, mapping directly onto
CLR value types (`System.ValueType`) — this half of Swift's value/
reference split transfers to the CLR essentially for free, since both
languages already agree on what a value type is at the semantic level, so
no departure was needed there (see `memory-model.md` Section 1).

For the reference-type half, three consequences follow directly from
choosing tracing GC over ARC, and `memory-model.md` documents mitigations
for each:

1. **Reference cycles are no longer a hazard.** Unlike ARC, a tracing GC
   correctly reclaims cyclic object graphs with no live external
   references, with no `weak`/`unowned` bookkeeping required to avoid a
   leak. `unowned` specifically is dropped from voyage-lang entirely
   (`memory-model.md` Section 7) — it existed in Swift as a
   cheaper-than-`weak` unchecked reference, a performance concern specific
   to ARC's weak-reference table that has no analogue under a tracing GC.
   `weak` is retained, but repurposed: not for cycle-breaking, but for the
   ordinary tracing-GC use case of not unintentionally keeping an object
   alive (caches, event subscriptions).
2. **Collection timing is no longer (roughly) deterministic.** An object
   with no live references becomes *eligible* for collection, not
   immediately reclaimed. `memory-model.md` Section 3 introduces
   `Disposable`/`using` — deliberately shaped like C#'s
   `IDisposable`/`using` rather than a novel mechanism, both because it's
   a well-understood solved problem and because it makes voyage-lang
   `Disposable` types `IDisposable`-compatible from C# for free, matching
   ADR-0007's bidirectional-interop-by-construction philosophy.
3. **Ownership tracking's safety rationale doesn't transfer.** Swift's
   (and Rust's) compile-time ownership tracking is motivated in large part
   by needing to prove *when* a value can be safely freed without a
   garbage collector as a fallback. voyage-lang always has that fallback.
   This reaffirms, from the memory-model side, the position `grammar.md`
   Section 9 already took tentatively: `borrowing`/`consuming` are not
   adopted as a safety mechanism, though the door is left open to
   revisiting them purely as a *performance* tool for allocation-sensitive
   NativeAOT hot paths — a different justification than Swift's original
   one, so it would need its own future ADR rather than inheriting this
   one's reasoning.

## Consequences

- **Positive:** Reuses the CLR's own GC — mature, tuned, NativeAOT-
  compatible, with Workstation/Server modes already available — rather
  than building or simulating a second memory-management system
  underneath or alongside it. Directly consistent with how ADR-0006
  reused `ThreadPool`/`Channel<T>` instead of building a custom scheduler.
- **Positive:** Eliminates an entire class of bug (retain cycles) that
  Swift developers have to actively manage, with zero effort from
  voyage-lang developers.
- **Positive:** `Disposable`/`using` gives voyage-lang a real answer to
  the deterministic-cleanup gap non-deterministic collection opens up,
  reusing a pattern (C#'s `IDisposable`) that's already proven at scale
  in the exact ecosystem voyage-lang targets.
- **Negative / accepted trade-off:** Collection timing is genuinely less
  predictable than ARC's reference-count-hits-zero moment. This is not
  mitigated away, only worked around for the specific cases
  (scarce resources) where it matters, via `Disposable`/`using`. General
  memory (non-resource-holding objects) simply isn't reclaimed on a
  predictable schedule, and voyage-lang code should not be written as
  though it is.
- **Follow-up:** The open items already listed in `memory-model.md`
  (enum lowering rule, `using` + `async`/actor interaction, `mutating
  func` semantics, stack-allocation guarantees for `struct`, whether
  finalizers are exposed at all) are tracked there, not resolved by this
  ADR.

## Alternatives Considered

- **Build ARC on top of the CLR** (compiler-inserted retain/release,
  shadowing the CLR's own tracked references) — rejected. This would run
  a second, redundant lifetime-tracking system alongside the GC the CLR
  is already running underneath everything, for no benefit — the GC still
  has to trace the object graph regardless, so ARC's retain/release
  overhead would be pure addition, not a replacement for anything.
- **Abandon the CLR's GC in favor of a from-scratch reference-counted
  runtime** — rejected outright; this would forfeit the entire reason
  voyage-lang targets CIL/.NET in the first place (mature, NativeAOT-
  ready runtime, direct C# interop per ADR-0007) in exchange for
  reinventing memory management from zero, which is a vastly larger
  undertaking than voyage-lang's scope justifies.
- **Adopt Rust/Swift-style ownership as the primary safety mechanism
  anyway**, despite the CLR always having a GC fallback — rejected for the
  reasons in Consequence 3 above; the safety motivation Swift/Rust have
  for ownership doesn't exist in voyage-lang's target environment, so
  importing the mechanism would add real complexity (a whole borrow-
  checking pass in `Voyage.Compiler/Semantics`) without importing the
  problem it exists to solve.
