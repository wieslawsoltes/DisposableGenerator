# DisposableGenerator agent contract

This file is the repository-wide implementation contract. It applies to every source, test, sample, package, documentation, and workflow change. Treat **must**, **must not**, and **required** as mandatory.

## Project intent

DisposableGenerator is a compile-time-only incremental C# source generator for explicit `IDisposable` and `IAsyncDisposable` ownership. It must remove mechanical disposal code without hiding ownership decisions or weakening the conventional .NET disposal contract.

The package must remain analyzer-only: consumers receive generated source and injected attributes, with no DisposableGenerator runtime assembly reference or runtime dependency.

## Toolchain and repository layout

- Use the SDK selected by `global.json` and central versions from `Directory.Packages.props`.
- Keep the generator in `src/DisposableGenerator`, unit/generator tests in `tests/DisposableGenerator.Tests`, package-consumer tests in `tests/PackageIntegration`, samples in `samples`, public documentation in `README.md` and `docs`, and automation in `eng` and `.github/workflows`.
- Keep the generator target at `netstandard2.0` unless a deliberate compatibility change is approved and documented.
- Preserve nullable analysis, deterministic builds, warnings-as-errors, and the repository formatting rules.
- Do not add a runtime helper library. Generated consumers must depend only on their target framework.

## Public ownership contract

- Generation is opt-in through `[GenerateDisposable]` on a non-static, non-record partial class. Every containing type must also be partial. File-local owners or containers are unsupported.
- Ownership is always explicit. A disposable member is owned only when marked `[DisposeMember]`; `[BorrowedMember]` records that another component owns it. Never infer ownership from a disposable type, constructor assignment, dependency injection, naming, accessibility, or mutability.
- Resolve `DISP006` by making the ownership decision visible. Use `[DisposeMember]` for resources this instance must release and `[BorrowedMember]` for DI-, framework-, parent-, cache-, or otherwise externally owned resources. Do not add `#pragma warning disable DISP006` as the normal fix. Suppression is permitted only when neither annotation can express the scenario, and the code must document why.
- An owned target must be a readable instance field or property whose type implements `IDisposable` or `IAsyncDisposable`. A target must never be both owned and borrowed.
- Preserve `[DisposeMember(Order = value)]`: lower values dispose first; equal values use configured declaration order.
- Warn for mutable owned members because replacing their value can leak the former resource. Do not silently snapshot or intercept assignments.

## Generated API and lifetime state

- A synchronous root implements `IDisposable`, exposes non-virtual public `Dispose()`, and supplies the conventional overridable `Dispose(bool)` core for generated inheritance.
- An asynchronous root implements `IAsyncDisposable`, exposes public `DisposeAsync()`, and supplies `DisposeAsyncCore()` for generated inheritance. A conjunctive root implements both contracts; an async-only root must not advertise `IDisposable`.
- Generated derived levels must override and chain the generated base core. Only the root owns public entry points, lifetime state, the synchronization gate, and the dynamic-registration collection.
- `Dispose()` and `DisposeAsync()` must share one atomic lifetime transition. Repeated and competing calls, including concurrent sync/async calls, must execute cleanup at most once.
- Mark the instance disposed before invoking user cleanup. Registration must not race into a collection being drained.
- Every public deterministic disposal entry point must call `GC.SuppressFinalize(this)` after its generated cleanup path.
- Generated member names, accessibility, and signatures are compatibility surface. Detect collisions and report a diagnostic instead of overwriting, hiding, or ambiguously binding handwritten members.

## Disposal modes

- `GenerateSynchronousDispose` defaults to `true`; async, unmanaged cleanup, and finalization default to `false`.
- At least one disposal interface must be enabled. Finalization requires synchronous generation and implies unmanaged cleanup.
- Every generated level in an inheritance chain must use the same synchronous/asynchronous interface mode.
- Async cleanup prefers `IAsyncDisposable.DisposeAsync()` for dual-capability resources and falls back to `IDisposable.Dispose()` for sync-only resources.
- Sync cleanup must never block on async-only cleanup. Diagnose fixed async-only members on a conjunctive owner with `DISP022`; require async generation with `DISP020` when no async path exists.
- `RegisterAsyncDisposable` returns `ValueTask<T>` because late registration may require immediate asynchronous disposal. Call sites must await it.

## Ordering, hooks, and exceptions

- Default owned-member order is reverse declaration order. Default dynamic-resource order is reverse registration order. Both must remain deterministic across files and configurable through documented MSBuild properties.
- The default hierarchy order is most-derived to root. At each level call `OnDisposing()`, dispose that level's owned members, then call `OnDisposed()`. At the root, drain dynamic registrations after fixed root members and before `OnDisposed()`.
- `StopOnFirst` propagates normally while generated derived levels still chain their base cleanup in `finally`.
- `ContinueAndAggregate` attempts every hook, member, registration, and generated base level, flattens nested aggregates, and throws one final `AggregateException`.
- Once cleanup starts, failures must not make a later disposal call retry already claimed cleanup.
- When hooks are disabled by configuration, do not declare or call them.

## Dynamic registration

- Generate registration only on roots and only when enabled. It is private for sealed roots and protected for inheritable roots so unrelated callers cannot attach resources to an object's lifetime.
- The sync and async registration method names are configurable, must be distinct valid non-keyword identifiers, and must not collide with disposal infrastructure.
- Null registration throws `ArgumentNullException`.
- Default late registration throws `ObjectDisposedException` and does not take ownership. `DisposeImmediately` disposes or awaits the supplied resource and returns it.
- Sync and async registrations share one deterministic sequence and the root collection.

## Unmanaged cleanup and finalization

- Prefer `SafeHandle` ownership through `[DisposeMember]`. Raw unmanaged cleanup is an exceptional opt-in.
- `DisposeUnmanaged()` must be an implementation-only `partial void DisposeUnmanaged()` hook. It runs after managed cleanup during deterministic disposal and on `Dispose(false)`.
- A generated finalizer must share the same lifetime state and contain all exceptions. Finalizer code must never touch managed objects.
- Reject handwritten finalizers on generated owners so there is only one finalization contract.

## Diagnostics and configuration

- Diagnostics `DISP001` through `DISP025`, their default severities, locations, and meanings are public analyzer behavior. Add or change diagnostics only with matching descriptor, analyzer release notes, unit tests, README/configuration documentation, and package integration coverage where applicable.
- Report actionable diagnostics instead of emitting uncompilable or ambiguous source. Invalid MSBuild values report `DISP009` and fall back to the documented default.
- Keep `buildTransitive/DisposableGenerator.props`, option parsing, README tables, `docs/configuration.md`, tests, and package-consumer tests synchronized for every option.
- Standard `.editorconfig`, `NoWarn`, and pragma severity controls must continue to work, but project-wide ownership configuration must not replace explicit annotations in repository examples or tests.

## Generator implementation rules

- Keep generation incremental. Derive outputs from immutable models and avoid retaining `Compilation`, syntax nodes, or symbols beyond the transformation that needs them.
- Use semantic symbols for correctness; syntax is only an efficient candidate filter or a source-order/location input.
- Emit fully qualified framework names with `global::` and escape user identifiers. Preserve namespaces, nesting, generic parameters/constraints, accessibility, and supported modern C# declarations.
- Generated output must be deterministic for identical inputs. Do not depend on enumeration, filesystem, culture, hash, or thread scheduling order.
- Never reflect on or execute consumer code. Never introduce unsafe code unless an explicit feature requires it and tests cover it.
- Keep generated code readable, nullable-correct, and warning-free under consumer warnings-as-errors.
- Preserve compatibility with the Roslyn version referenced by the package project. Unit tests may exercise newer language syntax, but generator implementation APIs must remain available to the packaged Roslyn baseline.

## Required tests for changes

- Generator behavior or diagnostics: add focused tests in `tests/DisposableGenerator.Tests` for valid source, generated shape, diagnostics, and runtime behavior when relevant.
- Disposal semantics: cover idempotency, concurrency, exact order, inheritance, late registration, and both exception policies as affected.
- Async changes: cover sync-only, async-only, dual-capability resources, async registration, and competing disposal paths as affected.
- Finalization changes: cover deterministic unmanaged cleanup and finalizer safety without relying on nondeterministic timing for the only assertion.
- Language-shape changes: cover namespaces, nested/generic containers, escaped identifiers, nullable value types, primary constructors, and current/preview syntax as affected.
- Packaging or build-asset changes: pack first and run `eng/run-integration-tests.sh` against the `.nupkg`. Package integration projects must use `PackageReference`, never `ProjectReference`.
- A regression test must fail before the fix or otherwise directly prove the changed contract.

## Mandatory validation

Run checks proportional to the change. Before declaring any generator, packaging, configuration, or workflow change complete, the full baseline is required:

```bash
dotnet restore DisposableGenerator.slnx
dotnet build DisposableGenerator.slnx --configuration Release --no-restore
dotnet test DisposableGenerator.slnx --configuration Release --no-build
dotnet run --project samples/DisposableGenerator.Sample --configuration Release --no-build
dotnet run --project samples/DisposableGenerator.AvaloniaReactiveUI.Sample --configuration Release --no-build -- --smoke-test
dotnet pack src/DisposableGenerator/DisposableGenerator.csproj --configuration Release --no-build --output artifacts/packages
./eng/verify-package.sh
./eng/run-integration-tests.sh
dotnet format DisposableGenerator.slnx --verify-no-changes --no-restore
```

Also run `shellcheck` for changed shell scripts and `actionlint` for changed workflows when those tools are available. Do not weaken, skip, or convert a failing required check into an allowed failure to make CI green.

## Package and workflow gates

- The package must contain the analyzer DLL, transitive MSBuild props, README, and license; it must not expose build output as a runtime/library asset or publish Roslyn dependencies to consumers.
- Integration tests must restore the packed local `.nupkg` in an isolated consumer workspace. Do not let a `ProjectReference` or previously cached DisposableGenerator package satisfy the test.
- `.github/workflows/integration.yml` must run on every branch push and every pull request across the supported OS/framework matrix.
- Release publication must depend on a successful invocation of the integration workflow. Never publish before package verification and integration success.
- Keep build, integration, and release workflows separate. Branch protection should mark the integration matrix as required for protected branches.

## Code Review Rules

- Update public documentation and samples in the same change whenever installation, generated API, configuration, diagnostics, behavior, compatibility, or limitations change.
- Examples must show explicit ownership and correct `using` or `await using`. DI-supplied disposable dependencies must use `[BorrowedMember]`, not a warning pragma.
- Do not claim support that is not covered by unit or packed-package integration tests.
- Review generated-code changes for public API compatibility, deadlocks/reentrancy, exception masking, analyzer performance, deterministic ordering, and sync-over-async hazards.
