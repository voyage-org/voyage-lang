# Semantics/

Implements name resolution and type checking for ADR-0011's "minimal
pipeline" language subset: unlabeled function calls, `let`/`var`
bindings, `if`/`while`/`break`/`continue`/`return`, stored-property-only
`struct`/`enum`, basic expressions (including string interpolation), and
`switch`/pattern matching over enums. Everything ADR-0011 explicitly
defers past this minimal subset — labeled call arguments, generics
instantiation, attributes, declaration modifiers, `associatedtype`,
computed properties, array literals, `actor`/`task{}`/`atomic{}`,
`throws`/`try`/`catch`, `for`-in — is diagnosed as "not yet supported"
rather than silently accepted or crashing; see each binder's own
diagnostic branches for exactly which construct maps to which message.

## Files

- **`Types.cs`** — the resolved `TypeSymbol` hierarchy (`PrimitiveType`,
  `StructType`, `EnumType`, `FunctionType`, `ErrorType`). Distinct from
  `Parsing.TypeNode`, which is syntax, not semantics — see `Types.cs`'s
  own remarks.
- **`Symbols.cs`** — declared-entity records (`VariableSymbol`,
  `ParameterSymbol`, `FunctionSymbol`, `StructSymbol`, `EnumSymbol`,
  `PropertySymbol`, `EnumCaseSymbol`) that a `Scope` holds and
  `BoundAst.cs` nodes reference.
- **`Scope.cs`** — the lexical name-resolution table, chained
  parent-to-child per block.
- **`BoundAst.cs`** — the type-checked tree `Lowering/` will eventually
  consume: every `BoundExpression` carries a resolved `Type`; every node
  still carries its source `Span`.
- **`TypeResolver.cs`** — turns a `Parsing.TypeNode` into a
  `TypeSymbol` against the global struct/enum table.
- **`DeclarationBinder.cs`** — first pass: resolves every top-level
  function/struct/enum *signature* before any body is bound, so forward
  references (calling a function, or naming a struct, declared later in
  the same file) work regardless of declaration order. See this file's
  own remarks for why that requires two passes rather than one.
- **`Binder.cs`** — second pass: binds each function body's (and any
  top-level) statements/expressions against the already-complete global
  scope from `DeclarationBinder`.
- **`SemanticAnalyzer.cs`** — the public entry point:
  `SemanticAnalyzer.Analyze(CompilationUnit, IDiagnosticSink) ->
  BoundCompilationUnit`, orchestrating both passes above.

## What's still open

- **Struct/enum methods** aren't supported — only stored properties
  (`struct`) and cases (`enum`) resolve; a `func` nested inside either is
  diagnosed as unsupported. Blocked in part on the same computed-property/
  modifier parser gaps `Parsing/README.md` already tracks.
- **No implicit numeric widening** (`Int` to `Double`, etc.) —
  `Binder.IsAssignable` is exact-match-only for now, matching Swift's own
  lack of implicit numeric conversion, but real Swift's explicit
  conversion initializers (`Double(someInt)`) aren't wired up as an
  escape hatch yet either.
- **No switch exhaustiveness checking** — every `switch` is accepted
  regardless of whether every enum case (or a `default`) is covered; this
  was already flagged as a `Semantics/` question by `Parsing.
  SwitchStatement`'s own remarks, and remains open.
- **Generic declarations are ignored, not rejected outright** — a
  generic struct/function's type parameters aren't registered as usable
  types, so a signature that actually references one fails type
  resolution with an "unknown type" diagnostic rather than a clearer
  "generics aren't supported yet" one. Full generics instantiation is
  explicitly out of ADR-0011's minimal subset regardless.
- **The Nova/ADR-0006/ADR-0008 concurrency-safety passes** —
  `HasSuspensionPoints`, the transitive-await crawl for `atomic{}`, and
  the post-`await` actor-state-mutation diagnostic — are not implemented.
  These depend on `actor`/`task{}`/`atomic{}` parsing at all, which
  ADR-0011 defers past the minimal pipeline; they're the natural next
  major addition once the minimal subset broadens that far.

Depends on `Parsing/` producing an AST first (unchanged from before);
`Lowering/` is the next phase downstream, still unimplemented.
