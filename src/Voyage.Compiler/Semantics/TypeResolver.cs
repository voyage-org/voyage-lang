using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Parsing;

namespace Voyage.Compiler.Semantics;

/// <summary>
/// Turns a <c>Parsing.TypeNode</c> (syntax — "the identifier <c>Int</c>
/// appeared in a type position") into a <see cref="TypeSymbol"/>
/// (semantics — "that identifier means the built-in Int type"). Needs
/// the global struct/enum symbol table to resolve user-declared type
/// names, which is why <see cref="DeclarationBinder"/> constructs one
/// <see cref="TypeResolver"/> up front and reuses it for every
/// signature/property/case it resolves, rather than each call site
/// building its own lookup.
/// </summary>
public sealed class TypeResolver(IReadOnlyDictionary<string, StructSymbol> structs, IReadOnlyDictionary<string, EnumSymbol> enums, IDiagnosticSink diagnostics)
{
    private static readonly Dictionary<string, PrimitiveType> Primitives = new()
    {
        ["Int"] = PrimitiveType.Int,
        ["Double"] = PrimitiveType.Double,
        ["Bool"] = PrimitiveType.Bool,
        ["String"] = PrimitiveType.String,
        ["Void"] = PrimitiveType.Void,
    };

    /// <summary>
    /// Resolves a single <see cref="TypeNode"/>. Per ADR-0011's minimal
    /// subset, only <see cref="NamedTypeNode"/> with zero generic
    /// arguments is handled — <see cref="ArrayTypeNode"/>,
    /// <see cref="FunctionTypeNode"/>, existentials/opaque types, and
    /// generic type arguments are all outside the minimal subset (array
    /// literals aren't parsed yet at all; the others need generic-call
    /// disambiguation and protocol/existential semantics this phase
    /// doesn't cover yet). Each of those produces a diagnostic and
    /// <see cref="ErrorType.Instance"/> rather than throwing, so binding
    /// can continue past one unresolvable type — same "report and keep
    /// going" philosophy every other phase already follows.
    /// </summary>
    public TypeSymbol Resolve(TypeNode node)
    {
        if (node is not NamedTypeNode named)
        {
            diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"type form '{node.GetType().Name}' is not yet supported by Semantics/ — only plain named types (e.g. 'Int', 'Point') are resolved so far",
                node.Span));
            return ErrorType.Instance;
        }

        if (named.GenericArguments.Count > 0)
        {
            diagnostics.Report(new Diagnostic(
                DiagnosticSeverity.Error,
                $"generic type '{named.Name}<...>' is not yet supported by Semantics/",
                node.Span));
            return ErrorType.Instance;
        }

        if (Primitives.TryGetValue(named.Name, out var primitive))
        {
            return primitive;
        }

        if (structs.TryGetValue(named.Name, out var structSymbol))
        {
            return new StructType(structSymbol);
        }

        if (enums.TryGetValue(named.Name, out var enumSymbol))
        {
            return new EnumType(enumSymbol);
        }

        diagnostics.Report(new Diagnostic(
            DiagnosticSeverity.Error,
            $"unknown type '{named.Name}'",
            node.Span));
        return ErrorType.Instance;
    }
}
