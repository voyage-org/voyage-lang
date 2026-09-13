using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Voyage.Compiler.Semantics;

namespace Voyage.Compiler.CodeGen;

/// <summary>
/// The <c>CodeGen/</c> phase entry point. Unlike every earlier phase
/// (<c>Parser.Parse</c>, <c>SemanticAnalyzer.Analyze</c>,
/// <c>Lowerer.Lower</c>), this phase takes no <c>IDiagnosticSink</c> —
/// by design. A <see cref="BoundCompilationUnit"/> reaching
/// <see cref="CodeGenerator"/> is assumed to already be fully valid: any
/// diagnostic from <c>Semantics/</c> or <c>Lowering/</c> means the
/// caller should stop before ever calling here, the same way a real
/// compiler doesn't try to generate code for a program that failed to
/// type-check. Anything that goes wrong inside this phase is therefore
/// an internal-consistency bug in an earlier phase (or in this one),
/// surfaced as a thrown exception, not a user-facing diagnostic — see
/// e.g. <see cref="MethodEmitContext.GetStorage"/>'s and
/// <see cref="TypeEmitter"/>'s own remarks for concrete examples of that
/// distinction.
///
/// Exposes two output modes per ADR-0012's Decision 1, sharing every
/// emission call below them (<see cref="TypeEmitter"/>,
/// <see cref="StatementEmitter"/>, <see cref="ExpressionEmitter"/>) —
/// only how the final assembly is realized differs:
///
/// - <see cref="EmitInMemory"/> — fast, in-process, for tests. `Main` is
///   invoked directly via reflection, so no CLR-level entry point needs
///   to be set.
/// - <see cref="EmitToFile"/> — a genuinely persisted, independently
///   runnable assembly. Getting this right needed more than
///   <c>PersistedAssemblyBuilder.Save(path)</c> alone: that call
///   produces a loadable assembly, but with no CLR entry point token
///   set, so <c>dotnet &lt;output&gt;.dll</c> fails with "Entry point
///   not found" — found for real by actually trying to run
///   `voyage build`'s output the way a person would (via the
///   <c>Voyage.Cli</c> project), not just checking the file exists.
///   The documented fix is lower-level: generate the metadata
///   separately via <c>GenerateMetadata</c>, then build the PE image
///   directly via <see cref="ManagedPEBuilder"/> with an explicit
///   <c>entryPoint</c> token pointing at the emitted <c>Main</c>
///   method.
/// </summary>
public static class CodeGenerator
{
    public static (Assembly Assembly, Type ProgramType) EmitInMemory(BoundCompilationUnit unit, string assemblyName)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
        var (programType, _) = Emit(unit, moduleBuilder);
        return (assemblyBuilder, programType);
    }

    public static void EmitToFile(BoundCompilationUnit unit, string assemblyName, string outputPath)
    {
        var persistedBuilder = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var moduleBuilder = persistedBuilder.DefineDynamicModule("MainModule");
        var (_, mainMethod) = Emit(unit, moduleBuilder);

        var metadataBuilder = persistedBuilder.GenerateMetadata(out var ilStream, out var fieldData);
        var peBuilder = new ManagedPEBuilder(
            header: PEHeaderBuilder.CreateExecutableHeader(),
            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
            ilStream: ilStream,
            mappedFieldData: fieldData,
            entryPoint: MetadataTokens.MethodDefinitionHandle(mainMethod.MetadataToken));

        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        peBlob.WriteContentTo(fileStream);
    }

    private static (Type ProgramType, MethodBuilder MainMethod) Emit(BoundCompilationUnit unit, ModuleBuilder moduleBuilder)
    {
        var ctx = new CodeGenContext(moduleBuilder);

        TypeEmitter.EmitStructsAndEnums(ctx, unit);

        var programType = moduleBuilder.DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // Declare every function's MethodBuilder before emitting any
        // body — same two-pass reasoning DeclarationBinder already
        // established for signatures: a function calling another
        // function declared later in the same file needs that other
        // MethodBuilder to already be registered.
        foreach (var function in unit.Functions)
        {
            var parameterTypes = function.Symbol.Parameters.Select(p => ctx.ResolveClrType(p.Type)).ToArray();
            var methodBuilder = programType.DefineMethod(
                function.Symbol.Name,
                MethodAttributes.Public | MethodAttributes.Static,
                ctx.ResolveClrType(function.Symbol.ReturnType),
                parameterTypes);
            ctx.RegisterFunction(function.Symbol, methodBuilder);
        }

        foreach (var function in unit.Functions)
        {
            EmitFunctionBody(ctx, function);
        }

        var mainMethod = EmitMain(ctx, programType, unit.TopLevelStatements);
        var builtType = programType.CreateType();

        return (builtType, mainMethod);
    }

    private static void EmitFunctionBody(CodeGenContext ctx, BoundFunctionDeclaration function)
    {
        var methodBuilder = ctx.GetFunction(function.Symbol);
        var il = methodBuilder.GetILGenerator();
        var method = new MethodEmitContext(il);

        for (var i = 0; i < function.Symbol.Parameters.Count; i++)
        {
            // Re-deriving `AsVariable()` here (rather than caching the
            // instance Semantics.Binder created) is safe: VariableSymbol
            // is a record, and MethodEmitContext's dictionary keys on
            // structural equality, not reference identity — an
            // independently-constructed but equal VariableSymbol looks
            // up correctly.
            method.DeclareArgument(function.Symbol.Parameters[i].AsVariable(), i);
        }

        StatementEmitter.EmitStatements(ctx, method, function.Body);

        // A trailing `ret` after whatever the body already emitted.
        // Harmless dead code when the body already returns on every
        // path (the common, currently-tested case — implicit-return
        // injection in Lowering/ guarantees this for a single-
        // expression body, and Void functions can simply fall off the
        // end). For a multi-statement, non-Void function that doesn't
        // actually return on every path — an open gap neither
        // Semantics/ nor Lowering/ currently catches (see
        // Lowering/README.md) — this trailing `ret` is reachable with
        // nothing pushed for the declared return type, which surfaces
        // as a runtime verification failure instead of silently wrong
        // behavior. That's an acceptable outcome for this phase: a loud
        // failure on an already-known gap, not a new one CodeGen/
        // introduces.
        il.Emit(OpCodes.Ret);
    }

    private static MethodBuilder EmitMain(CodeGenContext ctx, TypeBuilder programType, IReadOnlyList<BoundStatement> topLevelStatements)
    {
        var mainMethod = programType.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var il = mainMethod.GetILGenerator();
        var method = new MethodEmitContext(il);

        StatementEmitter.EmitStatements(ctx, method, topLevelStatements);

        il.Emit(OpCodes.Ret);

        return mainMethod;
    }
}
