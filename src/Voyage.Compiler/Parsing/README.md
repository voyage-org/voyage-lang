# Parsing/

Hand-written recursive descent (ADR-0010 — no parser generator).
Converts the token stream from `Lexing/` into an AST.

`samples/hello.voy` (`print("Hello, Voyage.")`) parses to:

```
CompilationUnit @ 1:1-2:1
  ExpressionStatement @ 1:1-2:1
    CallExpression @ 1:1-1:24
      callee:
        IdentifierExpression 'print' @ 1:1-1:6
      arguments:
        StringLiteralExpression "Hello, Voyage." @ 1:7-1:23
```

## Current scope

Implemented:
- Top-level expression statements
- `let`/`var` binding statements: `let x = 10`, `var x: Double` (type
  annotation, no initializer — the struct-property shape), or
  `let z: Int = 30` (both). At least one of the annotation or the
  initializer is required.
- Call expressions: `callee(arg, arg, ...)`, including nested calls
- Binary and unary operators, per `grammar.md`'s "Operator Precedence and
  Associativity" table (itself adapted from Swift's real
  `precedencegroup` chain): `||`, `&&`, `==`/`!=`/`<`/`<=`/`>`/`>=`,
  `??`, `+`/`-`, `*`/`/`/`%`, unary `-`/`!`
- Primary expressions: identifiers, string/integer/float/boolean/nil
  literals, parenthesized expressions
- `func` declarations: `func name<T>(a: Int, _ value: T) -> Int
  where T: Equatable { ... }` — params (see parameter labels below), an
  optional `-> Type` return type (omitted means implicit Void), an
  optional `<...>` generic parameter list, an optional trailing `where`
  clause, and a body of ordinary statements. The body itself is
  **optional**: `func draw() -> String` with nothing after it is a
  *protocol requirement* (`FunctionDeclaration.Body == null`), distinct
  from an empty implementation (`func draw() -> String {}`, a real,
  present, zero-statement body).
- Parameter labels: `name: Type` (external label defaults to the same
  value as the internal name, Swift's implicit default), `_ name: Type`
  (external label suppressed — `Parameter.ExternalLabel == null`), or
  `external internal: Type` (an explicit, distinct external label).
  Disambiguated via one token of lookahead.
- `return` statements, with or without a value
- Minimal type references for param/return/annotation types: a bare
  identifier with an optional trailing `?` (`Int`, `String?`) — see below
  for what this deliberately excludes
- `if`/`else`/`else-if` — `else if` desugars to a single-element else
  branch holding a nested `IfStatement` (the standard desugaring, so
  `Semantics/`/`Lowering/` don't need a separate "else-if" concept).
  `else` must directly follow the `if` body's closing `}` with no
  newline in between; `else` on its own line isn't yet supported.
- `while` loops
- Bare `break`/`continue` (no loop labels — voyage-lang doesn't have
  loop labels yet)
- `struct`/`enum`/`protocol`/`extension` declarations, all sharing the
  same infrastructure:
  - Bodies reuse the exact same block-statement machinery as function
    bodies, so `var`/`let` properties and `func` methods parse inside
    them automatically with no new member-parsing infrastructure.
  - An optional `<T, U: Protocol>` generic parameter list after the
    name (`struct Stack<T>`), an optional `: A, B` conformance/
    inheritance clause after that (`struct Point: Drawable, Equatable`,
    `protocol P2: P1`, `extension Point: Drawable`), and an optional
    trailing `where` clause before the body (`extension Array where
    Element: Equatable` — note this works with *no* `<...>` list of its
    own, constraining an already-generic extended type).
  - The parser stays permissive about *shape* across all four — e.g. a
    `func` with a real body parses fine inside a `protocol`, and a
    bodyless `func` requirement parses fine inside an `extension`, even
    though neither is semantically sensible. Whether a given member
    belongs in a given declaration kind is a `Semantics/` question, not
    a `Parsing/` one.
- Generics: `<T>` (bare parameter), `<T: Protocol>` (inline constraint),
  `<T: Drawable & Equatable>` (protocol composition via `&`), multiple
  comma-separated parameters (`<K, V>`), and trailing `where T: Protocol`
  clauses — all via one shared `TypeConstraint` node/parse method, since
  an inline constraint and a `where`-clause entry have identical shape.
  `<`/`>` disambiguation from the comparison operators works because
  generic-parameter parsing only ever triggers at declaration call
  sites (right after a name, before `(`/`{`), never inside expression
  parsing — confirmed by a dedicated test that `a < b && c > d` still
  parses as ordinary comparisons.
- `case` declarations inside `enum` bodies, with or without associated
  values (`case circle(radius: Double)`, `case triangle`) — the
  associated-value list reuses the same `Parameter` parsing as function
  parameters, since the two have identical shape. Swift's
  comma-separated multi-case shorthand (`case a, b, c`) isn't supported.
- Assignment statements: `x = 0`, plus the compound forms `+=`, `-=`,
  `*=`, `/=`. `Target` is a full expression, not just an identifier, so
  `self.x = 0`/`items[0] = 0`-style targets become valid automatically
  once member access/subscripting exist — no change needed here when
  that happens. Whether a given `Target` is actually assignable (an
  lvalue) is a `Semantics/` question; `Parsing/` stays permissive about
  shape, same philosophy as everywhere else in this list.

Explicitly **not yet** implemented — each of these produces a clear
diagnostic and a recovery node (`UnsupportedStatement`/`ErrorExpression`)
rather than a crash or silently-dropped content, so a file mixing
supported and unsupported constructs still parses as far as it can:
- Generic *type arguments at use sites* (`Stack<Int>`, `Array<T>`) —
  generic parameter *declarations* (`struct Stack<T>`) are supported
  (above), but referencing a generic type with concrete/generic
  arguments filled in is part of the richer-type-syntax gap below.
- Richer type syntax generally: generic type arguments (`Array<T>`),
  array sugar (`[T]`), function types (`(Int) -> String`), and
  keyword-spelled type forms (`Self`, `any P`, `some P`) — only
  bare-identifier types (plus trailing `?`) are parsed so far. This
  means `type-system.md`'s own generic examples using `[T]`/`(T) ->
  Bool` parameter types still don't fully parse end-to-end yet, even
  though the `<T>`/`where` machinery around them now does.
- `if let`/`if case` conditional binding forms — only a plain
  boolean-valued condition expression is recognized
- `switch`, `for`-`in`, `guard`, `repeat`-`while`
- `actor`
- String interpolation (`"\(...)"`)
- Member access (`.`), subscripting, ternary, `as`-casting
- Range operators (`..<`, `...`) — recognized by the lexer, not yet wired
  into the expression grammar since nothing consumes them yet (`for`-`in`
  doesn't exist)

## Structural node registration

Registers structural nodes like `AtomicStatement` (ADR-0008) as
first-class AST node types so `Semantics/` can find them by type rather
than re-inspecting syntax — not yet needed at this milestone's scope, but
the `AstNode` base type and record-per-construct pattern in `Ast.cs` is
set up to extend this way as later grammar constructs are added.

## Known limitations

**Malformed declarations (and any input hitting an entirely
unrecognized token) can produce multiple redundant diagnostics for a
single error.** Every `Expect()` call that fails reports a diagnostic
without consuming the unexpected token (by design — it lets the *next*
caller decide how to recover), but when several `Expect()`/parse calls
chain together, each one can hit the same still-unconsumed token and
report its own "expected X" error. Confirmed this never causes an
infinite loop — `ParsePrimary`'s default case always advances past an
unrecognized token as the ultimate forward-progress guarantee — so this
is a diagnostic-*quality* issue (noisy output for bad input), not a
correctness or safety one. Worth addressing once error-recovery UX
becomes a priority; not blocking for now since every currently-supported
construct's happy path is unaffected and covered by tests.

## Next milestone

Richer type syntax (`Array<T>`, `[T]`, `(Int) -> String`, `any P`/`some
P`) is arguably the natural follow-up to generics — it's what's now
blocking `type-system.md`'s own generic examples from fully parsing.
Member access (`.`) and subscripting (`[]`) are also a real, practically-
felt gap (nothing can read a struct's own field back yet). Otherwise:
`switch` (a real design question, since meaningful pattern matching needs
`enum` cases to match against, which now exist) → `for`-`in` (needs the
range operators already lexed but not yet wired into any grammar
construct) → error handling (`throws`) → concurrency (`actor`/`task{}`,
saved for last as the most complex slice). No fixed order is binding —
pick whichever construct unblocks the most useful next test case.
