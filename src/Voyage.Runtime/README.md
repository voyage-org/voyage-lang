# Voyage.Runtime

Not yet started.

Runtime support shipped alongside compiled voyage-lang programs — builtin
types and collections (`Core/`), and .NET BCL interop shims (`Interop/`).

Directly depends on design decisions already made but not yet
implemented against: the `Array<T>`/`[T]` copy-on-write value-semantics
question flagged open in `type-system.md`, the `Channel<T>`-backed actor
mailbox from ADR-0006, and `Disposable`/`using` from `memory-model.md`
all need real implementations here once `Voyage.Compiler` can emit code
that calls into them.

No ADR yet exists for `Voyage.Runtime`'s own internal architecture —
expected once `Voyage.Compiler/CodeGen/` is far enough along to need a
concrete runtime to target.
