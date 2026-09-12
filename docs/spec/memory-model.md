# voyage-lang Memory Model (Draft v0.1)

Status: draft — first pass, not yet implemented in `Voyage.Compiler`/
`Voyage.Runtime`.

This document specifies how voyage-lang manages memory, and is explicit
about where it follows Swift's precedent and where it deliberately departs
from it, since Swift's memory model (ARC) and the CLR's (tracing GC) are
not the same mechanism and voyage-lang can't inherit Swift's answer
verbatim — the same situation ADR-0006 already worked through for actor
reentrancy. See ADR-0009 for the full rationale behind the departures
recorded here.

---

## 1. Two Memory Models, Not One: Value Types vs. Reference Types

voyage-lang inherits Swift's value/reference type split, and it happens to
map onto the CLR's own value/reference type split almost exactly —
this is one of the few places where targeting the CLR made a Swift-inspired
decision *easier* to implement rather than harder.

- **`struct`** is a **value type**. Assignment, parameter passing, and
  returning a `struct` **copies** it. Lowers directly to a CLR value type
  (`System.ValueType`), which is stack-allocated when possible (a local
  variable, a field of another value type not itself heap-allocated) or
  inlined into the containing object's layout otherwise. No GC involvement
  for a `struct` used this way — copying a value type on the CLR has
  nothing to do with the garbage collector.
- **`class`** and **`actor`** are **reference types**. Assignment,
  parameter passing, and returning copies a *reference*, not the
  underlying data — multiple bindings can point at the same instance.
  Lowers directly to a CLR reference type (heap-allocated,
  GC-tracked object).
- **`enum`** is always a **value type**, regardless of what its
  associated values' types are (ADR-0012) — the same principle already
  established above for a `struct` holding a reference-typed field: a
  field being a reference type doesn't make the *containing* type a
  reference type. Lowers to a CLR `struct` with an integer tag plus one
  field per case/associated-value-slot (ADR-0012's Decision 2 has the
  full layout rationale, including why a space-efficient union-style
  layout isn't used for now).

```voyage
struct Point {
    var x: Double
    var y: Double
}

var a = Point(x: 1, y: 2)
var b = a          // COPY — b.x mutation does not affect a.x
b.x = 99

class Counter {
    var value: Int = 0
}

let c1 = Counter()
let c2 = c1         // SAME INSTANCE — c2.value mutation affects c1.value
c2.value = 99
```

## 2. Garbage Collection, Not ARC — and Why That's a Deliberate Departure

Swift manages reference-type lifetime with **ARC** (automatic reference
counting): the compiler inserts retain/release calls at compile time, and
an object is deallocated the instant its reference count hits zero. This
gives Swift fairly deterministic, immediate cleanup timing for reference
types, at the cost of needing `weak`/`unowned` references to break
retain cycles manually, and runtime retain/release overhead on every
reference copy.

voyage-lang **does not** use ARC. Reference types (`class`, `actor`) are
managed by the CLR's **tracing garbage collector** — the same GC used by
every other CLR language, in both the standard CoreCLR runtime and
NativeAOT (NativeAOT embeds the same GC, not a separate one; Workstation
and Server GC modes are both available exactly as they are for ordinary
CoreCLR/C# applications).

This is a genuine, consequential divergence from Swift, not a minor
implementation detail, and ADR-0009 covers the full reasoning. The
short version:

- **Reclaiming ARC's behavior on the CLR would mean fighting the runtime**,
  not working with it — the CLR has no retain/release primitives to hook
  into; building ARC semantics on top of a tracing GC would mean either a
  second, shadow reference-counting layer racing against the real GC (pure
  overhead, no benefit) or abandoning the CLR's GC entirely (abandoning
  NativeAOT's whole raison d'être as a mature, battle-tested runtime).
- **Tracing GC has one real advantage ARC lacks**: reference cycles are
  reclaimed automatically. Swift developers must reason about `weak`/
  `unowned` to avoid leaking cyclic structures (e.g. parent↔child object
  graphs, delegate patterns); voyage-lang developers do not need to think
  about this class of bug at all under a tracing collector.
- **The cost**: collection timing is not deterministic the way ARC's
  reference-count-hits-zero moment is. An object with no more live
  references is *eligible* for collection, not immediately reclaimed.
  This matters for anything holding a genuinely scarce resource (file
  handles, sockets, unmanaged memory, actor mailbox channels) where
  "eventually, whenever the GC gets to it" is not an acceptable release
  timeline.

## 3. Deterministic Cleanup: `Disposable` and `using`

Because GC timing is non-deterministic, voyage-lang needs an explicit,
deterministic cleanup mechanism for scarce-resource-holding types,
independent of when the GC eventually runs — this is the same problem C#'s
`IDisposable`/`using` pattern exists to solve, and voyage-lang adopts the
same shape rather than inventing a new one, since it maps directly onto
existing CLR interop (a voyage-lang type implementing this protocol is
`IDisposable`-compatible from the C# side for free, consistent with
ADR-0007's bidirectional-interop-by-construction philosophy).

```voyage
protocol Disposable {
    func dispose()
}

class FileHandle: Disposable {
    func dispose() {
        // release the OS handle deterministically
    }
}

func processFile(path: String) {
    using let handle = FileHandle(path: path)
    // handle.dispose() is called here, deterministically, when the
    // `using` scope exits — including on early return or thrown error
}
```

`using` composes with `defer` (see Section 4) at the language level, but
is offered as the primary idiom for the specific disposal case since it
ties cleanup directly to a binding's scope rather than requiring the
developer to remember to write the `defer` by hand.

## 4. `defer`

Adopted directly from Swift: a `defer` block runs when the enclosing scope
exits, regardless of how it exits (normal fall-through, early `return`, or
a thrown error). Multiple `defer` blocks in the same scope run in reverse
order of declaration, matching Swift.

```voyage
func readConfig() throws(IOError) -> Config {
    let lock = acquireLock()
    defer { lock.release() }

    // ... work that might throw ...
    return config
}
```

`defer` is the general-purpose mechanism; `using` (Section 3) is the
narrower, more idiomatic choice specifically for `Disposable` cleanup.

## 5. Actor State and Memory

An `actor` (Section 8 of `grammar.md`) is a reference type, GC-managed like
any `class`, with one addition: an actor's mailbox (its
`Channel<T>`-backed message queue, per ADR-0006) is itself a managed
object owned by the actor instance. An actor becomes eligible for
collection once nothing holds a live reference to it — including, notably,
once no external `Sender<T>` handles referencing its mailbox remain live —
at which point both the actor and its mailbox are reclaimed together in
the ordinary GC sweep. No special-cased actor deallocation protocol is
needed beyond what any reference-type cleanup already requires.

## 6. Ownership: Deferred (See grammar.md Section 9)

voyage-lang does **not** currently adopt Swift's `borrowing`/`consuming`
ownership modifiers. This was already noted as tentative in `grammar.md`
Section 9; this document confirms the same position from the memory-model
side: Rust/Swift-style compile-time ownership tracking is meaningful
because those languages need to know statically when a value's underlying
storage can be freed without a GC to fall back on. Under a tracing GC,
there is always a fallback — the GC reclaims a `class`/`actor` regardless
of whether the compiler proves anything about its lifetime — so ownership
tracking's main payoff (safe deterministic deallocation without a
collector) doesn't apply to voyage-lang's reference types the way it does
in Aurelia (C++23 core, no GC at all) or genuinely GC-less Swift-adjacent
contexts (Embedded Swift). It could still matter for `struct`-heavy,
allocation-sensitive NativeAOT hot paths as a *performance* tool rather
than a *safety* tool — that's the door left open in `grammar.md` Section 9,
not reopened here.

## 7. Weak References

Because voyage-lang uses a tracing GC rather than ARC, `weak` references
are useful for a narrower reason than in Swift: not to break retain
cycles (the GC already handles cycles), but to avoid unintentionally
keeping an otherwise-collectible object alive — e.g. caches, observer/
event-handler registrations.

```voyage
class EventBus {
    weak var lastSubscriber: Subscriber?
}
```

Swift's `unowned` (a non-optional, unchecked weak reference, cheaper than
ARC's `weak` at runtime because it skips ARC's weak-reference table) has
no equivalent performance rationale under a tracing GC — the CLR's own
`WeakReference<T>` is already the uniform mechanism, and voyage-lang's
`weak` lowers directly to it. **`unowned` is not adopted.**

---

## Open Items for Next Pass

- [ ] `using` block scoping interaction with `async`/`await` and `actor`
      isolation (Section 3) — does a `using` binding inside an `async`
      function correctly dispose across suspension points? Needs explicit
      confirmation, likely "yes, same as C#'s `await using`" but not yet
      stated as a rule.
- [ ] Struct mutation semantics for `self`-mutating methods (Swift's `mutating
      func`) — not yet drafted here or in `grammar.md`.
- [ ] Stack-allocation guarantees, if any, for `struct` beyond "the CLR
      may choose to" — whether voyage-lang wants a `Span<T>`-style
      stack-only type category for NativeAOT hot paths is unresolved.
- [ ] Finalizers (CLR's `~Type()` equivalent) — whether voyage-lang
      exposes them at all, given `using`/`defer` already cover the
      deterministic-cleanup case they're a poor substitute for.
