# ADR-0007: Module/Import Syntax — Swift-Inspired, CLR-Metadata-Resolved

- Status: Accepted
- Date: 2026-08-08
- Related: ADR-0001 (Swift-inspired syntax), grammar.md Section 11

## Context

Module/import syntax was flagged as fully undrafted since the earliest
version of `grammar.md`. It blocks real progress on several fronts: no
multi-file sample can be written, the `@main` structured-program example
from ADR-0003 has nowhere to `import` its dependencies from, and
`Voyage.Compiler` can't define what a compilation unit even is without it.

The requirement driving the design: voyage-lang's import syntax should be
Swift-inspired at the declaration level, but it must also support importing
**legacy C# libraries** directly — this is a hard requirement, not a nice-
to-have, since voyage-lang's entire reason for targeting CIL/.NET rather
than a Rust-based runtime (see the original Nova post-mortem motivating
this whole project) was to get first-class interop with the existing
.NET ecosystem.

### Why this is structurally different from Aurelia's or Swift's C interop

This is the key insight that shapes the whole design. Aurelia interops with
C++ because it *is* C++23 at its core — no import bridging needed, but also
no cross-runtime story. Swift interops with C via the **ClangImporter**: a
real, substantial piece of the Swift compiler that parses C/Objective-C
headers and synthesizes Swift-visible declarations from them, because C and
Swift do not share a runtime type system or metadata format.

voyage-lang's situation with C# is fundamentally easier than either of
those precedents: **voyage-lang and C# already share the same runtime type
system**, because both compile to CIL and both describe their public
surface using the same CLR assembly metadata format (the same mechanism
that already lets C#, F#, VB.NET, and every other CLR language interoperate
with each other with zero bridging layers, reflection-based tooling, or
header-parsing steps). Importing "a legacy C# library" is not meaningfully
different, at the metadata level, from importing another voyage-lang
module — both are just CLR assemblies exposing CLR types. There is nothing
for voyage-lang to build that resembles a ClangImporter; the CLR itself
already did that unification work decades ago.

This means the import *design* question isn't "how do we bridge two type
systems" (already solved by the CLR) — it's purely "what should the
*syntax* look like," which is where Swift's precedent is genuinely useful.

## Decision

voyage-lang adopts Swift's import declaration syntax, resolved against CLR
assembly metadata instead of `.swiftmodule` files:

```voyage
import Voyage.Collections        // a voyage-lang-native module
import System.Text.Json          // a referenced C# (or any CLR-language) assembly
```

**No syntactic distinction between voyage-lang-native and C#-native
imports.** Both resolve through the same mechanism, because at the CLR
metadata level there is no difference to expose — an assembly is an
assembly regardless of the compiler that produced it. This is a deliberate
design choice, not an oversight: introducing a separate import form for
"foreign" C# libraries would invent ceremony the underlying platform
doesn't require.

**Scoped imports**, matching Swift's `import <kind> Module.Symbol` form:

```voyage
import struct System.Guid
import class System.Text.StringBuilder
import func System.Math.Sqrt
```

**Access levels map directly onto real CLR accessibility modifiers**:

| voyage-lang | CLR equivalent | Scope |
|---|---|---|
| `public` | `public` | Any referencing assembly |
| `internal` | `internal` | Same compiled module/assembly |
| `fileprivate` | erased to `internal`/`private` | Declaring source file (voyage-lang-level concept only — CLR has no file-scoped visibility) |
| `private` | `private` | Declaring type |

Because `public`/`internal` are real CLR modifiers rather than a voyage-
lang-specific abstraction translated at a boundary, interop is
**bidirectional by construction**: a `public` voyage-lang type is
consumable from an ordinary C# project referencing the compiled
voyage-lang assembly with zero extra ceremony on the C# side, same as
consuming any other .NET library.

## Consequences

- **Positive:** "Importing legacy C# libraries" requires no new compiler
  subsystem comparable to Swift's ClangImporter — `Voyage.Compiler` only
  needs an assembly-metadata reader, which is a well-trodden, mature part
  of the .NET tooling ecosystem (`System.Reflection.Metadata` and
  similar), not something voyage-lang has to invent.
- **Positive:** Bidirectional interop (C# can consume voyage-lang output
  as easily as voyage-lang consumes C#) falls out for free from sharing
  real CLR accessibility semantics, rather than needing to be designed
  and implemented as a separate feature.
- **Positive:** Reuses Swift's already-battle-tested import declaration
  syntax (whole-module and scoped-symbol forms) rather than inventing a
  new one, consistent with ADR-0001's overall direction.
- **Negative / open risk:** Because there's no syntactic marker
  distinguishing a voyage-lang module from a foreign C# assembly at the
  `import` site, tooling (`Voyage.LanguageServer`) needs to do this
  distinction work itself for things like "go to voyage-lang source" vs.
  "show decompiled/metadata-only view" — a tooling concern, not a language
  design flaw, but worth flagging for `Voyage.LanguageServer` design work.
- **Follow-up, not yet decided:** whether a Swift-style `@testable import`
  or `@_exported import` equivalent is needed. Deferred as non-blocking —
  neither affects the core interop story this ADR resolves.
- **Follow-up, out of scope for this ADR:** the package/project manifest
  format for declaring which assemblies a `Voyage.Cli` project references
  (the voyage-lang analogue of `.csproj`'s `<PackageReference>`) is a
  tooling concern, not a language-syntax one.

## Alternatives Considered

- **A distinct import form for C# libraries** (e.g. `import csharp
  System.Text.Json` or similar) — rejected. Would introduce a false
  distinction the CLR metadata layer doesn't have, adding syntax ceremony
  for no real benefit, and would need updating if/when other CLR
  languages (F#, VB.NET) are imported too — the unmarked form scales to
  "any CLR assembly" for free.
- **A Swift-ClangImporter-style bridging/synthesis layer** — rejected as
  unnecessary; this solves a type-system-unification problem that C#
  interop on the CLR doesn't have. Building one anyway would be pure
  wasted implementation effort mirroring a problem that doesn't exist in
  voyage-lang's actual target environment.
- **Aurelia-style single-ABI/gRPC boundary** for cross-language calls
  (the pattern Nova's post-mortem specifically recommended for Aurelia's
  C++ core) — rejected for voyage-lang/C# specifically, since that
  recommendation was aimed at genuinely disjoint runtimes (C ABI across
  language boundaries with no shared metadata). It doesn't apply here
  because CIL/CLR metadata already *is* the shared boundary.
