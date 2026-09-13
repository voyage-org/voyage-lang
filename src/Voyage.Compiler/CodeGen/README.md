# CodeGen/

Implements IR → CIL emission for ADR-0011's minimal pipeline subset, per
ADR-0012's architecture decisions. Consumes the same
`Semantics.BoundCompilationUnit` type `Lowering/` hands back — no
separate codegen-IR (same "no parallel tree" philosophy `Lowering/`
already established).

**This is the actual payoff milestone ADR-0011 set out to reach**: a
`.voy` program can now be compiled and genuinely run, either in-process
or as a real, independently loadable `.dll` on disk. Verified
end-to-end in tests — not just "the IL looks plausible," but compiled,
invoked via reflection, and checked against actual computed results
(arithmetic, struct construction/property access, enum construction/
pattern matching, loops, compound assignment, string interpolation,
short-circuit `&&`/`||`, value-correct string equality, and a genuinely
persisted-then-reloaded assembly).

## Files

- **`CodeGenContext.cs`** — per-compilation state: every
  symbol-to-Reflection.Emit-builder mapping (struct/enum `TypeBuilder`s
  and their fields/constructors/factories, function `MethodBuilder`s),
  plus `ResolveClrType` (`TypeSymbol` → CLR `Type`, per
  `type-system.md` Section 6 and ADR-0012's struct/enum decision).
- **`MethodEmitContext.cs`** — per-function state: `VariableSymbol` →
  storage (`ArgumentStorage`/`LocalStorage` — see its remarks for why
  this distinction can't be recovered from the bound tree itself), and
  the loop-label stack `break`/`continue` resolve against.
- **`TypeEmitter.cs`** — builds every struct/enum's CLR type in three
  passes (shells, bodies, bake), mirroring `DeclarationBinder`'s own
  two-step struct/enum resolution. Implements ADR-0012's enum layout
  (`int Tag` + one field per case/slot, no union overlap) and emits a
  static per-case factory method for each enum case.
- **`ExpressionEmitter.cs`** — IL emission for every `BoundExpression`/
  `LoweredAst` kind. Notable pieces: `EmitAddress` (address-of for
  assignment targets, recursing through a property-access chain),
  short-circuit `&&`/`||` (branching, not eager evaluation), and
  value-correct `String` equality (`string.op_Equality`, not raw
  `ceq`'s reference-identity comparison).
- **`StatementEmitter.cs`** — IL emission for every `BoundStatement`
  kind, including the compound-assignment (`+=` etc.) read-modify-write
  expansion `Lowering/` doesn't do (see `Lowering/README.md`'s scope).
- **`CodeGenerator.cs`** — the public entry point. `EmitInMemory`
  (fast, in-process, what tests use) and `EmitToFile` (a genuinely
  persisted, independently runnable assembly, per ADR-0012 Decision 1)
  share every emission call above; only the final assembly-construction
  step differs.

Notably, **`CodeGenerator` takes no `IDiagnosticSink`** — by design. A
`BoundCompilationUnit` reaching this phase is assumed already fully
valid (any `Semantics/`/`Lowering/` diagnostic means the caller should
stop before ever calling here). Anything unexpected inside `CodeGen/`
itself throws rather than reporting a diagnostic — it means an
internal-consistency bug in an earlier phase or in this one, not
malformed-but-otherwise-valid user input.

## Real IL details worth knowing (verified against the actual runtime before relying on them)

- `ldfld` works directly on a value-type instance already on the
  evaluation stack (no address needed) — used for every ordinary
  property/tag/slot *read*. A *write* (an assignment target, or the
  read half of a compound assignment) needs the address instead
  (`ldloca`/`ldarga`/`ldflda`), which is what `EmitAddress` computes.
- `String == String` cannot be raw `ceq` — that compares reference
  identity, not the value equality voyage-lang's `==` means. Calling
  `string.op_Equality`/`op_Inequality` directly reuses C#'s own
  already-correct semantics rather than reimplementing string
  comparison here.
- `&&`/`||` must branch, not eagerly evaluate both operands — the right
  operand may have side effects (or, per this phase's own test suite,
  a division that would otherwise throw) that must not run when
  short-circuiting applies.
- **A loaded persisted assembly is file-locked for the process's
  lifetime on Windows** (not on Linux/macOS) — `Assembly.LoadFile`
  memory-maps the `.dll`, and Windows won't allow deleting an open
  file. Encountered for real: the test suite's own cleanup of its
  persisted-assembly test's temp file threw
  `UnauthorizedAccessException` on Windows despite passing cleanly on
  Linux, fixed by making that cleanup best-effort (see the test itself
  for the fix). Worth keeping in mind for the future `voyage` CLI's
  `build` command too — anything that loads its own freshly-emitted
  output for verification, then wants to overwrite or delete it in the
  same process, will hit this on Windows.

## What's still open

- **Enum case construction has no source syntax or Semantics/ binding
  rule yet.** Every enum's static per-case factory method is emitted
  regardless (see `TypeEmitter`'s remarks) and is exercised in tests by
  invoking it directly via reflection, but no voyage-lang *source*
  program can currently produce an enum value — only match on one
  received as a parameter. This is ADR-0012's own discovered
  Consequence, not resolved here; needs a `Parsing/`+`Semantics/`
  follow-up (construction syntax, then a binding rule for it).
- **Duplicate enum case names aren't rejected by `Semantics/`.**
  `TypeEmitter` throws defensively if two cases share a name (which
  would otherwise silently collide on the generated factory-method
  name), but the real fix belongs in `DeclarationBinder`.
- **The one-field-per-slot enum layout is not space-efficient** — an
  accepted ADR-0012 trade-off, not a bug. A real union-style layout
  (restricted to blittable-only cases, where the CLR's overlap
  restriction doesn't apply) is a reasonable future optimization.
- **A non-exhaustive `switch` with no `default` can produce an
  `InvalidProgramException` at runtime, not a compile-time diagnostic.**
  This is `Lowering/`'s already-documented open gap
  (`Lowering/README.md`: "no switch exhaustiveness checking") surfacing
  concretely here: the desugared `BoundIf` chain's final empty `else`
  falls through to `CodeGen/`'s mandatory trailing `ret`, which has
  nothing to return for a non-`Void` function. Encountered for real
  while writing this phase's own tests — the fix (real exhaustiveness
  checking) belongs in `Semantics/`, not here; in the meantime, source
  should always add a `default` clause when case coverage isn't
  provably exhaustive, same as real Swift requires.
- **No definite-assignment checking** — a `let`/`var` declared with a
  type but no initializer (e.g. `var x: Int`) is left at its CLR
  default (`0`/`false`/`null`) rather than genuinely flagged as
  possibly-unread-before-assignment. An open `Semantics/` gap, not
  introduced here.
- **Generic specialization/monomorphization** — still explicitly
  deferred to its own future ADR (per ADR-0010's original deferral and
  `type-system.md`'s open items); out of ADR-0011's minimal subset
  regardless.

Depends on `Lowering/` having already produced a desugared bound tree
with no `BoundSwitch` nodes remaining.
