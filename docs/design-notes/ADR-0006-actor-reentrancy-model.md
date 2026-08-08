# ADR-0006: Fully Reentrant Actors via a Lock-Free Mailbox Loop

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0002 (Concurrency model — actor + task{} as complementary
  layers). Resolves the actor reentrancy follow-up flagged there.

## Context

ADR-0002 adopted `actor` as voyage-lang's isolation boundary type, alongside
`task{}`/`spawn{}`/`async let` as structured concurrency primitives,
mirroring Swift's split between isolation and spawning. It left one
question open: what actually happens, at runtime, when an `await` inside an
actor method yields control — i.e., actor reentrancy semantics.

This matters because Swift and the CLR arrive at concurrency from different
directions, and voyage-lang can't just copy Swift's answer verbatim:

- **Swift's actors** are fully reentrant by design, and this works cleanly
  because Swift's concurrency runtime manages its own cooperative, custom
  scheduler with a fixed-width thread pool purpose-built for actor
  execution.
- **The CLR** has no equivalent custom scheduler for this purpose. Async
  execution on .NET is driven by `async`/`await` compiling down to
  compiler-generated `IAsyncStateMachine` types, dispatched through the
  general-purpose `ThreadPool`. There's no actor-aware runtime layer
  underneath it.

This means voyage-lang can adopt Swift's *design goal* (fully reentrant
actors, deadlock-free by construction) without being able to reuse Swift's
*implementation strategy*. `Voyage.Runtime` needs its own lowering
strategy suited to how the CLR actually schedules work.

A na�ve port using something like a heavy `SynchronizationContext` to force
actor-method continuations back onto a captured context would work
semantically but kills scalability — it reintroduces exactly the kind of
thread affinity that makes ASP.NET's classic `SynchronizationContext`
model a known scaling bottleneck, and it's a poor fit for
NativeAOT-first, high-core-count targets, which is a stated priority for
`Voyage.Runtime` (see `docs/spec/grammar.md`, NativeAOT + linux-musl-x64/
arm64 as tier-1 targets).

## Decision

voyage-lang adopts **fully reentrant actors**, matching Swift's design
goal, implemented via a **lock-free mailbox loop** built on
`System.Threading.Channels.Channel<T>` rather than any
`SynchronizationContext`-based approach.

### Core implementation pattern

**a. The mailbox.** Each actor instance owns a single-consumer message
queue: `Channel<T>` created with `SingleReader = true` (the actor's own
processing loop is the only reader) and `SingleWriter = false` (any number
of external callers may enqueue concurrently). `SingleReader = true` lets
the CLR's channel implementation skip locks and interlocked operations on
the read side entirely, which matters since every actor method call in the
system routes through this path.

**b. The worker loop.** `Voyage.Runtime` drives each actor with an active
loop runner that pulls the next message off the mailbox, invokes the
corresponding method, and awaits its completion before pulling the next
message — except at suspension points, per (c).

**c. The suspension.** When a message handler's execution hits an `await`,
the compiler-generated `IAsyncStateMachine` yields control back to the
.NET `ThreadPool` in the ordinary CLR way. Critically, the actor's loop
runner does **not** block waiting for that continuation to resume. It
immediately checks the mailbox for the next pending message and begins
processing it. Reentrant calls interleave with the original call's
eventual continuation rather than queuing behind it.

### Deadlock immunity

If Actor A calls Actor B, and B calls back into A within the same
asynchronous chain, the system does not deadlock: B's call into A is
simply a new message landing in A's mailbox, processed by A's loop runner
like any other incoming message — including while A's original call to B
is still logically "in flight" on the stack, suspended at an `await`.

### NativeAOT / scaling fit

Because no actor method is pinned to a specific thread across an `await`
boundary, the CLR `ThreadPool` can freely steal and schedule continuations
across cores without the context-switching and thread-affinity overhead a
`SynchronizationContext`-based design would impose. This is a materially
better fit for `Voyage.Runtime`'s NativeAOT/multi-core targets than a
Swift-style dedicated scheduler would be if ported naively.

## The Critical Challenge: State Evaporation Across `await`

Full reentrancy has a sharp edge: an actor's local invariants over its own
mutable state **are not preserved across an `await`** inside a method
body. Between the moment execution suspends at `await` and the moment it
resumes, an arbitrary number of other messages may have run to completion
against the same actor, mutating the very state the suspended method was
reasoning about. Code written as though `self.balance` is stable across an
`await` is silently wrong under this model — this is the same trap Swift
users hit with reentrant actors, and it's had real consequences in
practice, most infamously in class-of-bugs the Swift community refers to
informally as "check-then-act across an await" races.

voyage-lang cannot treat this as the developer's problem alone; the
compiler needs to actively defend against it.

## Compiler Mitigations

### a. Compiler-enforced static warnings

`Voyage.Compiler/Semantics` tracks actor-local mutable state access across
`await` boundaries within a method body. If code accesses or mutates a
`self`-scoped mutable property (e.g. `self.balance`) **after** an `await`
in the same method, without an explicit re-validation of that state, the
compiler emits a diagnostic — a warning by default, escalatable to an
error via project configuration (`Voyage.Cli` build settings, TBD in a
future ADR). This forces the interleaving hazard into the open at
compile time rather than leaving it as a purely runtime concern.

### b. Isolated reentrancy boundaries via local copying

The idiomatic pattern voyage-lang encourages — and that the diagnostic in
(a) is tuned to reward — is:

```voyage
actor Ledger {
    var balance: Decimal = 0

    func withdraw(amount: Decimal) async throws(LedgerError) {
        // 1. Read actor state into an immutable local BEFORE the await.
        let snapshotBalance = balance
        guard snapshotBalance >= amount else {
            throw LedgerError.insufficientFunds
        }

        // 2. Perform the async operation independently of actor state.
        let confirmation = try await ledgerService.recordWithdrawal(amount)

        // 3. Re-enter with an atomic transaction block that re-validates
        //    and applies changes against current state, not the stale
        //    snapshot.
        try atomic {
            guard balance >= amount else {
                throw LedgerError.insufficientFunds  // re-checked, not assumed
            }
            balance -= amount
        }
    }
}
```

The `atomic { }` block (syntax provisional, needs its own grammar entry) is
a synchronous, non-suspending scope guaranteed to run without interleaving
against other mailbox messages — it re-validates against current state
rather than trusting the pre-`await` snapshot, closing the race the naive
version would have.

## Consequences

- **Positive:** Matches Swift's deadlock-free reentrant design goal without
  requiring a custom Swift-style scheduler — reuses the CLR's own
  `ThreadPool` and `Channel<T>` machinery, both mature, NativeAOT-compatible
  primitives.
- **Positive:** Avoids the specific scaling trap (`SynchronizationContext`
  thread affinity) that a naive Swift-to-CLR port would fall into.
- **Positive:** The state-evaporation hazard is treated as a first-class
  compiler concern (static analysis + an idiomatic mitigation pattern)
  rather than left as undocumented tribal knowledge, unlike how this class
  of bug is often learned the hard way in other reentrant-actor languages.
- **Negative / open risk:** The static analysis in (a) requires real
  design work — precise rules for what counts as "re-validating" state,
  how it interacts with helper methods that touch `self` indirectly, and
  false-positive rate on legitimate patterns are all unresolved. Tracked
  as a follow-up, not solved by this ADR.
- **Negative / open risk:** `atomic { }` block syntax and semantics
  (can it itself contain `await`? almost certainly not, since that would
  reintroduce the exact hazard it exists to close — needs an explicit
  rule) are provisional and need their own `grammar.md` entry and
  likely a dedicated ADR before implementation.
- **Follow-up:** Actor-to-actor call ordering guarantees (or explicit lack
  thereof) under this model aren't yet specified — e.g., whether messages
  from a single sender to a single actor are guaranteed FIFO. `Channel<T>`
  itself provides FIFO ordering per the .NET documentation, which is a
  reasonable default to inherit, but this needs to be stated explicitly
  rather than left implicit.

## Alternatives Considered

- **`SynchronizationContext`-based actor isolation** — rejected; forces
  thread affinity across `await` boundaries, which conflicts directly with
  NativeAOT/multi-core scaling goals and is a well-known bottleneck
  pattern in the .NET ecosystem (e.g. classic ASP.NET's `SynchronizationContext`
  deadlock and scaling issues).
- **Non-reentrant ("turn-based") actors**, where an actor fully completes
  one message, including any `await`s inside it, before starting the next
  (closer to how some other .NET actor frameworks like Orleans or Akka.NET
  behave by default) — rejected as the primary model; this avoids the
  state-evaporation hazard entirely, but forfeits the deadlock immunity
  that was the explicit design goal, and can leave an actor's message
  queue backed up behind a single slow `await` chain. May be worth
  revisiting as an opt-in alternative isolation mode in a future ADR, but
  is not the default.
- **Blocking on await inside the loop runner** — rejected outright; this
  defeats the entire purpose of async I/O and would serialize all actor
  work behind whatever the current message happens to be waiting on,
  eliminating any concurrency benefit.
