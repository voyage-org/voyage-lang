using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Parsing;

namespace Voyage.Compiler.Semantics;

/// <summary>
/// The first of two binding passes (see <see cref="Binder"/> for the
/// second). Walks every top-level <c>func</c>/<c>struct</c>/<c>enum</c>
/// in a <c>CompilationUnit</c> and resolves each one's *signature* —
/// parameter/return/property/case types — into a <see cref="Symbol"/>,
/// without looking at any function body yet.
///
/// This two-pass split exists for one concrete reason: forward
/// reference. A function can call another function declared later in
/// the same file (<c>func a() { b() }</c> followed by <c>func b() { }
/// </c>), and a struct's property type can name another struct declared
/// later too. If binding were a single pass that both registered
/// signatures *and* bound bodies as it walked top to bottom, the second
/// function/struct wouldn't exist in scope yet when the first one's body
/// was bound. Resolving every signature first means the global
/// <see cref="Scope"/> is complete before <see cref="Binder"/>'s second
/// pass binds any body, so declaration order in source stops mattering
/// (matching how every language with top-level declarations actually
/// behaves).
/// </summary>
public sealed class DeclarationBinder(IDiagnosticSink diagnostics)
{
    /// <summary>
    /// Resolves every top-level declaration's signature and returns the
    /// populated global <see cref="Scope"/> plus each resolved symbol
    /// list, ready for <see cref="Binder"/>'s second pass. Non-
    /// declaration top-level statements (a bare <c>print(...)</c> at
    /// file scope, say) are ignored here — <see cref="Binder"/> handles
    /// those directly since they have no signature to pre-resolve.
    /// </summary>
    public (Scope GlobalScope, IReadOnlyList<StructSymbol> Structs, IReadOnlyList<EnumSymbol> Enums, IReadOnlyList<FunctionSymbol> Functions, TypeResolver TypeResolver)
        BindSignatures(CompilationUnit unit)
    {
        // Struct/enum *names* must all be known before any type in any
        // signature resolves — a property of type `Circle` needs
        // `Circle` registered even if `Circle`'s own properties haven't
        // been resolved yet (and, for a case genuinely impossible to
        // support without indirection later — e.g. `struct Node { var
        // next: Node }` — at least fails with a clear diagnostic instead
        // of a confusing "unknown type" one). So struct/enum resolution
        // itself is two steps: register empty-shelled symbols first,
        // then fill in their properties/cases once every name exists.
        var structDecls = unit.Statements.OfType<StructDeclaration>().ToList();
        var enumDecls = unit.Statements.OfType<EnumDeclaration>().ToList();
        var functionDecls = unit.Statements.OfType<FunctionDeclaration>().ToList();

        var structsByName = new Dictionary<string, StructSymbol>();
        var enumsByName = new Dictionary<string, EnumSymbol>();

        foreach (var decl in structDecls)
        {
            if (!structsByName.TryAdd(decl.Name, new StructSymbol(decl.Name, Properties: [], decl)))
            {
                diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{decl.Name}' is already declared", decl.Span));
            }
        }

        foreach (var decl in enumDecls)
        {
            if (!enumsByName.TryAdd(decl.Name, new EnumSymbol(decl.Name, Cases: [], decl)))
            {
                diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{decl.Name}' is already declared", decl.Span));
            }
        }

        var resolver = new TypeResolver(structsByName, enumsByName, diagnostics);

        // Now that every struct/enum name exists, fill in each one's
        // real shape. Symbols are records, so "filling in" means
        // replacing the shelled-out entry with a new one carrying the
        // resolved Properties/Cases — the dictionary entry is what
        // TypeResolver and every later lookup actually reads.
        foreach (var decl in structDecls)
        {
            var properties = BindStructProperties(decl, resolver);
            structsByName[decl.Name] = structsByName[decl.Name] with { Properties = properties };
        }

        foreach (var decl in enumDecls)
        {
            var cases = BindEnumCases(decl, resolver);
            enumsByName[decl.Name] = enumsByName[decl.Name] with { Cases = cases };
        }

        var globalScope = new Scope(parent: null);
        var functions = new List<FunctionSymbol>();

        foreach (var (name, symbol) in structsByName)
        {
            if (!globalScope.TryDeclare(symbol))
            {
                diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{name}' is already declared", symbol.Declaration.Span));
            }
        }

        foreach (var (name, symbol) in enumsByName)
        {
            if (!globalScope.TryDeclare(symbol))
            {
                diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{name}' is already declared", symbol.Declaration.Span));
            }
        }

        foreach (var decl in functionDecls)
        {
            var symbol = BindFunctionSignature(decl, resolver);
            functions.Add(symbol);
            if (!globalScope.TryDeclare(symbol))
            {
                diagnostics.Report(new Diagnostic(DiagnosticSeverity.Error, $"'{decl.Name}' is already declared", decl.Span));
            }
        }

        globalScope.TryDeclare(BuiltinPrintSymbol);

        return (globalScope, structsByName.Values.ToList(), enumsByName.Values.ToList(), functions, resolver);
    }

    /// <summary>
    /// The one built-in every ADR-0011 minimal program needs, since
    /// <c>samples/hello.voy</c> (and every other minimal sample) calls
    /// it. Typed as <c>(String) -&gt; Void</c> only for now — real
    /// Swift's <c>print</c> takes any <c>CustomStringConvertible</c> and
    /// a variadic argument list, both out of scope until protocols and
    /// variadics are semantics-checked, which isn't part of the ADR-0011
    /// minimal subset.
    /// </summary>
    private static readonly FunctionSymbol BuiltinPrintSymbol = new(
        "print",
        Parameters: [new ParameterSymbol("value", ExternalLabel: null, PrimitiveType.String)],
        ReturnType: PrimitiveType.Void,
        Declaration: null!); // No source declaration — this is compiler-provided, not user-written.

    private FunctionSymbol BindFunctionSignature(FunctionDeclaration decl, TypeResolver resolver)
    {
        var parameters = decl.Parameters
            .Select(p => new ParameterSymbol(p.Name, p.ExternalLabel, resolver.Resolve(p.Type)))
            .ToList();

        // No `-> Type` written means implicit Void, per Parsing.Ast.cs's
        // own FunctionDeclaration.ReturnType remarks.
        var returnType = decl.ReturnType is null ? PrimitiveType.Void : resolver.Resolve(decl.ReturnType);

        return new FunctionSymbol(decl.Name, parameters, returnType, decl);
    }

    private List<PropertySymbol> BindStructProperties(StructDeclaration decl, TypeResolver resolver)
    {
        var properties = new List<PropertySymbol>();

        foreach (var member in decl.Members)
        {
            switch (member)
            {
                case BindingStatement { DeclaredType: not null } binding:
                    properties.Add(new PropertySymbol(binding.Name, resolver.Resolve(binding.DeclaredType), binding.IsMutable));
                    break;

                case BindingStatement { DeclaredType: null } binding:
                    // Per ADR-0011's minimal subset, a struct property
                    // needs an explicit `: Type` — inferring a stored
                    // property's type from its initializer expression
                    // (as Parsing/ already allows for ordinary local
                    // `let`/`var`) isn't implemented for struct members
                    // yet, since it would need Binder's expression-typing
                    // logic to run *before* DeclarationBinder's signature
                    // pass has finished, which breaks the forward-
                    // reference guarantee this class exists to provide.
                    diagnostics.Report(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"struct property '{binding.Name}' needs an explicit type annotation (e.g. 'var {binding.Name}: Int') — inferring it from an initializer isn't supported yet",
                        binding.Span));
                    break;

                default:
                    // Methods, computed properties, etc. — all outside
                    // ADR-0011's minimal subset (structs have stored
                    // properties only for now).
                    diagnostics.Report(new Diagnostic(
                        DiagnosticSeverity.Error,
                        $"struct member of kind '{member.GetType().Name}' is not yet supported by Semantics/ — only stored properties (var/let with a type) are resolved so far",
                        member.Span));
                    break;
            }
        }

        return properties;
    }

    private List<EnumCaseSymbol> BindEnumCases(EnumDeclaration decl, TypeResolver resolver)
    {
        var cases = new List<EnumCaseSymbol>();

        foreach (var member in decl.Members)
        {
            if (member is not CaseDeclaration caseDecl)
            {
                diagnostics.Report(new Diagnostic(
                    DiagnosticSeverity.Error,
                    $"enum member of kind '{member.GetType().Name}' is not yet supported by Semantics/ — only 'case' declarations are resolved so far",
                    member.Span));
                continue;
            }

            var associatedValues = caseDecl.AssociatedValues
                .Select(v => new AssociatedValueSlot(v.Label, resolver.Resolve(v.Type)))
                .ToList();
            cases.Add(new EnumCaseSymbol(caseDecl.Name, associatedValues));
        }

        return cases;
    }
}
