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

            case UnsupportedStatement stmt:
                Line($"UnsupportedStatement @ {stmt.Span}");
                break;

            case BindingStatement stmt:
                Line($"BindingStatement {(stmt.IsMutable ? "var" : "let")} '{stmt.Name}' @ {stmt.Span}");
                WriteLabeled("initializer", stmt.Initializer, sb, indent + 1);
                break;

            case CallExpression call:
                Line($"CallExpression @ {call.Span}");
                WriteLabeled("callee", call.Callee, sb, indent + 1);
                if (call.Arguments.Count == 0)
                {
                    Line2("arguments: (none)", sb, indent + 1);
                }
                else
                {
                    Line2("arguments:", sb, indent + 1);
                    foreach (var arg in call.Arguments)
                    {
                        Write(arg, sb, indent + 2);
                    }
                }
                break;

            case IdentifierExpression id:
                Line($"IdentifierExpression '{id.Name}' @ {id.Span}");
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

    private static void Line2(string text, StringBuilder sb, int indent) =>
        sb.Append(' ', indent * 2).Append(text).Append('\n');
}
