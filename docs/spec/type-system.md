# voyage-lang Type System (Draft v0.1)

Status: draft — first pass, not yet implemented in `Voyage.Compiler`.

This document specifies voyage-lang's type system: nominal typing rules,
generics (including the previously-untouched associated-type and
protocol-composition corners), the `Optional<T>`/`?` model, and existential
vs. opaque protocol types. It complements `grammar.md` (declaration syntax)
and `memory-model.md` (value vs. reference semantics, which this document
assumes and does not repeat).

---

## 1. Kinds of Types

- **Nominal types**: `struct`, `enum`, `class`, `actor`, `protocol` — each
  introduces a named type or type constraint. See `grammar.md` Sections
  4–5 for declaration syntax, `memory-model.md` Section 1 for value vs.
  reference semantics.
- **Function types**: `(ParamTypes) -> ReturnType`, with `async` and
  `throws(ErrorType)` as part of the function type itself, not bolted on
  separately:

  ```voyage
  let op: (Int, Int) -> Int
  let fetch: (URL) async throws(NetworkError) -> Data
  ```

- **Tuple types**: `(Int, String)`, structural rather than nominal —
  two tuple types with the same element types and order are the same
  type, no declaration required.
- **Optional types**: `T?`, sugar over a built-in `Optional<T>` — see
  Section 3.

## 2. Generics

### Type parameters and constraints

```voyage
func firstMatch<T>(_ items: [T], predicate: (T) -> Bool) -> T? {
    for item in items {
        if predicate(item) {
            return item
        }
    }
    return nil
}

func merge<T>(_ a: [T], _ b: [T]) -> [T] where T: Equatable {
    // ...
}
```

Constraint syntax matches Swift: `<T: ProtocolName>` inline, or a trailing
`where` clause for more complex or multiple constraints. Multiple
constraints on one parameter combine with `&` (protocol composition, see
Section 4) or multiple `where` clauses joined by `,`.

### Associated types

`protocol` declarations may declare an `associatedtype` — a placeholder
type filled in by each conforming type, rather than a type parameter
supplied by the caller:

```voyage
protocol Container {
    associatedtype Element
    func append(_ item: Element)
    var count: Int { get }
}

struct Stack<T>: Container {
    var items: [T] = []
    func append(_ item: T) { items.append(item) }
    var count: Int { items.count }
    // Element is inferred as T from the append(_:) signature —
    // no explicit `typealias Element = T` required when inferable.
}
```

This is voyage-lang's version of what's often called a **protocol with
associated types (PAT)**. PATs are the trickiest corner of Swift's type
system, and voyage-lang inherits the same core restriction Swift has, for
the same underlying reason: a protocol with an `associatedtype` (or a `Self`
requirement) **cannot be used as a standalone existential type** the way an
associated-type-free protocol can. `var x: Container` is **not** valid,
because the compiler would have no concrete `Element` to reason about at
that use site. See Section 4 for how voyage-lang works around this via
opaque types and generic constraints — the same two escape hatches Swift
provides.

### Variance

Generic type parameters are **invariant by default**: `Container<Dog>` is
not assignable to a `Container<Animal>`-typed location even if `Dog:
Animal`, matching Swift's default and unlike, e.g., Java/Kotlin's more
permissive array covariance (which is itself a well-known source of
runtime type-safety holes in those languages — deliberately not repeated
here).

Function types are the one place variance applies implicitly, and again
matches Swift: a function type is covariant in its return type and
contravariant in its parameter types, enforced structurally rather than
through explicit variance annotations (no `in`/`out` parameter-position
markers as in C#/Kotlin generics). Explicit user-declared variance
annotations on generic type parameters are **not adopted** — this is a
deliberate simplification versus C#'s `in`/`out` generic variance markers,
consistent with the general principle of not importing complexity the
CLR technically supports but Swift's own design chose to avoid.

## 3. `Optional<T>`

`T?` is sugar for a built-in generic `Optional<T>` enum with two cases,
conceptually:

```voyage
enum Optional<T> {
    case some(T)
    case none
}
```

`nil` is the literal for `.none`, usable wherever an `Optional<T>` is
expected regardless of `T`, per the `nil` literal already listed in the
`grammar.md` token appendix. `if let`/`guard let` (grammar.md Section 6)
are the primary unwrapping idiom; `??` (nil-coalescing) and postfix `!`
(force-unwrap, a runtime-checked crash-on-`nil` operation, used sparingly)
round out the operator-level handling already listed in the token
appendix.

### CLR interop note

`Optional<T>` for reference types (`class`/`actor`) lowers to an ordinary
nullable reference at the CLR level — a `class`-typed `T?` and a bare
reference-typed `T` share the same underlying null-check mechanism the CLR
already has, so this costs nothing extra. `Optional<T>` for value types
(`struct`) lowers to the CLR's own `System.Nullable<T>` where `T` is a CLR
value type, reusing an existing CLR mechanism rather than voyage-lang
inventing a parallel one. This mapping needs confirmation against
`Voyage.Compiler/Lowering/` once implementation begins, but is the
natural default given both halves already exist on the CLR.

## 4. Existential and Opaque Protocol Types

Two distinct ways to say "a value that conforms to protocol `P`, without
naming the concrete type" — voyage-lang adopts both, matching current
Swift exactly rather than Swift's older, more ambiguous pre-`any`/`some`
model:

### `any P` — existential types

A boxed value of *some* concrete type conforming to `P`, decided at
runtime; the concrete type may vary between different `any P` values.

```voyage
func render(shapes: [any Drawable]) {
    for shape in shapes {
        print(shape.draw())   // dynamic dispatch through the protocol witness
    }
}
```

`any P` requires `P` to have no `associatedtype`/`Self` requirements it
can't erase, same restriction as Section 2 — a `Container` with an
`associatedtype Element` cannot be spelled `any Container` without further
qualification (a future `any Container<Element == Int>`-style constrained
existential is a plausible future addition, not committed to in this
draft).

### `some P` — opaque return types

A single, fixed concrete type conforming to `P`, decided by the function's
implementation at compile time, but hidden from the caller:

```voyage
func makeStack() -> some Container {
    return Stack<Int>()
}
```

Unlike `any P`, the compiler knows the exact concrete type here — it's
just not exposed to the caller. This makes `some P` both faster (static
dispatch, no boxing/witness-table indirection at the CLR level) and able
to return protocols with associated types, since the concrete type behind
the `some` is fully known at the definition site even though it's opaque
to callers.

### Protocol composition

`&` combines multiple protocol constraints in either position:

```voyage
func describe(item: any Drawable & Comparable) -> String { ... }
func makeThing() -> some Drawable & Equatable { ... }
```

### When to use which

- **`some P`** — default choice for return types. Faster, and works with
  PATs. Use unless callers genuinely need to hold values of *differing*
  concrete types behind the same protocol.
- **`any P`** — for heterogeneous collections (`[any Drawable]` holding
  mixed concrete shapes) or anywhere the concrete type must vary at
  runtime. Costs a CLR-level boxing/witness-table indirection for value
  types; free for reference types, which already carry a vtable-style
  dispatch mechanism at the CLR level.

## 5. Type Inference

voyage-lang infers types for `let`/`var` bindings without an explicit
annotation, from the initializer expression, matching Swift:

```voyage
let x = 42          // inferred: Int
let name = "Nehal"  // inferred: String
var items = [Int]() // inferred: [Int]
```

Function return types are **not** inferred from the body and must be
declared explicitly (no `func f() { 42 }`-style implicit signature
inference) — this is a deliberate divergence from full Hindley–Milner-
style inference (which some languages support), matching Swift's own
choice to keep function *signatures* explicit even though implicit return
of the *last expression's value* is separately supported (grammar.md
Section 3, ADR-0005). Signature-level inference and body-level implicit
return are different features; voyage-lang adopts the second without the
first, exactly as Swift does.

## 6. Primitive Type → CLR Mapping

| voyage-lang | CLR type |
|---|---|
| `Int` | `System.Int64` (64-bit by default, matching Swift's `Int` being pointer-width/64-bit on all voyage-lang tier-1 targets) |
| `Int32` / `Int64` | `System.Int32` / `System.Int64` (explicit-width escape hatches) |
| `Double` | `System.Double` |
| `Float` | `System.Single` |
| `Bool` | `System.Boolean` |
| `String` | `System.String` |
| `Void` / `()` | `System.Void` |
| `[T]` (Array) | a voyage-lang `Array<T>` wrapping `System.Collections.Generic.List<T>` or a direct CLR array, TBD — needs a decision on value semantics (Swift arrays are copy-on-write value types; a raw CLR array is not) before this mapping is final |

The `[T]` row is flagged rather than settled — Swift's `Array<T>` has
copy-on-write value semantics (a `struct` wrapping a shared, lazily-copied
buffer), which has no single obvious CLR equivalent (`List<T>` is a
reference type, a raw `T[]` is fixed-size). This needs its own decision,
tracked below, before `Voyage.Compiler`'s standard collection lowering can
be finalized.

---

## Open Items for Next Pass

- [ ] `Array<T>` / `[T]` value-semantics implementation — copy-on-write
      wrapper over a CLR buffer, vs. a simpler (but semantically
      divergent from Swift) reference-typed collection. Blocks Section 6's
      mapping table from being final.
- [ ] Constrained existentials (`any Container<Element == Int>`-style) —
      not committed to in this draft; Section 4 flags it as plausible
      future work only.
- [ ] `mutating func` semantics for `struct` methods that need to modify
      `self` — referenced from `memory-model.md`'s open items, not yet
      specified here either.
- [ ] Generic specialization/monomorphization strategy for NativeAOT
      (full monomorphization vs. shared generic code, a real CLR-level
      trade-off between binary size and performance) — a `Voyage.Compiler`
      implementation concern, not resolved at the language-design level
      in this document, but worth its own ADR before implementation.
- [ ] Numeric literal type inference defaults (e.g. does an untyped
      integer literal default to `Int`? almost certainly yes, matching
      Swift, but not yet stated as an explicit rule).
