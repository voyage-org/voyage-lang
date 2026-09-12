# CodeGen/

Not yet implemented. Architecture is now decided (ADR-0012) — this
directory is ready for implementation to begin.

IR → CIL emission, consuming the same `Semantics.BoundCompilationUnit`
type `Lowering/` hands back (no separate codegen-IR — see
`Lowering/README.md`'s design note, which this phase follows too).

## Decided (ADR-0012)

- **Emission target**: `System.Reflection.Emit.PersistedAssemblyBuilder`
  for real, independently runnable `.dll` output — verified end-to-end
  on the actual installed SDK (built, saved, reloaded in a fresh
  process, and its emitted `Main` ran correctly). An in-process
  `AssemblyBuilder.DefineDynamicAssembly(..., AssemblyBuilderAccess.Run)`
  path is also available and is what the test suite should default to,
  reserving the persisted path for what the `voyage` CLI's future
  `build` command needs.
- **Enum representation**: every `enum` lowers to a CLR `struct` with an
  `int Tag` field plus one field per `(case, associated-value-slot)`
  pair — no union-style field overlap. Confirmed necessary, not just
  simpler: the CLR throws `TypeLoadException` on an explicit-layout
  struct that overlaps a managed-reference-typed field with a
  value-typed one, and voyage-lang associated values can be any type.
  `Lowering/`'s `LoweredEnumTagCheck` compiles to a `Tag ==` comparison;
  `LoweredAssociatedValueAccess` reads that case's own dedicated field.
- **Struct/function emission**: direct `TypeBuilder`/`MethodBuilder`/
  `ILGenerator` walk over the bound tree — a `struct` becomes a
  `TypeBuilder` with one `FieldBuilder` per stored property plus a
  generated memberwise constructor; a function becomes a `MethodBuilder`
  with its body emitted by walking `BoundStatement`s in order.

See ADR-0012 for the full reasoning, the alternatives considered, and
consequences (including a real gap it surfaced but doesn't resolve —
see below).

## What's still open

- **Generic specialization/monomorphization strategy** — still
  explicitly deferred to its own future ADR (per ADR-0010's original
  deferral and `type-system.md`'s open items), since generics
  instantiation is outside ADR-0011's minimal subset regardless.
- **Enum case construction can't be exercised end-to-end yet.** Writing
  ADR-0012's enum factory-method design surfaced that `Semantics.Binder`
  has no path at all for binding an enum-case *construction* expression
  (e.g. something like `Shape.circle(radius: 5.0)`) — only pattern
  matching against an already-typed value works today. Every existing
  `Semantics/`/`Lowering/` test matches an enum case against a function
  *parameter* already typed as that enum, never against a freshly
  constructed value. `CodeGen/`'s enum-construction factory methods can
  still be implemented against ADR-0012's design, but a real
  construct-then-match test needs a `Semantics/` (and possibly
  `Parsing/`, since the construction syntax itself isn't specified in
  `grammar.md` either) follow-up first.
- **Space-inefficient enum layout** — the one-field-per-slot approach
  is deliberately not space-efficient (see ADR-0012's accepted
  trade-off). A real union-style layout, restricted to cases whose
  associated values are all unmanaged/blittable, is a reasonable future
  optimization once the minimal pipeline runs end-to-end.

Depends on `Lowering/` having already produced a desugared bound tree
with no `BoundSwitch` nodes remaining.
