# ADR-0010: `Voyage.Compiler` Architecture — Hand-Written Recursive Descent, Five-Phase Pipeline

- Status: Accepted
- Date: 2026-08-08
- Related: All prior ADRs (this formalizes the `Parsing/` → `Semantics/` →
  `Lowering/` → `Diagnostics/` phase names already referenced informally
  throughout ADR-0006 and ADR-0008)

## Context

Every ADR so far has referenced `Voyage.Compiler`'s internal phases in
passing — ADR-0006 talks about `Voyage.Compiler/Lowering/` injecting CIL
`ret` instructions, ADR-0008 talks about `Voyage.Compiler/Semantics/`
running the transitive-await crawl and `Voyage.Compiler/Diagnostics/`
halting compilation on a violation — but none of this was ever decided as
its own question. The phase names have been used as if settled without
anyone actually settling them, and no decision has been made on the more
basic question of *how* the parser itself gets built.

This needs resolving before any code is written, consistent with the
spec-before-code discipline that's held for the language design itself
(`grammar.md`, `type-system.md`, `memory-model.md`, ADR-0001 through
ADR-0009). Writing a lexer or parser without this decided first would mean
architecture decisions getting made implicitly, buried in code, instead of
explicitly, with reasoning attached — exactly the failure mode this
project has been avoiding since Nova.

## Decision

### Parser strategy: hand-written recursive descent

`Voyage.Compiler`'s parser is hand-written recursive descent, not
generated from a grammar file by a parser-generator tool (ANTLR, Bison,
or similar).

This directly follows Swift's own precedent, confirmed by inspecting
`swiftlang/swift`'s actual source layout rather than assuming: Swift's
parser lives in `lib/Parse/` as hand-written C++ —
`Parser.cpp`/`ParseExpr.cpp`/`ParseStmt.cpp`/`ParseDecl.cpp`/`Lexer.cpp`
and others, organized by grammar category, not generated from a `.g4` or
similar grammar specification. Since voyage-lang's syntax is itself
Swift-inspired (ADR-0001), inheriting Swift's parsing strategy alongside
its grammar is a natural and low-risk choice, not a separate gamble.

Reasons this is the right choice independent of precedent:

- **Diagnostic quality.** ADR-0008's `atomic{}` violations and ADR-0006's
  actor-isolation diagnostics both depend on precise, path-aware error
  messages (see ADR-0008's example: a multi-line diagnostic showing the
  full transitive call path to a hidden `await`). Hand-written recursive
  descent gives full control over error recovery and message construction
  at every parse point; generated parsers make this meaningfully harder to
  customize well.
- **Grammar instability.** voyage-lang's grammar is still actively
  settling — nine ADRs deep and still has open items (`enum` value/
  reference lowering, `mutating func`, generic monomorphization). A
  grammar-file-driven generator adds a regeneration step and an extra
  layer of indirection every time a grammar detail changes; a hand-written
  parser changes exactly where the change is, directly in source.
- **No new toolchain dependency.** `Voyage.Cli` and `Voyage.Compiler`
  already commit to a pure C#/.NET toolchain (per the original project
  structure and ADR-0007's CLR-metadata-native philosophy). A parser
  generator would be an external dependency pulling against that.

### Pipeline: five phases, matching the names already in use

`Voyage.Compiler` is organized into five phases, each its own
subdirectory/namespace, formalizing what ADR-0006 and ADR-0008 already
assumed existed:

1. **`Lexing/`** — source text → token stream. Fully specified already:
   `grammar.md`'s Appendix (Supported Tokens) is a complete literal
   enumeration of every keyword, operator, punctuation mark, and literal
   form the lexer needs to recognize. This is the one phase with zero
   open spec questions blocking it — see "Where to start" below.
2. **`Parsing/`** — token stream → AST. Registers structural nodes
   like `AtomicStatement` (ADR-0008) as first-class AST node types, not
   generic blocks with a flag — this matters because later phases
   (`Semantics/`) need to find these nodes by type, not by re-inspecting
   syntax.
3. **`Semantics/`** — type checking, the effects/`HasSuspensionPoints`
   pass and transitive-await call-graph crawl from ADR-0008, the
   post-`await` actor-state-mutation diagnostic from ADR-0006, and general
   type inference (`type-system.md` Section 5). This is the phase where
   most of the compile-time-only guarantees this project has been
   designing around actually get enforced.
4. **`Lowering/`** — AST → CIL-ready intermediate form. Implicit-return
   injection (ADR-0005), `atomic{}` flat-nesting flattening (ADR-0008
   Rule 1), and the actual `ret` instruction synthesis all happen here.
5. **`Diagnostics/`** — shared across phases 2–4, not a late final step.
   Every phase reports through this rather than throwing raw exceptions or
   printing directly, so error formatting (like ADR-0008's multi-line
   call-path diagnostic) stays consistent regardless of which phase
   detected the problem.

`CodeGen/` (CIL emission proper, using the final lowered form) is
acknowledged as a real, distinct sixth concern but deliberately not
detailed in this ADR — it's downstream of enough undecided
implementation-level questions (which CIL-emission library/approach,
generic monomorphization strategy already flagged as its own future ADR in
`type-system.md`) that specifying it now would be premature relative to
what's actually been designed.

### Where to start

Given the phase list above, `Lexing/` is the correct first implementation
target, and within it, the correct first milestone is round-tripping the
corrected hello world sample (`print("Hello, Voyage.")`, per ADR-0003)
through `Lexing/` → a minimal `Parsing/` slice (just enough to parse a
top-level call-expression statement) → an AST dump, with no `Semantics/`,
`Lowering/`, or `CodeGen/` involved yet. This mirrors how Swift's own
compiler and most real-world compilers bootstrap: prove the pipeline's
skeleton end-to-end on the simplest possible program before adding any
complex language feature (`struct`/`actor`/generics), rather than
attempting the full grammar at once.

## Consequences

- **Positive:** Every future implementation decision has an explicit home
  — "which phase does this belong in" is now an answered question, not
  something each contributor decides ad hoc per feature.
- **Positive:** Hand-written recursive descent keeps diagnostic quality
  fully within `Voyage.Compiler`'s control, which several already-accepted
  ADRs (0006, 0008) depend on for their compile-time-guarantee stories to
  actually be usable rather than just theoretically correct.
- **Negative / accepted trade-off:** Hand-written parsers are more code to
  write and maintain than a grammar file plus generated output, and don't
  get automatic grammar-ambiguity detection a generator would provide.
  Accepted as the right trade given the diagnostic-quality and
  grammar-instability reasons above.
- **Follow-up:** `CodeGen/` architecture is intentionally deferred to its
  own future ADR, as is the generic monomorphization strategy already
  flagged in `type-system.md`'s open items — both are real decisions, just
  not ones this ADR is positioned to make yet.

## Alternatives Considered

- **Parser generator (ANTLR, Bison, or similar) driven by a `.g4`/grammar
  file** — rejected for the diagnostic-quality and grammar-instability
  reasons above. Revisit only if the grammar reaches a genuinely stable
  v1.0 and hand-written parser maintenance becomes the bottleneck — not
  the case now.
- **Combine `Semantics/` and `Lowering/` into a single phase** — rejected;
  keeping them separate matches how ADR-0006 and ADR-0008 already describe
  the work (semantic checks that can *reject* a program vs. lowering that
  only runs once a program is already known-valid), and conflating them
  would blur where a compile-time-only guarantee is actually enforced
  versus where it's merely assumed already true.
