using System.Reflection.Emit;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.CodeGen;

/// <summary>
/// Where a <see cref="VariableSymbol"/> actually lives once emitted:
/// either a static method's argument slot (a function parameter — see
/// <see cref="ArgumentStorage"/>) or an IL local (a <c>let</c>/<c>var</c>
/// binding — see <see cref="LocalStorage"/>). This distinction matters
/// because <see cref="Semantics.Binder"/> already erases it: a parameter
/// is bound into a function's scope as an ordinary
/// <see cref="VariableSymbol"/> via <c>ParameterSymbol.AsVariable()</c>,
/// so by the time a <c>BoundVariableReference</c> reaches
/// <see cref="ExpressionEmitter"/>, there's no way to tell from the
/// bound tree alone whether it's a parameter or a local — only
/// <see cref="MethodEmitContext"/>'s own bookkeeping (populated
/// separately for parameters vs. each <c>BoundVariableDeclaration</c>
/// encountered) knows which opcodes apply (<c>ldarg</c>/<c>starg</c>
/// vs. <c>ldloc</c>/<c>stloc</c>).
/// </summary>
public abstract record VariableStorage;

public sealed record ArgumentStorage(int Index) : VariableStorage;

public sealed record LocalStorage(LocalBuilder Local) : VariableStorage;

/// <summary>
/// Emission state scoped to one function body (or the top-level
/// <c>Main</c> body) — a fresh instance per
/// <see cref="CodeGenerator"/> call into
/// <see cref="StatementEmitter.EmitStatements"/>, never shared across
/// functions, the same reasoning <see cref="Semantics.Binder"/> gives
/// for creating a fresh instance per function body (its own loop-depth
/// and return-type state shouldn't leak between unrelated functions —
/// this class's <see cref="_loops"/> stack is the CodeGen/ analogue).
/// </summary>
public sealed class MethodEmitContext(ILGenerator il)
{
    public ILGenerator IL { get; } = il;

    private readonly Dictionary<VariableSymbol, VariableStorage> _variables = [];
    private readonly Stack<(Label ContinueLabel, Label BreakLabel)> _loops = new();

    public void DeclareArgument(VariableSymbol variable, int index) => _variables[variable] = new ArgumentStorage(index);

    public void DeclareLocal(VariableSymbol variable, LocalBuilder local) => _variables[variable] = new LocalStorage(local);

    /// <summary>Throws rather than returning a sentinel on a missing
    /// variable — every <c>BoundVariableReference</c> reaching
    /// <see cref="ExpressionEmitter"/> was already resolved against a
    /// real <see cref="VariableSymbol"/> by <see cref="Semantics.Binder"/>,
    /// so a lookup miss here means a genuine CodeGen/ bug (a
    /// <c>BoundVariableDeclaration</c> or parameter that was never
    /// registered), not malformed user input to report as a
    /// diagnostic.</summary>
    public VariableStorage GetStorage(VariableSymbol variable) =>
        _variables.TryGetValue(variable, out var storage)
            ? storage
            : throw new InvalidOperationException($"no storage registered for variable '{variable.Name}' — CodeGen/ bug: it was referenced before being declared or registered");

    public void PushLoop(Label continueLabel, Label breakLabel) => _loops.Push((continueLabel, breakLabel));

    public void PopLoop() => _loops.Pop();

    /// <summary>Same "throw, don't diagnose" reasoning as
    /// <see cref="GetStorage"/> — <see cref="Semantics.Binder"/> already
    /// rejects <c>break</c>/<c>continue</c> outside a loop (see its own
    /// <c>_loopDepth</c> tracking), so a well-formed bound tree never
    /// reaches this with an empty loop stack.</summary>
    public (Label ContinueLabel, Label BreakLabel) CurrentLoop =>
        _loops.Count > 0
            ? _loops.Peek()
            : throw new InvalidOperationException("'break'/'continue' reached CodeGen/ outside any loop — Semantics/ should have already rejected this");
}
