# Voyage.Cli

Implements a minimal `voyage` command-line tool: `run` and `build`,
tying together the whole pipeline (`Lexing/` → `Parsing/` → `Semantics/`
→ `Lowering/` → `CodeGen/`) behind two commands. Exists so working the
compiler forward is easier — a one-command way to actually run a real
`.voy` file, instead of every experiment needing its own throwaway
test-harness invocation.

## Commands

- **`voyage run <file.voy>`** — compiles and runs a file in-process
  (`CodeGenerator.EmitInMemory` + reflection `Invoke` on the emitted
  `Main`).
- **`voyage build <file.voy> [-o <path>]`** — compiles to a genuinely
  standalone-runnable `.dll` (`CodeGenerator.EmitToFile`), plus a
  companion `.runtimeconfig.json` so `dotnet <output>.dll` works without
  any extra setup.
- **`voyage help`** — usage text.

Only the top-level-statements entry-point convention (ADR-0003) is
supported — a file's statements run top to bottom, no wrapper required.
The `@main`-attributed-type convention (ADR-0003's other form, and the
one `Voyage.Cli` scaffolding is meant to default to per this file's
original placeholder note) needs attribute parsing, which `Parsing/`
doesn't implement yet.

Diagnostics from any failing phase are printed (via `Diagnostic`'s own
`ToString()`) and the pipeline stops before the next phase runs — the
same "don't cascade past a failing phase" rule the test suite's own
`BindSource`/`LowerSource` helpers already follow, just surfaced as real
CLI output instead of a test assertion.

## Two real bugs found by actually using this, not by any earlier test

Both were invisible to the 360+ tests that existed before this project,
because neither one is the kind of thing "does the pipeline run and
produce a plausible result" testing catches — they only showed up once
a real person's workflow (build something, then run it the ordinary
way) was actually exercised:

1. **`TypeSymbol.ToString()` wasn't being used.** Every diagnostic
   mentioning a type printed the C# record default
   (`PrimitiveType { Name = Int }`) instead of the intended `Int` — the
   base record's `ToString()` override needed to be `sealed` to stop
   every derived record from synthesizing its own. Fixed in
   `Semantics/Types.cs`. No earlier test ever inspected diagnostic
   *message text*, only counted diagnostics, so this was invisible until
   `voyage run` actually printed one to a terminal.
2. **`voyage build`'s output couldn't be run via plain `dotnet
   <output>.dll`.** `PersistedAssemblyBuilder.Save()` alone produces a
   loadable assembly with no CLR entry point token set — `dotnet` refuses
   to run it ("Entry point not found"). The fix needed the lower-level,
   documented pattern: `GenerateMetadata` + `ManagedPEBuilder` with an
   explicit `entryPoint` pointing at the emitted `Main` method. Fixed in
   `CodeGen/CodeGenerator.cs`'s `EmitToFile`, with a new automated test
   (checking `Assembly.EntryPoint` directly) added so this specific
   regression can't silently return — the existing persisted-assembly
   test invoked `Main` directly via reflection, which works with or
   without a real entry point set, so it hadn't caught this either.

## What's still open

- **No `repl` command** — not attempted yet; a REPL needs incremental
  compilation (re-binding new statements against previously-declared
  names across separate `voyage run`-style invocations), which is a
  distinct, larger piece of work from `run`/`build`.
- **No project-manifest support** — `grammar.md` Section 11 flags a
  `Voyage.Cli` project manifest format (the voyage-lang analogue of
  `.csproj`) as an open item; this CLI only ever compiles a single file.
- **No `@main` scaffolding** — blocked on attribute parsing (see above).
- **Minimal diagnostic formatting** — reuses `Diagnostic.ToString()`
  as-is (`error [line:col]: message`), no source-line rendering or caret
  pointing at the exact span. `Diagnostics/`'s own README already notes
  richer formatting is deliberately deferred past this milestone.
- **No NativeAOT publish** — explicitly deferred, per
  `Directory.Build.props`'s own comment; this CLI runs via `dotnet run`/
  the ordinary framework-dependent `dotnet <assembly>.dll` path for now.
- **Discovered, not fixed here**: there's currently no way to print or
  interpolate a computed `Int`/`Double`/`Bool` value at all — see
  `Semantics/README.md`'s open items for the real gap (no
  `String`-conversion path for non-`String` types) this surfaced.
