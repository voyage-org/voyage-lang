using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Parsing;

namespace Voyage.Compiler.Semantics;

/// <summary>
/// The public entry point to <c>Semantics/</c> — takes a
/// <c>Parsing.CompilationUnit</c> and produces a
/// <see cref="BoundCompilationUnit"/>, the same relationship
/// <c>Parser.Parse</c> has to <c>Lexer.Tokenize</c>'s output. Runs
/// <see cref="DeclarationBinder"/>'s signature pass first, then
/// <see cref="Binder"/>'s body pass for each function — see
/// <see cref="DeclarationBinder"/>'s remarks for why the two passes are
/// ordered this way.
///
/// Scope note: this class, and everything else in <c>Semantics/</c> so
/// far, implements exactly ADR-0011's "minimal pipeline" language
/// subset — unlabeled function calls, <c>let</c>/<c>var</c>,
/// <c>if</c>/<c>while</c>, stored-property-only <c>struct</c>/
/// <c>enum</c>, and <c>switch</c>/pattern matching. Every construct
/// ADR-0011 explicitly defers (labeled arguments, generics
/// instantiation, attributes, modifiers, <c>associatedtype</c>, computed
/// properties, array literals, <c>actor</c>/<c>task{}</c>/
/// <c>atomic{}</c>, <c>throws</c>, <c>for</c>-in) is diagnosed as
/// unsupported rather than silently accepted or crashing — see each
/// binder's own "not yet supported" diagnostic branches.
/// </summary>
public static class SemanticAnalyzer
{
    public static BoundCompilationUnit Analyze(CompilationUnit unit, IDiagnosticSink diagnostics)
    {
        var declarationBinder = new DeclarationBinder(diagnostics);
        var (globalScope, structSymbols, enumSymbols, functionSymbols, typeResolver) = declarationBinder.BindSignatures(unit);

        var boundFunctions = new List<BoundFunctionDeclaration>();
        foreach (var function in functionSymbols)
        {
            // A null Body means a protocol-requirement-shaped function
            // (no `{ ... }` at all) reached top level — per
            // FunctionDeclaration's own remarks this is meant for
            // `protocol` member lists, not top-level declarations, so a
            // top-level occurrence is diagnosed rather than silently
            // skipped or crashing on a null body.
            if (function.Declaration.Body is null)
            {
                diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"top-level function '{function.Name}' has no body — a bodyless function is only valid as a protocol requirement",
                    function.Declaration.Span));
                continue;
            }

            var binder = new Binder(globalScope, typeResolver, diagnostics, function.ReturnType);
            var body = binder.BindFunctionBody(function, function.Declaration.Body);
            boundFunctions.Add(new BoundFunctionDeclaration(function, body, function.Declaration.Span));
        }

        var boundStructs = structSymbols
            .Select(s => new BoundStructDeclaration(s, s.Declaration.Span))
            .ToList();

        var boundEnums = enumSymbols
            .Select(e => new BoundEnumDeclaration(e, e.Declaration.Span))
            .ToList();

        // Anything at the top level that isn't itself a func/struct/enum
        // declaration (a bare `print(...)` call, a top-level `let`, etc.)
        // — bound directly against the global scope, same as
        // Parsing.CompilationUnit.Statements mixes declarations and
        // ordinary statements together at file scope.
        var topLevelStatements = unit.Statements
            .Where(s => s is not FunctionDeclaration and not StructDeclaration and not EnumDeclaration)
            .ToList();

        var topLevelBinder = new Binder(globalScope, typeResolver, diagnostics, currentReturnType: PrimitiveType.Void);
        var boundTopLevel = topLevelBinder.BindTopLevelStatements(topLevelStatements);

        return new BoundCompilationUnit(boundFunctions, boundStructs, boundEnums, boundTopLevel, unit.Span);
    }
}
