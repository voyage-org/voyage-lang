# .github/workflows

Not yet started. CI (build, test, NativeAOT publish per RID) is deferred
until there's a working build across all five compiler phases — a CI
pipeline for `Lexing/` alone, with everything else stubbed, wouldn't yet
catch the failures that matter most.

Note for whoever sets this up: `tests/Voyage.Compiler.Tests` currently
runs as a plain console app (`dotnet run`) rather than via `dotnet test`,
since it predates NuGet access being available (see root README's
"Why a hand-rolled smoke test" note). A CI workflow should account for
that, or migrate the test project to a real test framework first.
