# voyage-lang

> A standalone, curiosity-driven programming language targeting the .NET runtime.

![Voyage lang logo](http://raw.githubusercontent.com/DeepcometAI/assets/refs/heads/main/images/logo/vdotnet.svg)

**voyage-lang** (`.voy`) is an independent language project built for exploration — not scoped to power any particular product, OS, or downstream consumer. It exists to explore what a modern, CLR-targeted language can look like when designed from scratch, unconstrained by legacy compatibility or a specific application domain.

Part of [voyage-org](https://github.com/voyage-org), Deepcomet's third branch reserved for from-scratch, curiosity-driven language and systems work — distinct from the mainline [Deepcomet AI](https://ai.deepcomet.space) product line and Project Neutrino's OS/kernel research.

---

## Philosophy

- **Standalone by design.** voyage-lang isn't the application language for Voyage OS, isn't a scripting layer for anything else in the Deepcomet ecosystem, and carries no obligations to serve another project's roadmap. It is decoupled on purpose.
- **One runtime, no FFI boundary.** Compiles to CIL and runs on a single managed runtime end to end. No cross-language boundary between a native core and a managed shell — a deliberate lesson carried forward from earlier language work (Nova), where a fragile C↔Rust FFI boundary proved to be the project's fatal flaw.
- **Spec before code.** The language is designed on paper first. `docs/spec/` is the source of truth; the compiler is an implementation of the spec, not the other way around.
- **Modern .NET, taken seriously.** Built against .NET 9/10, fully open-source and cross-platform, with NativeAOT and musl runtime identifiers treated as first-class targets from day one — not bolted on later.

---

## Project Status

🚧 **Early design phase.** Language spec and compiler architecture are actively being drafted. No stable release yet. Expect breaking changes to everything, including this README.

---

## Project Structure

```
voyage-lang/
│
├── src/
│   ├── Voyage.Compiler/          # Core compiler library
│   │   ├── Lexing/
│   │   ├── Parsing/               → AST
│   │   ├── Semantics/             → type checking, name resolution
│   │   ├── Lowering/              → AST → IR
│   │   ├── CodeGen/                → IR → CIL
│   │   └── Diagnostics/           # error reporting, source spans
│   │
│   ├── Voyage.Runtime/            # Runtime support shipped with compiled programs
│   │   ├── Core/                  # builtin types, collections
│   │   └── Interop/                # .NET BCL interop shims
│   │
│   ├── Voyage.Cli/                 # `voyage` command-line tool (build/run/repl)
│   │
│   └── Voyage.LanguageServer/      # LSP server (editor support)
│
├── tests/
│   ├── Voyage.Compiler.Tests/
│   ├── Voyage.Runtime.Tests/
│   └── Voyage.Integration.Tests/
│
├── samples/                        # example .voy programs
│
├── docs/
│   ├── spec/                       # language specification
│   │   ├── grammar.md
│   │   ├── type-system.md
│   │   └── memory-model.md
│   └── design-notes/               # architecture decision records
│
├── tools/
│   └── grammar-gen/
│
├── .github/workflows/              # CI: build, test, NativeAOT publish per RID
│
├── voyage-lang.sln
├── Directory.Build.props
└── README.md
```

The compiler is built as a **library first, CLI second** (`Voyage.Compiler` + a thin `Voyage.Cli` wrapper), so the same pipeline can later be reused by a language server, REPL, or analyzer tooling without duplication.

---

## Getting Started

> Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/) or later.

```bash
git clone https://github.com/voyage-org/voyage-lang.git
cd voyage-lang
dotnet build
```

Run a `.voy` file (once the CLI supports it):

```bash
dotnet run --project src/Voyage.Cli -- run samples/hello.voy
```

### Hello, world

```voy
print("Hello, Voyage.")
```

*(Syntax is provisional and subject to change as the spec evolves.)*

---

## Design Goals

| Goal | Approach |
|---|---|
| Single-runtime execution | Compiles to CIL, runs on CoreCLR — no native/managed FFI boundary |
| Portable, self-contained binaries | NativeAOT with `linux-musl-x64` / `linux-musl-arm64` RIDs treated as primary targets, not an afterthought |
| Predictable tooling | Compiler exposed as a reusable library from day one (LSP, REPL, CLI all share one pipeline) |
| Documented intent | Every non-obvious design decision recorded as an ADR under `docs/design-notes/` |

---

## Non-Goals

- voyage-lang is **not** an application language for Voyage OS, and Voyage OS's design does not constrain it.
- It is **not** a fork or a fresh attempt at reviving [Nova AI Lang](https://github.com/DeepcometAI/nova-ai-lang) — no code is carried forward.
- It is **not** a sibling of [Aurelia / Aurelia.NET](https://github.com/DeepcometAI/aurelia) — separate lineage, separate design principles, separate org.

---

## Building

```
dotnet build
dotnet run --project tests/Voyage.Compiler.Tests
```

Requires the .NET 10 SDK. `tests/Voyage.Compiler.Tests` currently runs as
a plain console app rather than via `dotnet test` — see its directory for
why, if you're wondering.

---

## Roadmap

- [x] `docs/spec/grammar.md`, `type-system.md`, `memory-model.md` — first drafts complete, backed by 10 ADRs
- [x] Lexer → working token stream (`src/Voyage.Compiler/Lexing/`), 46/46 checks passing
- [x] Parser first milestone → `samples/hello.voy` round-trips to an AST dump (`src/Voyage.Compiler/Parsing/`), 67/67 checks passing across Lexing/ + Parsing/
- [x] Parser: `let`/`var` bindings, binary/unary operators with full precedence table (`grammar.md`'s "Operator Precedence and Associativity", verified against Swift's real `precedencegroup` chain) — 91/91 checks passing
- [x] Parser: `func` declarations (params, optional `-> Type` return type, `{ ... }` body), `return` statements, minimal type references (`Int`, `String?`) — 106/106 checks passing
- [x] Parser: `if`/`else`/`else-if`, `while`, `break`/`continue` — 120/120 checks passing
- [x] Parser: `struct`/`enum`/`case` declarations, full `let`/`var` type annotations (including annotation-only bindings) — 136/136 checks passing
- [x] Parser: assignment statements (`=`, `+=`, `-=`, `*=`, `/=`) — 142/142 checks passing
- [x] Parser: `protocol`/`extension` declarations, conformance/inheritance clauses (also retrofitted onto `struct`/`enum`) — 152/152 checks passing
- [x] Parser: generics — `<T>`, `<T: Protocol & Protocol>`, `where` clauses, parameter labels (`_ name: Type`, `external internal: Type`) — 174/174 checks passing
- [x] Parser: member access (`.`), subscripting (`[...]`), `self` — 192/192 checks passing
- [x] Parser: richer type syntax — `[T]`, `(Int) -> Bool`, `Stack<Int>`, `any`/`some`, `Self` (all nesting freely) — 216/216 checks passing
- [x] Parser: string interpolation (`"Hello, \(name)!"`), including nested calls and member access inside interpolations — 228/228 checks passing
- [ ] Parser: `switch`, `for`-`in`
- [ ] Semantic analysis (type checking, name resolution)
- [ ] CIL code generation, first runnable `.voy` program
- [ ] `voyage` CLI: `build`, `run`, `repl`
- [ ] NativeAOT publish pipeline across target RIDs (CI)
- [ ] Language server (LSP) for editor support
- [ ] Public v0.1 release

---

## Contributing

voyage-lang is currently in its design phase and evolving quickly. Design discussion happens in `docs/design-notes/` before implementation — if you're interested in contributing, start there rather than opening a PR against `src/`.

---

## License

[MIT LICENSE](LICENSE)

---

## Part of Deepcomet

voyage-lang is developed under **[voyage-org](https://github.com/voyage-org)**, the curiosity-driven third branch of the [Deepcomet](https://deepcomet.space) ecosystem, alongside:

- **[Deepcomet AI](https://ai.deepcomet.space)** — AI software and scientific packages
- **[Deepcomet Science](https://science.deepcomet.space)** — frontier research, FSA, visualization
