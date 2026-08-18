using System.Text;

namespace Voyage.Compiler.Parsing;

/// <summary>
/// Renders an AST as indented, human-readable text — the mechanism the
/// first Parsing/ milestone (per Parsing/README.md) uses to "round-trip
/// through an AST dump" and be checked against an expected shape.
///
/// Not intended as voyage-lang source round-tripping (i.e. this doesn't
/// reproduce valid `.voy` syntax) — it's a structural dump for humans
/// and tests to verify against, the same role `-dump-ast` output plays
/// for Swift's own compiler.
/// </summary>
public static class AstPrinter
{
    public static string Print(AstNode node)
    {
        var sb = new StringBuilder();
        Write(node, sb, 0);
        return sb.ToString();
    }

    private static void Write(AstNode node, StringBuilder sb, int indent)
    {
        void Line(string text) => sb.Append(' ', indent * 2).Append(text).Append('\n');

        switch (node)
        {
            case CompilationUnit unit:
                Line($"CompilationUnit @ {unit.Span}");
                foreach (var stmt in unit.Statements)
                {
                    Write(stmt, sb, indent + 1);
                }
                break;

            case ExpressionStatement stmt:
                Line($"ExpressionStatement @ {stmt.Span}");
                Write(stmt.Expression, sb, indent + 1);
                break;

            case AssignmentStatement stmt:
                Line($"AssignmentStatement {stmt.Operator} @ {stmt.Span}");
                WriteLabeled("target", stmt.Target, sb, indent + 1);
                WriteLabeled("value", stmt.Value, sb, indent + 1);
                break;

            case UnsupportedStatement stmt:
                Line($"UnsupportedStatement @ {stmt.Span}");
                break;

            case BindingStatement stmt:
                Line($"BindingStatement {(stmt.IsMutable ? "var" : "let")} '{stmt.Name}' @ {stmt.Span}");
                if (stmt.DeclaredType is not null)
                {
                    WriteLabeled("declaredType", stmt.DeclaredType, sb, indent + 1);
                }
                if (stmt.Initializer is not null)
                {
                    WriteLabeled("initializer", stmt.Initializer, sb, indent + 1);
                }
                else
                {
                    Line2("initializer: (none)", sb, indent + 1);
                }
                break;

            case StructDeclaration structDecl:
                Line($"StructDeclaration '{structDecl.Name}' @ {structDecl.Span}");
                Line2($"genericParameters: {FormatGenericParameters(structDecl.GenericParameters)}", sb, indent + 1);
                Line2($"conformedProtocols: {FormatNameList(structDecl.ConformedProtocols)}", sb, indent + 1);
                Line2($"where: {FormatGenericParameters(structDecl.WhereConstraints)}", sb, indent + 1);
                WriteStatementList("members", structDecl.Members, sb, indent + 1);
                break;

            case EnumDeclaration enumDecl:
                Line($"EnumDeclaration '{enumDecl.Name}' @ {enumDecl.Span}");
                Line2($"genericParameters: {FormatGenericParameters(enumDecl.GenericParameters)}", sb, indent + 1);
                Line2($"conformedProtocols: {FormatNameList(enumDecl.ConformedProtocols)}", sb, indent + 1);
                Line2($"where: {FormatGenericParameters(enumDecl.WhereConstraints)}", sb, indent + 1);
                WriteStatementList("members", enumDecl.Members, sb, indent + 1);
                break;

            case ProtocolDeclaration protocolDecl:
                Line($"ProtocolDeclaration '{protocolDecl.Name}' @ {protocolDecl.Span}");
                Line2($"genericParameters: {FormatGenericParameters(protocolDecl.GenericParameters)}", sb, indent + 1);
                Line2($"inheritedProtocols: {FormatNameList(protocolDecl.InheritedProtocols)}", sb, indent + 1);
                Line2($"where: {FormatGenericParameters(protocolDecl.WhereConstraints)}", sb, indent + 1);
                WriteStatementList("members", protocolDecl.Members, sb, indent + 1);
                break;

            case ExtensionDeclaration extensionDecl:
                Line($"ExtensionDeclaration '{extensionDecl.ExtendedType}' @ {extensionDecl.Span}");
                Line2($"genericParameters: {FormatGenericParameters(extensionDecl.GenericParameters)}", sb, indent + 1);
                Line2($"conformedProtocols: {FormatNameList(extensionDecl.ConformedProtocols)}", sb, indent + 1);
                Line2($"where: {FormatGenericParameters(extensionDecl.WhereConstraints)}", sb, indent + 1);
                WriteStatementList("members", extensionDecl.Members, sb, indent + 1);
                break;

            case CaseDeclaration caseDecl:
                Line($"CaseDeclaration '{caseDecl.Name}' @ {caseDecl.Span}");
                if (caseDecl.AssociatedValues.Count == 0)
                {
                    Line2("associatedValues: (none)", sb, indent + 1);
                }
                else
                {
                    Line2("associatedValues:", sb, indent + 1);
                    foreach (var p in caseDecl.AssociatedValues)
                    {
                        Write(p, sb, indent + 2);
                    }
                }
                break;

            case FunctionDeclaration fn:
                Line($"FunctionDeclaration '{fn.Name}' @ {fn.Span}");
                Line2($"genericParameters: {FormatGenericParameters(fn.GenericParameters)}", sb, indent + 1);
                if (fn.Parameters.Count == 0)
                {
                    Line2("parameters: (none)", sb, indent + 1);
                }
                else
                {
                    Line2("parameters:", sb, indent + 1);
                    foreach (var p in fn.Parameters)
                    {
                        Write(p, sb, indent + 2);
                    }
                }
                if (fn.ReturnType is not null)
                {
                    WriteLabeled("returnType", fn.ReturnType, sb, indent + 1);
                }
                else
                {
                    Line2("returnType: (none, implicit Void)", sb, indent + 1);
                }
                Line2($"where: {FormatGenericParameters(fn.WhereConstraints)}", sb, indent + 1);
                if (fn.Body is not null)
                {
                    WriteStatementList("body", fn.Body, sb, indent + 1);
                }
                else
                {
                    Line2("body: (none — protocol requirement)", sb, indent + 1);
                }
                break;

            case Parameter p:
                Line($"Parameter externalLabel='{p.ExternalLabel ?? "(none)"}' name='{p.Name}' @ {p.Span}");
                Write(p.Type, sb, indent + 1);
                break;

            case TypeNode t:
                Line($"TypeNode '{FormatType(t)}' @ {t.Span}");
                break;

            case ReturnStatement ret:
                Line($"ReturnStatement @ {ret.Span}");
                if (ret.Value is not null)
                {
                    WriteLabeled("value", ret.Value, sb, indent + 1);
                }
                else
                {
                    Line2("value: (none)", sb, indent + 1);
                }
                break;

            case IfStatement ifStmt:
                Line($"IfStatement @ {ifStmt.Span}");
                WriteLabeled("condition", ifStmt.Condition, sb, indent + 1);
                WriteStatementList("then", ifStmt.ThenBranch, sb, indent + 1);
                if (ifStmt.ElseBranch is not null)
                {
                    WriteStatementList("else", ifStmt.ElseBranch, sb, indent + 1);
                }
                break;

            case WhileStatement whileStmt:
                Line($"WhileStatement @ {whileStmt.Span}");
                WriteLabeled("condition", whileStmt.Condition, sb, indent + 1);
                WriteStatementList("body", whileStmt.Body, sb, indent + 1);
                break;

            case BreakStatement brk:
                Line($"BreakStatement @ {brk.Span}");
                break;

            case ContinueStatement cont:
                Line($"ContinueStatement @ {cont.Span}");
                break;

            case CallExpression call:
                Line($"CallExpression @ {call.Span}");
                WriteLabeled("callee", call.Callee, sb, indent + 1);
                WriteExpressionList("arguments", call.Arguments, sb, indent + 1);
                break;

            case MemberAccessExpression member:
                Line($"MemberAccessExpression '.{member.MemberName}' @ {member.Span}");
                WriteLabeled("target", member.Target, sb, indent + 1);
                break;

            case SubscriptExpression sub:
                Line($"SubscriptExpression @ {sub.Span}");
                WriteLabeled("target", sub.Target, sb, indent + 1);
                WriteExpressionList("arguments", sub.Arguments, sb, indent + 1);
                break;

            case IdentifierExpression id:
                Line($"IdentifierExpression '{id.Name}' @ {id.Span}");
                break;

            case SelfExpression self:
                Line($"SelfExpression @ {self.Span}");
                break;

            case StringLiteralExpression str:
                Line($"StringLiteralExpression \"{str.Value}\" @ {str.Span}");
                break;

            case IntegerLiteralExpression i:
                Line($"IntegerLiteralExpression {i.Value} @ {i.Span}");
                break;

            case FloatLiteralExpression f:
                Line($"FloatLiteralExpression {f.Value} @ {f.Span}");
                break;

            case BooleanLiteralExpression b:
                Line($"BooleanLiteralExpression {b.Value} @ {b.Span}");
                break;

            case NilLiteralExpression n:
                Line($"NilLiteralExpression @ {n.Span}");
                break;

            case ParenthesizedExpression paren:
                Line($"ParenthesizedExpression @ {paren.Span}");
                Write(paren.Inner, sb, indent + 1);
                break;

            case BinaryExpression bin:
                Line($"BinaryExpression {bin.Operator} @ {bin.Span}");
                WriteLabeled("left", bin.Left, sb, indent + 1);
                WriteLabeled("right", bin.Right, sb, indent + 1);
                break;

            case UnaryExpression un:
                Line($"UnaryExpression {un.Operator} @ {un.Span}");
                WriteLabeled("operand", un.Operand, sb, indent + 1);
                break;

            case ErrorExpression err:
                Line($"ErrorExpression @ {err.Span}");
                break;

            default:
                Line($"<unhandled node type {node.GetType().Name}> @ {node.Span}");
                break;
        }
    }

    private static void WriteLabeled(string label, AstNode node, StringBuilder sb, int indent)
    {
        sb.Append(' ', indent * 2).Append(label).Append(":\n");
        Write(node, sb, indent + 1);
    }

    /// <summary>Prints a labeled list of statements (function/if/while
    /// bodies), or "(none)"/"(empty)" style placeholder when there are
    /// none — factored out since function bodies, if-branches, and while
    /// bodies all share this exact shape.</summary>
    private static void WriteStatementList(string label, IReadOnlyList<Statement> statements, StringBuilder sb, int indent)
    {
        if (statements.Count == 0)
        {
            Line2($"{label}: (empty)", sb, indent);
            return;
        }

        Line2($"{label}:", sb, indent);
        foreach (var s in statements)
        {
            Write(s, sb, indent + 1);
        }
    }

    /// <summary>Same shape as <see cref="WriteStatementList"/> but for
    /// expression lists — call/subscript argument lists.</summary>
    private static void WriteExpressionList(string label, IReadOnlyList<Expression> expressions, StringBuilder sb, int indent)
    {
        if (expressions.Count == 0)
        {
            Line2($"{label}: (none)", sb, indent);
            return;
        }

        Line2($"{label}:", sb, indent);
        foreach (var e in expressions)
        {
            Write(e, sb, indent + 1);
        }
    }

    private static void Line2(string text, StringBuilder sb, int indent) =>
        sb.Append(' ', indent * 2).Append(text).Append('\n');

    /// <summary>Formats a `: A, B` conformance/inheritance clause's name
    /// list for a single-line dump entry, or "(none)" if empty.</summary>
    private static string FormatNameList(IReadOnlyList<string> names) =>
        names.Count == 0 ? "(none)" : string.Join(", ", names);

    /// <summary>Formats a `&lt;T, U: Protocol&gt;` generic-parameter list
    /// or `where T: Protocol` clause's constraints for a single-line
    /// dump entry, or "(none)" if empty. Shared by both uses since
    /// `TypeConstraint` covers both shapes — see its remarks in
    /// Ast.cs.</summary>
    private static string FormatGenericParameters(IReadOnlyList<TypeConstraint> constraints)
    {
        if (constraints.Count == 0)
        {
            return "(none)";
        }
        return string.Join(", ", constraints.Select(c =>
            c.ConformedProtocols.Count == 0
                ? c.TypeName
                : $"{c.TypeName}: {string.Join(" & ", c.ConformedProtocols)}"));
    }

    /// <summary>Renders a <see cref="TypeNode"/> back into a compact,
    /// source-like single-line string (e.g. `[Int]?`, `(Int) -> String`,
    /// `any Drawable &amp; Equatable`) for a single dump line, rather than
    /// recursing through the general `Write` tree-printer — a type
    /// expression reads far better compact than deeply nested.</summary>
    private static string FormatType(TypeNode type)
    {
        var core = type switch
        {
            NamedTypeNode n when n.GenericArguments.Count == 0 => n.Name,
            NamedTypeNode n => $"{n.Name}<{string.Join(", ", n.GenericArguments.Select(FormatType))}>",
            ArrayTypeNode a => $"[{FormatType(a.ElementType)}]",
            FunctionTypeNode f => $"({string.Join(", ", f.ParameterTypes.Select(FormatType))}) -> {FormatType(f.ReturnType)}",
            ExistentialTypeNode e => $"any {string.Join(" & ", e.Protocols)}",
            OpaqueTypeNode o => $"some {string.Join(" & ", o.Protocols)}",
            SelfTypeNode => "Self",
            _ => $"<unhandled TypeNode {type.GetType().Name}>",
        };
        return type.IsOptional ? $"{core}?" : core;
    }
}
