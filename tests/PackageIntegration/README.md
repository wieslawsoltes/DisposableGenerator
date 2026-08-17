# Packed-package integration tests

These are standalone consumer projects. Every project references `DisposableGenerator` through `PackageReference`; none may reference the generator project directly.

The runner copies the projects outside the repository, copies exactly one `.nupkg` into an isolated local NuGet source, clears all other package sources, and uses a fresh package cache. This proves that package analyzer assets and `buildTransitive` configuration work for real consumers.

| Project | Coverage |
|---|---|
| `SyncConsumer` | Owned/borrowed members, generated inheritance, hooks, fixed/dynamic order, concurrent idempotency, default late registration. |
| `AsyncConsumer` | Async-only and conjunctive modes, dual-capability preference, sync fallback, sync/async competition, async registration, aggregate failures. |
| `ConfiguredConsumer` | Renamed registration APIs, declaration/registration order, immediate late disposal, nullable disposable structs, hooks, aggregate failures, unmanaged cleanup, finalizer compilation. |
| `ModernCSharpConsumer` | .NET 10/preview parsing, class primary constructors, field-backed and partial properties, escaped identifiers, nested generated types in a generic record container. |
| `DiagnosticsConsumer` | Expected package diagnostics `DISP001`, `DISP007`, `DISP009`, `DISP010`, `DISP020`, and `DISP025`. |
| `OwnershipDiagnosticConsumer` | Compiler SARIF emission of the informational `DISP006` ownership audit. |

Pack before running locally:

```bash
dotnet pack src/DisposableGenerator/DisposableGenerator.csproj --configuration Release --output artifacts/packages
./eng/run-integration-tests.sh
```

The no-argument runner uses the newest package under `artifacts/packages` and tests `net10.0`. Pass an exact package and framework explicitly when needed:

```bash
./eng/run-integration-tests.sh artifacts/packages/DisposableGenerator.1.0.0.nupkg net8.0
./eng/run-integration-tests.sh artifacts/packages/DisposableGenerator.1.0.0.nupkg net9.0
./eng/run-integration-tests.sh artifacts/packages/DisposableGenerator.1.0.0.nupkg net10.0
```

The selected framework's SDK and targeting pack must be installed. CI runs all three frameworks on Linux, Windows, and macOS.
