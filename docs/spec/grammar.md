# voyage-lang Grammar (Draft v0.1)

Status: draft — first pass, not yet implemented in `Voyage.Compiler`.
Target: CIL / .NET (CLR), NativeAOT-first, `linux-musl-x64`/`arm64` as tier-1.

This draft's syntax is Swift-inspired, and deliberately differentiated from
Aurelia's syntax, so voyage-lang and Aurelia read as distinct sibling
languages rather than reskins of each other.

---

## 1. Lexical Structure

- Line comments: `// ...`
- Block comments: `/* ... */` (nestable)
- Identifiers: Unicode-aware, `[a-zA-Z_][a-zA-Z0-9_]*` plus extended ID
  characters, Swift-style
- String literals: double-quoted, with interpolation via `\(expr)`
- No semicolons required at end of statement (newline-terminated), semicolons
  optional for same-line multiple statements

```voyage
let greeting = "Hello, \(name)! You are \(age) years old."
```

## 2. Bindings

```voyage
let x = 10          // immutable
var y = 20           // mutable
let z: Int = 30      // explicit type annotation
```

## 3. Functions

Full keyword `func` (not shortened) — matches Swift, deliberately diverges
from Aurelia's `fn`.

```voyage
func add(a: Int, b: Int) -> Int {
    return a + b
}

func greet(name: String) -> String {
    "Hello, \(name)!"   // implicit return — single-expression body
}
```

### Implicit Return

A function, computed property, or subscript whose body is a **single
expression** matching the declared return type omits `return` entirely —
matching Swift's scope exactly (see ADR-0005). `if`/`switch` count as a
single expression when every branch is itself a single expression:

```voyage
func rating(for score: Int) -> String {
    if score > 800 {
        "Excellent"
    } else if score > 500 {
        "Good"
    } else {
        "Needs work"
    }
}
```

Multi-statement bodies always require an explicit `return` — this is a
deliberate scope limit, not a gap to close later. See ADR-0005 for why.

### Entry Point Conventions

voyage-lang supports two entry point styles, matching Swift's split:

**Top-level statements** — for single-file scripts and samples. No
wrapping function required; the file executes top to bottom:

```voyage
print("Hello, Voyage.")
```

**`@main` attribute** — for structured, multi-file programs (the expected
shape for anything built via `Voyage.Cli`). Attaches to a type with a
static `main()`:

```voyage
struct VoyageMain {
    static func main() throws(AppError) {
        print("Hello, Voyage.")
    }
}
```

`@main` composes with typed throws directly, matching the error-handling
model in Section 7 — no separate untyped-`main` special case needed.

Generic functions use `<T>` with optional `where` clauses:

```voyage
func identity<T>(_ value: T) -> T {
    return value
}

func firstMatch<T>(_ items: [T], predicate: (T) -> Bool) -> T? where T: Equatable {
    // ...
}
```

## 4. Structs and Enums

```voyage
struct Point {
    var x: Double
    var y: Double
}

enum Shape {
    case circle(radius: Double)
    case rectangle(width: Double, height: Double)
    case triangle(base: Double, height: Double)
}
```

## 5. Protocols and Extensions

```voyage
protocol Drawable {
    func draw() -> String
}

extension Point: Drawable {
    func draw() -> String {
        "Point(\(x), \(y))"
    }
}
```

## 6. Control Flow

```voyage
if let value = optionalValue {
    // ...
} else {
    // ...
}

guard let value = optionalValue else {
    return
}

switch shape {
case .circle(let radius):
    // ...
case .rectangle(let w, let h):
    // ...
default:
    // ...
}

for item in items {
    // ...
}

while condition {
    // ...
}
```

## 7. Error Handling — Typed Throws with Untyped Fallback

Diverges from Aurelia's `Result<T, E>` pattern. Adopts Swift's typed throws
(`throws(ErrorType)`) for compile-time-checked error types with ergonomic
propagation, **alongside** plain untyped `throws` as a first-class fallback
— not a deprecated escape hatch. See ADR-0004 for the full rationale.

```voyage
enum ConfigError: Error {
    case fileNotFound(path: String)
    case parseFailure(reason: String)
}

// Typed: precise, exhaustive catch, best for a fixed, known error surface
func loadConfig(path: String) throws(ConfigError) -> Config {
    guard fileExists(path) else {
        throw ConfigError.fileNotFound(path: path)
    }
    // ...
}

// Untyped: default choice for app code, libraries with evolving errors,
// or functions that call into several differently-typed throwers
func run() throws {
    let config = try loadConfig(path: "voyage.toml")
    // ...
}

func handle() {
    do {
        try run()
    } catch let error as ConfigError {
        // narrow, typed catch when it's known
    } catch {
        // exhaustive fallback for `any Error`
    }
}
```

Guidance (not enforced by the compiler): reach for typed throws in
performance-critical or embedded-style code paths with a small, fixed error
surface; default to untyped `throws` everywhere else, especially public
library APIs whose error sets may grow — a typed-throws signature change is
a breaking change in a way an untyped one is not.

## 8. Concurrency

`actor` and `task{}` are complementary layers, not competing alternatives —
mirroring how Swift itself separates isolation from structured concurrency.

- **`actor`** — an isolation *boundary*. A type whose mutable state can only
  be touched by one task at a time. About data-race safety, not spawning.
- **`task{}` / `async let`** — structured concurrency *primitives* for
  spawning, grouping, and awaiting child work. Usable both inside and
  outside actors.

```voyage
actor DataStore {
    var cache: [String: Data] = [:]

    func mutate() {
        cache["key"] = someData
    }

    func refresh(urls: [URL]) async throws(NetworkError) {
        task {
            for url in urls {
                spawn {
                    let data = try await network.get(url)
                    self.cache[url.key] = data   // safe: stays inside actor isolation
                }
            }
        }
    }
}

func fetchBoth(a: URL, b: URL) async throws(NetworkError) -> (Data, Data) {
    async let x = network.get(a)
    async let y = network.get(b)
    return (try await x, try await y)
}

func fetchData(cancel: CancelToken) async -> Future<Data> {
    let result = try await network.get(url)
    return result
}
```

Two structured-concurrency shapes, matching Swift's own split:

- **`async let`** — lightweight, for a fixed, known-at-compile-time number
  of concurrent children.
- **`task{ }` / `spawn{ }`** — for a dynamic/unbounded number of children,
  with `join`/`waitForAll` semantics.

`Future<T>` wraps `Task<T>` under the hood (`Task<T>` never surface-exposed).
Built-in `Sender<T>` / `Receiver<T>` channels for message passing. Explicit
`CancelToken` parameters for cancellation, rather than Swift's ambient
cancellation propagation.

## 9. Ownership (tentative — likely deferred)

Swift's `borrowing` / `consuming` contextual keywords are noted but **not
currently adopted**, since voyage-lang targets the CLR, which has no runtime-level
compile-time ownership model comparable to Rust/Swift's. Would require
compiler-enforced static analysis layered on top, similar in spirit to
Aurelia's Neuro-Linear Borrow Analyzer. Deferred to a future ADR if
performance-critical NativeAOT paths need it.

## 10. String Interpolation

```voyage
let name = "Nehal"
let msg = "Welcome, \(name)!"
```

Matches Swift's `\(...)` exactly — diverges from Aurelia's f-string style
(`f"{x:.4f}"`).

---

## Open Items for Next Pass

- [x] ~~Reconcile `actor` vs. `task{}` concurrency model~~ — resolved,
      see Section 8: complementary layers (isolation vs. structured
      concurrency), not either/or.
- [x] ~~Decide implicit-return-of-last-expression~~ — resolved, see
      "Implicit Return" under Section 3 and ADR-0005: adopted, scoped to
      single-expression bodies only (including if/switch-as-expression),
      matching Swift's own deliberate limit.
- [x] ~~Decide on untyped `throws` as fallback alongside typed throws~~ —
      resolved, see Section 7 and ADR-0004: both supported, untyped is the
      default recommendation.
- [ ] Property wrappers / result builders — not yet decided whether
      voyage-lang adopts an equivalent
- [ ] Module/import syntax — not yet drafted
- [ ] Attribute/annotation syntax (voyage-lang equivalent of Aurelia's
      `@differentiable`/`@invariant`, if any) — not yet decided; would be a
      deliberate divergence point rather than a copy of either sibling

---

## Appendix: Supported Tokens (Draft v0.1)

### Keywords

| Category | Tokens |
|---|---|
| Declarations | `func`, `struct`, `enum`, `protocol`, `extension`, `actor`, `case` |
| Bindings | `let`, `var` |
| Types | `Self`, `Any`, `Optional` (`?` sugar) |
| Attributes | `@main` |
| Control flow | `if`, `else`, `guard`, `switch`, `default`, `for`, `in`, `while`, `repeat`, `break`, `continue`, `return`, `fallthrough` |
| Pattern binding | `if let`, `guard let` |
| Error handling | `throw`, `throws`, `try`, `catch`, `do` |
| Concurrency | `async`, `await`, `task`, `spawn`, `join`, `async let` |
| Access control | `public`, `internal`, `private`, `fileprivate` |
| Modifiers | `static`, `mutating`, `final`, `override` |
| Literals | `true`, `false`, `nil` |
| Miscellaneous | `import`, `where`, `as`, `is`, `self`, `super` |

### Operators

| Category | Tokens |
|---|---|
| Arithmetic | `+`, `-`, `*`, `/`, `%` |
| Comparison | `==`, `!=`, `<`, `<=`, `>`, `>=` |
| Logical | `&&`, `\|\|`, `!` |
| Assignment | `=`, `+=`, `-=`, `*=`, `/=` |
| Range | `..<`, `...` |
| Optional handling | `?`, `!`, `??` |
| Type/casting | `:`, `as`, `as?`, `as!` |
| Function/closure | `->`, `in` |
| Member/scope | `.`, `::` (module-qualified, TBD) |

### Punctuation & Grouping

`(` `)` `{` `}` `[` `]` `,` `;` `:` `\(` `)` (interpolation delimiters)

### Literals

- Integer: `42`, `0x2A`, `0b101010`, `0o52`
- Floating-point: `3.14`, `1.0e10`
- String: `"text"`, interpolated `"text \(expr) text"`
- Boolean: `true`, `false`
- Nil/optional: `nil`

### Comments

- Line: `//`
- Block: `/* ... */` (nestable)

This token list is a first pass tied to the constructs already drafted above
(Sections 1–9) and will grow as module/import syntax, attributes, and
property wrappers are decided.