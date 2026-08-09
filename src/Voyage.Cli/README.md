# Voyage.Cli

Not yet started.

The `voyage` command-line tool (build/run/repl). `docs/spec/grammar.md`
Section 11 references a `Voyage.Cli` project manifest format (the
voyage-lang analogue of `.csproj`'s `<PackageReference>`) as an explicit
open item — that's tooling design, not language design, and belongs here
once this project exists.

Also owns enforcing the ADR-0003 default: scaffolded projects should
default to the `@main` entry-point convention, not bare top-level
statement files.
