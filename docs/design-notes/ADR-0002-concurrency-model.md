# ADR-0002: Concurrency Model — `actor` and `task{}` as Complementary Layers

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0001 (Swift-inspired syntax)

## Context

Earlier design sessions settled on an async model for voyage-lang: `Future<T>`
wrapping `Task<T>` (never surface-exposed), `spawn`/`join` or `task{}`
blocks, explicit `CancelToken` parameters, and built-in `Sender<T>`/
`Receiver<T>` channels.

Once ADR-0001 pulled in Swift's `actor` type as a candidate isolation
primitive, `grammar.md` initially framed `actor` and `task{}` as competing
alternatives requiring a either/or decision — i.e., "adopt actor and drop
task{}" vs. "keep task{} and skip actor."

A pass over Swift's actual concurrency test suite showed this framing was
wrong. In Swift, `actor` and structured concurrency (`Task`, `TaskGroup`,
`async let`) are not alternatives:

- `actor` is an **isolation boundary** — a type whose mutable state can only
  be touched by one task at a time. It governs data-race safety, not
  spawning.
- `Task`/`TaskGroup`/`async let` are **structured concurrency primitives**
  for spawning, grouping, and awaiting child work — used both inside and
  outside actor bodies. Even `@MainActor`-isolated types reach for
  `withTaskGroup`/`addTask` to fan out concurrent work.

## Decision

voyage-lang adopts both primitives as complementary layers, not a forced
either/or:

- **`actor`** — isolation boundary type, carried over from Swift's model.
- **`task{}` / `spawn{}`** — voyage-lang's structured-concurrency primitive
  for a dynamic/unbounded number of child tasks, analogous to Swift's
  `withTaskGroup`/`addTask`, with `join`/`waitForAll` semantics.
- **`async let`** — lightweight structured concurrency for a fixed,
  known-at-compile-time number of concurrent children, matching Swift's
  `async let` exactly.
- **`Future<T>`** continues to wrap `Task<T>` under the hood, with explicit
  `CancelToken` parameters for cancellation — voyage-lang keeps explicit
  cancellation rather than adopting Swift's ambient cancellation
  propagation, to keep cancellation semantics visible at call sites.

```voyage
actor DataStore {
    var cache: [String: Data] = [:]

    func refresh(urls: [URL]) async throws(NetworkError) {
        task {
            for url in urls {
                spawn {
                    let data = try await network.get(url)
                    self.cache[url.key] = data   // safe: inside actor isolation
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
```

## Consequences

- **Positive:** No forced trade-off between data-race safety (`actor`) and
  flexible task spawning (`task{}`/`async let`) — both are available and
  compose, matching a proven precedent (Swift's own model) rather than an
  invented voyage-lang-specific compromise.
- **Positive:** Keeps the earlier `Future<T>`/`CancelToken`/`Sender`/
  `Receiver` decisions from prior design sessions intact rather than
  discarding them for `actor`.
- **Negative / open risk:** Two structured-concurrency shapes (`async let`
  vs. `task{}`/`spawn{}`) adds surface area beginners need to learn when to
  reach for which. Mitigated by the same split existing in Swift, so
  documentation/tooling precedent exists to draw on for `Voyage.LanguageServer`
  diagnostics and guidance.
- **Open follow-up:** Actor reentrancy semantics (what happens when an
  `await` inside an actor method yields control back to the isolation
  domain) not yet specified — needs its own ADR before `Voyage.Runtime`
  implementation begins.

## Alternatives Considered

- **`actor` only, drop `task{}`** — rejected; would have required
  redesigning the already-settled `Future<T>`/`CancelToken`/channel model
  around actor mailboxes for no clear benefit, and loses a lightweight
  primitive for non-actor-isolated concurrent work.
- **`task{}` only, drop `actor`** — rejected; forfeits compile-time-checked
  data-race safety, which is one of the stronger arguments for leaning on
  Swift's model in the first place per ADR-0001.