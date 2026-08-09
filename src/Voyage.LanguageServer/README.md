# Voyage.LanguageServer

Not yet started.

LSP server for editor support. ADR-0007 flags one concrete piece of work
for this project already: since voyage-lang and referenced C# assemblies
resolve `import`s through the same CLR-metadata mechanism with no
syntactic marker distinguishing them, `Voyage.LanguageServer` needs its
own logic for "go to voyage-lang source" vs. "show decompiled/
metadata-only view" — the language itself doesn't disambiguate this at
the `import` site by design, so tooling has to.
