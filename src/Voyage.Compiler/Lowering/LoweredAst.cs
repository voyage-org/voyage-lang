using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.Lowering;

/// <summary>
/// Design note for this whole file: <c>Lowering/</c> does <em>not</em>
/// introduce a full parallel IR hierarchy mirroring
/// <c>Semantics.BoundAst.cs</c>. For ADR-0011's minimal subset, almost
/// every bound node (arithmetic, calls, struct construction, property
/// access, <c>if</c>/<c>while</c>, variable declarations) needs no
/// transformation at all — only implicit-return injection (ADR-0005)
/// and <c>switch</c>/pattern-match desugaring actually rewrite anything.
/// Building a whole second tree type just to carry the ~90% of nodes
/// that pass through unchanged would be pure duplication with no payoff
/// at this scope — so <see cref="Lowerer"/> transforms a
/// <c>Semantics.BoundCompilationUnit</c> in place and hands
/// <c>CodeGen/</c> that same type back, its two post-lowering invariants
/// being: no <c>BoundSwitch</c> node survives (desugared into the
/// <see cref="LoweredEnumTagCheck"/>/<see cref="LoweredAssociatedValueAccess"/>
/// forms below, wrapped in ordinary <c>BoundIf</c>s), and every
/// non-<c>Void</c> function whose entire body was a single bare
/// expression now has an explicit <c>BoundReturn</c> instead.
///
/// This file holds the only two node kinds that genuinely don't exist
/// anywhere before lowering: pattern matching against an enum's case is
/// a source-level idea with no equivalent operation in
/// <c>Semantics/</c> — enums don't have a "tag" or associated-value
/// storage layout as far as <c>Semantics/</c> is concerned, since that
/// layout doesn't exist until <c>CodeGen/</c> picks a concrete CLR
/// representation for an enum (an open decision — see
/// <c>CodeGen/README.md</c>). These two node types name the operation
/// abstractly now; <c>CodeGen/</c> gives it a concrete meaning later
/// once that representation is chosen, the same way
/// <c>Semantics.BoundStructConstruction</c> names "construct this
/// struct" without <c>Semantics/</c> knowing or caring how CLR
/// allocation actually works.
/// </summary>
/// <summary>
/// "Is <see cref="Subject"/> currently the <see cref="Case"/> case of
/// its enum?" — the boolean test an <c>EnumCasePattern</c> compiles down
/// to. Always <see cref="PrimitiveType.Bool"/>-typed, regardless of
/// <see cref="Case"/>'s own associated-value types.
///
/// Design note for this whole file: <c>Lowering/</c> does <em>not</em>
/// introduce a full parallel IR hierarchy mirroring
/// <c>Semantics.BoundAst.cs</c>. For ADR-0011's minimal subset, almost
/// every bound node (arithmetic, calls, struct construction, property
/// access, <c>if</c>/<c>while</c>, variable declarations) needs no
/// transformation at all — only implicit-return injection (ADR-0005)
/// and <c>switch</c>/pattern-match desugaring actually rewrite anything.
/// Building a whole second tree type just to carry the ~90% of nodes
/// that pass through unchanged would be pure duplication with no payoff
/// at this scope — so <see cref="Lowerer"/> transforms a
/// <c>Semantics.BoundCompilationUnit</c> in place and hands
/// <c>CodeGen/</c> that same type back, its two post-lowering invariants
/// being: no <c>BoundSwitch</c> node survives (desugared into this node
/// and <see cref="LoweredAssociatedValueAccess"/>, wrapped in ordinary
/// <c>BoundIf</c>s), and every non-<c>Void</c> function whose entire
/// body was a single bare expression now has an explicit
/// <c>BoundReturn</c> instead.
///
/// This node (and <see cref="LoweredAssociatedValueAccess"/>) is one of
/// the only two kinds that genuinely don't exist anywhere before
/// lowering: pattern matching against an enum's case is a source-level
/// idea with no equivalent operation in <c>Semantics/</c> — enums don't
/// have a "tag" or associated-value storage layout as far as
/// <c>Semantics/</c> is concerned, since that layout doesn't exist until
/// <c>CodeGen/</c> picks a concrete CLR representation for an enum (an
/// open decision — see <c>CodeGen/README.md</c>). This node names the
/// operation abstractly now; <c>CodeGen/</c> gives it a concrete meaning
/// later once that representation is chosen, the same way
/// <c>Semantics.BoundStructConstruction</c> names "construct this
/// struct" without <c>Semantics/</c> knowing or caring how CLR
/// allocation actually works.
/// </summary>
public sealed record LoweredEnumTagCheck(BoundExpression Subject, EnumCaseSymbol Case, SourceSpan Span)
    : BoundExpression(PrimitiveType.Bool, Span);

/// <summary>
/// "Read associated-value slot <see cref="SlotIndex"/> of
/// <see cref="Subject"/>, assuming it's already known to be the
/// <see cref="Case"/> case" — always emitted guarded by a
/// <see cref="LoweredEnumTagCheck"/> for the same <see cref="Case"/>
/// (see <see cref="Lowerer.CompilePattern"/>), so a
/// <c>CodeGen/</c> implementation never needs to itself re-check the
/// tag before reading the slot. Typed as
/// <c>Case.AssociatedValues[SlotIndex].Type</c> — the same type
/// <see cref="Semantics.Binder"/> already resolved when it built
/// <see cref="Case"/> in the first place.
/// </summary>
public sealed record LoweredAssociatedValueAccess(BoundExpression Subject, EnumCaseSymbol Case, int SlotIndex, SourceSpan Span)
    : BoundExpression(Case.AssociatedValues[SlotIndex].Type, Span);
