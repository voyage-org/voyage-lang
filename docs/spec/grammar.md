# voyage-lang Grammar (Draft v0.1)

Status: draft — first pass, not yet implemented in `Voyage.Compiler`.
Target: CIL / .NET (CLR), NativeAOT-first, `linux-musl-x64`/`arm64` as tier-1.

This draft's syntax is Swift-inspired, and deliberately differentiated from
Aurelia's syntax, so voyage-lang and Aurelia read as distinct sibling
languages rather than reskins of each other. Type-level details
(generics, associated types, existential/opaque protocol types, primitive-
to-CLR type mapping) are specified separately in `type-system.md`; this
document covers declaration and expression syntax.

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

### Actor Reentrancy Model

`actor` in voyage-lang is **fully reentrant**, matching Swift's design goal
for deadlock-free actor calls, but implemented via a lock-free mailbox loop
suited to the CLR's `ThreadPool`/`IAsyncStateMachine` model rather than a
Swift-style custom scheduler. Full rationale and implementation pattern in
ADR-0006. Key implications for anyone writing actor code:

- State mutated by other messages **can** change between the start and end
  of a method body if that body contains an `await` — actor-local
  invariants are not preserved across suspension points.
- The compiler statically flags `self`-scoped mutable state access after an
  `await` without re-validation (`Voyage.Compiler/Semantics`).
- The idiomatic pattern is: snapshot state into a local before `await`,
  perform the async work independently, then re-validate and apply changes
  inside an `atomic { }` block on resume. `atomic { }` **cannot contain
  `await`** — this is compiler-enforced, not a style guideline — which
  guarantees the block runs as a single uninterrupted unit against actor
  state. See ADR-0006 for the full example and rationale.
- `atomic { }` blocks nest as a no-op (flattened by the lowering phase, no
  real inner boundary), may call other synchronous methods on `self` or
  other actors, and are protected by whole-compilation-unit transitive-
  await detection so a hidden `await` several calls deep in a helper
  function's call graph is still caught at compile time. Calls into
  external (e.g. C#) assemblies are banned by default unless explicitly
  annotated as verified-safe; indirect calls through function
  pointers/delegates/closures are banned entirely, since neither can be
  statically verified. See ADR-0008 for the full rule set and the
  compiler pipeline (`Parsing/` → `Semantics/` → `Diagnostics/`) that
  enforces it.

## 9. Ownership (tentative — likely deferred)

Swift's `borrowing` / `consuming` contextual keywords are noted but **not
currently adopted**, since voyage-lang targets the CLR, which has no runtime-level
compile-time ownership model comparable to Rust/Swift's. Would require
compiler-enforced static analysis layered on top, similar in spirit to
Aurelia's Neuro-Linear Borrow Analyzer. Deferred to a future ADR if
performance-critical NativeAOT paths need it. See `memory-model.md` and
ADR-0009 for the full reasoning: the CLR's tracing GC removes the safety
motivation Swift/Rust ownership exists for, so this stays a possible
future *performance* tool rather than a safety mechanism.

## 10. String Interpolation

```voyage
let name = "Nehal"
let msg = "Welcome, \(name)!"
```

Matches Swift's `\(...)` exactly — diverges from Aurelia's f-string style
(`f"{x:.4f}"`).

---

## 11. Modules and Imports

voyage-lang's import syntax is Swift-inspired at the declaration level, but
resolves against **CLR assembly metadata** rather than requiring a bridging
layer — see ADR-0007 for the full rationale on why this makes C#
interop structurally different (and simpler) than Aurelia's C interop or
Swift's Clang-header interop.

### Basic imports

```voyage
import Voyage.Collections        // a voyage-lang-native module
import System.Text.Json          // a referenced .NET/C# assembly's namespace
```

There is no syntactic distinction between importing a voyage-lang module
and importing a C#-authored (or F#-authored, or any CLR-language-authored)
namespace — both resolve through the same CLR assembly-metadata mechanism.
Whether an `import`ed symbol originated from `Voyage.Compiler` output or
`csc`/Roslyn output is invisible at the import-declaration level.

### Scoped imports

Matching Swift's `import <kind> Module.Symbol` form, for importing a single
declaration rather than an entire namespace:

```voyage
import struct System.Guid
import class System.Text.StringBuilder
import func System.Math.Sqrt
```

`@syncSafe` may attach to a scoped import to assert the symbol never
suspends, permitting it to be called from inside an `atomic { }` block
(Section 8). See ADR-0008 Rule 4 for the full rationale — the annotation
is a manual, human-asserted trust boundary, required per-symbol, and never
attaches to a whole-namespace import.

```voyage
@syncSafe
import func ExternalLedger.recordDebit
```

### Access levels and module boundaries

**Default visibility: `internal`.** Any declaration written without an
explicit access modifier is `internal` — visible within its own compiled
module/assembly, invisible outside it. This matches Swift's default
exactly and for the same reason: it lets single-file samples and quick
scripts (like the `import`-free hello world in Section 3) require zero
visibility ceremony, while still keeping declarations closed to outside
assemblies until deliberately opened with `public`.

Reuses the access-control keywords already listed in the token appendix:

- `public` — visible to any referencing assembly, voyage-lang or otherwise
  (maps to CLR `public`)
- `internal` — visible within the same compiled voyage-lang module/assembly
  only (maps to CLR `internal`) — **the default when no modifier is
  written**
- `fileprivate` — visible within the declaring source file only (voyage-
  lang-level concept, erased to `internal`/`private` at the CIL level
  since the CLR has no file-scoped visibility)
- `private` — visible within the declaring type only

Because `public`/`internal` map directly onto real CLR accessibility
modifiers, a `public` voyage-lang type is consumable from a C# project
referencing the compiled voyage-lang assembly with zero extra ceremony —
interop is bidirectional by construction, not just voyage-lang-importing-
C#.

### Open items

- Whether voyage-lang needs a Swift-style `@testable import` equivalent —
  not yet decided.
- Whether re-exporting (Swift's `@_exported import`) is supported — not
  yet decided.
- Package/project manifest format for declaring assembly references
  (analogous to `.csproj` `<PackageReference>`) is a `Voyage.Cli` tooling
  concern, not a language-syntax one, and is out of scope for this
  document.

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
- [x] ~~Module/import syntax~~ — resolved, see Section 11 and ADR-0007:
      Swift-inspired declaration syntax resolving against CLR assembly
      metadata directly, no C interop-style bridging layer needed.
      Sub-items still open: `@testable import`, re-export syntax, package
      manifest format (tooling, not language syntax).
- [ ] Attribute/annotation syntax (voyage-lang equivalent of Aurelia's
      `@differentiable`/`@invariant`, if any) — not yet decided; would be a
      deliberate divergence point rather than a copy of either sibling

---

## Appendix: Supported Tokens (Draft v0.1)

### Keywords

| Category | Tokens |
|---|---|
| Declarations | `func`, `struct`, `enum`, `protocol`, `extension`, `actor`, `case`, `associatedtype` |
| Bindings | `let`, `var` |
| Types | `Self`, `Any`, `Optional` (`?` sugar), `some`, `any` |
| Attributes | `@main`, `@syncSafe` |
| Control flow | `if`, `else`, `guard`, `switch`, `default`, `for`, `in`, `while`, `repeat`, `break`, `continue`, `return`, `fallthrough` |
| Pattern binding | `if let`, `guard let` |
| Error handling | `throw`, `throws`, `try`, `catch`, `do` |
| Concurrency | `async`, `await`, `task`, `spawn`, `join`, `async let`, `atomic` |
| Access control | `public`, `internal`, `private`, `fileprivate` |
| Modifiers | `static`, `mutating`, `final`, `override`, `weak` |
| Memory / cleanup | `defer`, `using` |
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