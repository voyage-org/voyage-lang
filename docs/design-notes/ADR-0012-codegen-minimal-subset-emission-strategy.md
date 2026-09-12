# ADR-0012: CodeGen/ Minimal-Subset Emission Strategy

- Status: Accepted
- Date: 2026-09-12
- Related: ADR-0009 (struct/class → CLR value/reference type mapping;
  leaves `enum` unresolved, tracked as an open item in
  `memory-model.md`); ADR-0010 (deferred `CodeGen/`'s architecture as
  premature); ADR-0011 (minimal pipeline scope — this ADR covers exactly
  that subset, nothing more)

## Context

`CodeGen/README.md` has said "not yet implemented, and deliberately not
yet designed" since ADR-0010, which explicitly deferred this phase's
architecture. With `Semantics/` and `Lowering/` now implemented for
ADR-0011's minimal subset, three concrete questions block starting
`CodeGen/` at all:

1. **How does an emitted assembly reach the outside world?** Modern
   .NET (5+) removed `AssemblyBuilder.Save()` — a dynamically built
   assembly can be run in-process via reflection, but there was no
   documented-in-this-repo answer for whether a genuinely runnable
   `.dll`/`.exe` file could still be produced, or how.
2. **What CLR type does a voyage-lang `enum` with associated values
   lower to?** `memory-model.md` Section 1 already states that an enum
   "follows whichever kind its associated data implies at the storage
   level" but flags the precise rule as an open item (Section "Open
   Items for Next Pass"). ADR-0009 resolved this question for `struct`/
   `class`/`actor` but explicitly left `enum` to a future decision.
3. **How does `Lowering/`'s `LoweredEnumTagCheck`/
   `LoweredAssociatedValueAccess` (see `Lowering/README.md`) map onto
   whatever that CLR type turns out to be?** These two node kinds were
   deliberately left abstract in `Lowering/` for exactly this ADR to
   give concrete meaning to.

Rather than reasoning about these from memory, each was verified
directly against the actual installed `dotnet-sdk-10.0` before deciding
anything (see Decision below for what each check found):

- Whether `System.Reflection.Emit.PersistedAssemblyBuilder` exists and
  produces a genuinely loadable, runnable assembly on this SDK, not just
  an in-memory one.
- Whether the CLR permits overlapping a managed-reference-typed field
  with a value-typed field at the same offset in an explicit-layout
  struct — the concrete mechanism a C-union-style, space-efficient enum
  representation would need.

## Decision

### 1. Emission target: `PersistedAssemblyBuilder`, producing a real runnable file

`System.Reflection.Emit.PersistedAssemblyBuilder` (new as of .NET 9,
confirmed present and functional on the installed `dotnet-sdk-10.0`) is
used for `CodeGen/`'s output. Verified end-to-end: a
`PersistedAssemblyBuilder`-built assembly written via `.Save(path)`
loads back with `Assembly.LoadFile` **in a separate process** and its
emitted `Main` method runs correctly. This means `voyagec` can produce
an actual `.dll` a person can hand to `dotnet run` or reference from
other .NET code — a real compiler output, not just an in-process demo.

For test-harness purposes (verifying emitted IL is correct without a
subprocess round-trip on every test), the ordinary
`AssemblyBuilder.DefineDynamicAssembly(..., AssemblyBuilderAccess.Run)`
path — also verified working — remains available as a faster in-process
execution mode. Both paths share the same `TypeBuilder`/`MethodBuilder`/
`ILGenerator` emission code; only the final assembly-construction call
differs. `CodeGen/` should expose both, defaulting the test suite to the
faster in-process path and reserving the persisted path for what the
future `voyage` CLI's `build` command actually needs.

### 2. Enum representation: a struct with an integer tag and one field per (case, slot) — no field overlap

Every voyage-lang `enum`, regardless of what its associated values'
types are, lowers to a CLR `struct` (`System.ValueType`). This resolves
`memory-model.md`'s open item: a struct field being a reference type
(e.g. an associated `String` value) does not make the *containing*
struct a reference type — this is the same principle ADR-0009 already
established for `class`/`actor` fields living inside an ordinary
`struct`, just applied to `enum` explicitly for the first time. So the
uncertainty `memory-model.md` flagged turns out to have a uniform
answer: **`enum` is always a value type**, full stop, with no
case-by-case split needed after all.

Layout: one `int` `Tag` field identifying the active case, plus one
field per unique `(case, associated-value-slot)` pair — e.g.
`Shape.circle(radius: Double)` and `Shape.square(side: Double)` produce
two backing fields (`_case0_slot0: double`, `_case1_slot0: double`), not
one shared/overlapping field. This is **not** the space-efficient
choice (a C-union-style layout, where every case's slots share the same
backing storage, wastes far less space) — it's the deliberately safe
one, confirmed necessary by directly testing the alternative: building a
CLR struct with an explicit-layout field overlap between a managed
reference-typed field and a value-typed field at the same offset throws
`TypeLoadException` ("contains an object field at offset 0 that is
incorrectly aligned or overlapped by a non-object field"). Since
voyage-lang associated values can be any type, including reference
types (`String` already; `class`/`actor` instances once those phases
exist), a real union layout would need to special-case which slots are
safe to overlap and which aren't — real, but genuinely more work than
ADR-0011's minimal-subset scope justifies. One field per slot is
trivially safe for every type and needs no such case analysis.

`LoweredEnumTagCheck(Subject, Case)` compiles to a `Tag == <case's
integer index>` comparison. `LoweredAssociatedValueAccess(Subject,
Case, SlotIndex)` compiles to reading that case's own dedicated field
for that slot — never the shared/overlapping read a union layout would
need.

Enum case *construction* (e.g. producing a `Shape` value in the first
place, as opposed to matching one) needs a static factory method per
case on the generated struct (constructor overloading can't
disambiguate by case name — only by parameter types, which can collide
across cases). This ADR specifies that factory-method shape as part of
the enum's CLR representation, but implementing it surfaces a real gap
described in Consequences below.

### 3. Struct/function emission: direct `TypeBuilder`/`MethodBuilder` walk over the lowered tree, no separate codegen-IR

Consistent with `Lowering/`'s own decision not to introduce a parallel
IR hierarchy (see `Lowering/README.md`), `CodeGen/` walks the
`Semantics.BoundCompilationUnit` that `Lowering/` hands back directly,
emitting `ILGenerator` instructions per node kind rather than lowering
further into some third tree representation first. A `struct`
declaration becomes a `TypeBuilder` with one `FieldBuilder` per stored
property (plus a generated memberwise constructor, mirroring
`Semantics.Binder`'s already-implicit positional-memberwise-
construction rule — see `Symbols.cs`'s `StructSymbol` remarks); a
function becomes a `MethodBuilder` with its body emitted by walking
`BoundStatement`s in order.

## Consequences

- **Positive:** `voyagec` can produce a real, independently runnable
  `.dll` — verified working end-to-end on the actual target SDK, not
  assumed from documentation that predates this repo's dependency on
  .NET 10 specifically.
- **Positive:** Resolves a `memory-model.md` open item that predates
  `CodeGen/` entirely — `enum` lowering is now specified, not just
  `struct`/`class`/`actor`.
- **Positive:** The one-field-per-slot enum layout needs no per-type
  safety analysis (which associated-value types are "safe" to overlap)
  — every voyage-lang type, present or future, works uniformly.
- **Negative / accepted trade-off:** The one-field-per-slot layout is
  not space-efficient — an enum with many cases each holding
  similarly-sized data wastes memory proportional to the number of
  cases, unlike a true tagged union. Revisiting this for a real
  union-style layout (restricted to cases whose associated values are
  all unmanaged/blittable, where the `TypeLoadException` constraint
  above doesn't apply) is a reasonable future optimization once the
  minimal pipeline is running end-to-end — not a correctness requirement
  now.
- **Negative / newly discovered gap, not resolved by this ADR:**
  Writing out the enum-construction factory-method design surfaced that
  `Semantics.Binder` currently has **no path at all** for binding an
  enum-case *construction* expression — `BindCall` only recognizes a
  plain identifier callee resolving to a `FunctionSymbol` or
  `StructSymbol` (see `Binder.cs`'s `BindCall`), and
  `BindMemberAccess`/`BindIdentifier` only ever produce a value for a
  `VariableSymbol` or a struct property read. A source expression like
  `Shape.circle(radius: 5.0)` (or whatever the eventual construction
  syntax is — not yet specified anywhere in `grammar.md` either) cannot
  bind today. Every existing `Semantics/`/`Lowering/` test that matches
  an enum case does so against a *parameter* already typed as that
  enum, never against a freshly constructed value — so this gap was
  invisible until `CodeGen/`'s factory-method design made it concrete.
  This needs a `Semantics/`/possibly `Parsing/` follow-up (both the
  construction syntax and its binding rule) before `CodeGen/` can be
  exercised end-to-end on a program that actually produces enum values,
  not just matches on ones handed in as parameters.
- **Follow-up:** Implementation order should be struct/primitive
  emission and function bodies first (fully unblocked today), with enum
  construction's `CodeGen/` half implementable in parallel — but its
  end-to-end test coverage blocked until the `Semantics/` gap above is
  closed.

## Alternatives Considered

- **In-process-only emission (`AssemblyBuilderAccess.Run`, no
  `.Save()`)** — rejected as the *sole* target once
  `PersistedAssemblyBuilder` was confirmed to work on the actual
  installed SDK; an in-process-only compiler would mean `voyagec` could
  never produce a file a person could actually keep or hand to someone
  else, which undercuts calling it a compiler at all. Kept as a
  secondary fast path for testing, per the Decision above, rather than
  discarded — it's still the right tool for a test suite that shouldn't
  pay subprocess-round-trip cost on every check.
- **Emit an IL text file and shell out to `ilasm`** — rejected, as
  already noted when this question first came up during the earlier
  minimal-pipeline discussion: adds an external toolchain dependency
  this project doesn't otherwise need, and `PersistedAssemblyBuilder`
  now provides the same "real file on disk" outcome without one.
  Superseded by this ADR's Decision 1.
- **True union-style enum layout from the start** (explicit-layout
  overlapping fields, restricted to blittable-only cases as a v0.1
  limitation) — considered and rejected for now: it would mean
  associated values containing any reference type (already true for
  `String`) couldn't use the "efficient" path at all, so `CodeGen/`
  would need two different enum-layout strategies (overlapping for
  blittable-only cases, one-field-per-slot for everything else) rather
  than one uniform rule. Adds real complexity for a space optimization
  ADR-0011's minimal-subset scope doesn't yet need.
- **Class-hierarchy enum representation** (an abstract base class per
  enum, one subclass per case, matching how a discriminated union is
  sometimes modeled in idiomatic C#) — rejected: this would make every
  voyage-lang `enum` a reference type, contradicting the value-type
  answer this ADR gives to `memory-model.md`'s open item, and would
  mean every enum value allocates on the heap even for a case as simple
  as `Optional<Int>.none` — a real behavioral departure from Swift's own
  (value-type) enum semantics that ADR-0009's value/reference-type
  philosophy was explicitly trying to preserve.
