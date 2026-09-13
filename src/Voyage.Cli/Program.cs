using System.Reflection;
using Voyage.Compiler.CodeGen;
using Voyage.Compiler.Diagnostics;
using Voyage.Compiler.Lexing;
using Voyage.Compiler.Lowering;
using Voyage.Compiler.Parsing;
using Voyage.Compiler.Semantics;

// The `voyage` command-line tool — deliberately minimal, matching
// ADR-0011's own minimal-pipeline philosophy: two commands (`run`,
// `build`), no project-manifest support, no `@main` entry-point
// convention (that needs attribute parsing, which Parsing/ doesn't
// implement yet — see this project's README), no REPL. Existing purely
// so working the compiler forward is easier: `voyage run` gives a
// one-command way to exercise the whole pipeline against a real .voy
// file, instead of every experiment needing its own throwaway test-
// harness invocation the way development did before this project
// existed.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var commandArgs = args[1..];

return command switch
{
    "run" => RunCommand(commandArgs),
    "build" => BuildCommand(commandArgs),
    "help" or "-h" or "--help" => HelpCommand(),
    _ => UnknownCommand(command),
};

int RunCommand(string[] commandArgs)
{
    if (commandArgs.Length < 1)
    {
        Console.Error.WriteLine("Usage: voyage run <file.voy>");
        return 1;
    }

    var path = commandArgs[0];
    var lowered = Compile(path);
    if (lowered is null)
    {
        return 1;
    }

    var assemblyName = Path.GetFileNameWithoutExtension(path);
    var (_, programType) = CodeGenerator.EmitInMemory(lowered, assemblyName);
    var main = programType.GetMethod("Main")!;

    try
    {
        main.Invoke(null, null);
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        // Unwrap: the raw TargetInvocationException wrapping (an
        // artifact of invoking via reflection) is implementation detail
        // the person running `voyage run` shouldn't have to see —
        // ex.InnerException is the actual runtime failure (e.g. the
        // InvalidProgramException a non-exhaustive switch can produce;
        // see CodeGen/README.md's open items).
        Console.Error.WriteLine($"Runtime error: {ex.InnerException.Message}");
        return 1;
    }

    return 0;
}

int BuildCommand(string[] commandArgs)
{
    if (commandArgs.Length < 1)
    {
        Console.Error.WriteLine("Usage: voyage build <file.voy> [-o <output.dll>]");
        return 1;
    }

    var path = commandArgs[0];
    string? explicitOutput = null;

    for (var i = 1; i < commandArgs.Length; i++)
    {
        if (commandArgs[i] == "-o" && i + 1 < commandArgs.Length)
        {
            explicitOutput = commandArgs[i + 1];
            i++;
        }
    }

    var lowered = Compile(path);
    if (lowered is null)
    {
        return 1;
    }

    var assemblyName = Path.GetFileNameWithoutExtension(path);
    var outputPath = explicitOutput ?? Path.ChangeExtension(path, ".dll");

    CodeGenerator.EmitToFile(lowered, assemblyName, outputPath);
    WriteRuntimeConfig(outputPath);
    Console.WriteLine($"Wrote {outputPath}");
    return 0;
}

/// <summary>
/// A bare <c>PersistedAssemblyBuilder</c>-produced <c>.dll</c> has no
/// <c>.runtimeconfig.json</c> alongside it — unlike a normal
/// <c>dotnet build</c> output, which always generates one — so
/// <c>dotnet &lt;output&gt;.dll</c> refuses to run it on its own
/// ("self-contained app" error, since it can't find a framework to
/// run against). Found for real by actually trying to run a `voyage
/// build`-produced .dll the way a person would, not just checking it
/// exists. Writing this minimal companion file is what real `dotnet
/// build` generates for a framework-dependent app targeting the
/// currently-running framework version — reading that version from
/// <see cref="Environment.Version"/> rather than hardcoding it keeps
/// this correct if the SDK this CLI itself runs on ever changes.
/// </summary>
void WriteRuntimeConfig(string dllPath)
{
    var configPath = Path.ChangeExtension(dllPath, ".runtimeconfig.json");
    var frameworkVersion = Environment.Version.ToString();
    var json = $$"""
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "{{frameworkVersion}}"
            }
          }
        }
        """;
    File.WriteAllText(configPath, json);
}

int HelpCommand()
{
    PrintUsage();
    return 0;
}

int UnknownCommand(string name)
{
    Console.Error.WriteLine($"Unknown command '{name}'.");
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

void PrintUsage()
{
    Console.WriteLine("""
        voyage — the voyage-lang compiler (minimal pipeline, ADR-0011)

        Usage:
          voyage run <file.voy>               Compile and run a source file
          voyage build <file.voy> [-o <path>]  Compile to a runnable .dll
          voyage help                          Show this message

        Only the top-level-statements entry-point convention (ADR-0003)
        is supported right now — a file's statements run top to bottom,
        no `@main`-attributed type required. The `@main` convention
        needs attribute parsing, which Parsing/ doesn't implement yet.
        """);
}

/// <summary>
/// Runs the full pipeline — Lexing/ → Parsing/ → Semantics/ →
/// Lowering/ — stopping at the first phase that reports any error, and
/// printing every diagnostic from whichever phase actually fails. This
/// mirrors the test suite's own "no diagnostics before proceeding"
/// pattern (see e.g. Voyage.Compiler.Tests' <c>BindSource</c>/
/// <c>LowerSource</c> helpers), just surfaced as real CLI output instead
/// of a test assertion.
/// </summary>
BoundCompilationUnit? Compile(string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return null;
    }

    var source = File.ReadAllText(path);

    var lexSink = new InMemoryDiagnosticSink();
    var tokens = Lexer.Tokenize(source, lexSink);
    if (!ReportAndContinue(path, lexSink))
    {
        return null;
    }

    var parseSink = new InMemoryDiagnosticSink();
    var unit = Parser.Parse(tokens, parseSink);
    if (!ReportAndContinue(path, parseSink))
    {
        return null;
    }

    var semanticSink = new InMemoryDiagnosticSink();
    var bound = SemanticAnalyzer.Analyze(unit, semanticSink);
    if (!ReportAndContinue(path, semanticSink))
    {
        return null;
    }

    var lowerSink = new InMemoryDiagnosticSink();
    var lowered = new Lowerer(lowerSink).Lower(bound);
    if (!ReportAndContinue(path, lowerSink))
    {
        return null;
    }

    return lowered;
}

/// <summary>Prints every diagnostic in <paramref name="sink"/> (not
/// just errors — a future phase emitting warnings should have them
/// surfaced here too, even though none currently do) and returns
/// whether the caller should proceed to the next phase.</summary>
bool ReportAndContinue(string path, InMemoryDiagnosticSink sink)
{
    foreach (var diagnostic in sink.Diagnostics)
    {
        Console.Error.WriteLine($"{path}: {diagnostic}");
    }

    return !sink.HasErrors;
}
