# DisposableGenerator

[![CI](https://github.com/wieslawsoltes/DisposableGenerator/actions/workflows/ci.yml/badge.svg)](https://github.com/wieslawsoltes/DisposableGenerator/actions/workflows/ci.yml)
[![NuGet Integration](https://github.com/wieslawsoltes/DisposableGenerator/actions/workflows/integration.yml/badge.svg)](https://github.com/wieslawsoltes/DisposableGenerator/actions/workflows/integration.yml)
[![NuGet](https://img.shields.io/nuget/v/DisposableGenerator.svg)](https://www.nuget.org/packages/DisposableGenerator)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

`DisposableGenerator` is an incremental C# source generator for explicit `IDisposable` and `IAsyncDisposable` ownership. Ownership stays visible in handwritten code, while the generator supplies thread-safe disposal entry points, inheritance chaining, dynamic registration, optional unmanaged cleanup, and opt-in finalization.

It is designed as an alternative to public lifetime-aggregation interfaces such as `ICompositeDisposable`. Registration is private on sealed types and protected on inheritable root types, so unrelated callers cannot attach resources to an object's lifetime.

## Install

```xml
<ItemGroup>
  <PackageReference Include="DisposableGenerator" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

The package is compile-time only. It adds no runtime dependency and injects the three attributes into the consuming compilation.

## Use explicit ownership

```csharp
using DisposableGenerator;

[GenerateDisposable]
public sealed partial class EditorViewModel
{
    [BorrowedMember]
    private readonly ISubscriptionService _borrowedService; // This interface implements IDisposable.

    [DisposeMember]
    private readonly IDisposable _subscription;

    public EditorViewModel(ISubscriptionService service)
    {
        _borrowedService = service; // Supplied by DI; this type does not own it.
        _subscription = service.Subscribe();

        RegisterDisposable(service.SubscribeToUpdates());
    }

    partial void OnDisposing()
    {
        // Optional exceptional cleanup that is not naturally IDisposable.
    }
}
```

The annotated type and every containing type must be `partial`. The annotated type must be a non-static class, not a record; record value equality and `with` cloning are incompatible with hidden mutable lifetime state and unique resource ownership. `[DisposeMember]` supports readable instance fields and properties whose type implements `IDisposable` or `IAsyncDisposable`, including nullable disposable value types and types made disposable by this generator in the same compilation. `[BorrowedMember]` records the opposite decision: the reference is intentionally not owned and must not be disposed by this object. The generator never infers ownership merely from a member's type.

Use `Order` when declaration order alone cannot express a dependency. Lower values are disposed first; equal values use the configured declaration order:

```csharp
[DisposeMember(Order = -100)]
private readonly IDisposable _mustCloseFirst;

[DisposeMember]
private readonly IDisposable _normalResource;
```

On a root generated type, the generated code:

- implements `IDisposable`;
- provides a non-virtual public `Dispose()` and the conventional `Dispose(bool)` hook;
- calls `GC.SuppressFinalize(this)`;
- ensures only one caller performs cleanup, including concurrent callers;
- provides `RegisterDisposable<T>(T)` for resources created dynamically;
- invokes optional `OnDisposing()` and `OnDisposed()` partial methods.

The registration method is private for a sealed root and protected for an inheritable root. Generated derived types use the inherited registration method and automatically override and chain `Dispose(bool)`.

## Asynchronous disposal

Enable both disposal interfaces when every owned asynchronous resource also has a meaningful synchronous fallback. This follows the [.NET conjunctive dispose pattern](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-disposeasync):

```csharp
[GenerateDisposable(GenerateAsyncDispose = true)]
public sealed partial class Transport
{
    [DisposeMember]
    private readonly Stream _stream;
}
```

`Dispose()` selects `IDisposable.Dispose()`, while `DisposeAsync()` prefers `IAsyncDisposable.DisposeAsync()` and falls back to synchronous disposal for members that implement only `IDisposable`. If a member is async-only, `DISP022` warns that calling `Dispose()` cannot release it.

For genuinely async-only ownership, do not advertise a misleading synchronous contract:

```csharp
[GenerateDisposable(
    GenerateSynchronousDispose = false,
    GenerateAsyncDispose = true)]
public sealed partial class AsyncSession
{
    [DisposeMember]
    private readonly IAsyncDisposable _connection;

    public ValueTask<T> OwnAsync<T>(T resource)
        where T : IAsyncDisposable => RegisterAsyncDisposable(resource);
}
```

Consume that type with `await using` or `await DisposeAsync()`. Async registration returns `ValueTask<T>` and must be awaited. The synchronous and asynchronous paths share one atomic lifetime state, so concurrent or repeated calls perform cleanup once. Every generated level in an inheritance chain must use the same sync/async mode.

`OnDisposing()` and `OnDisposed()` remain synchronous notifications on both paths. Represent custom awaitable cleanup as an `IAsyncDisposable` member or dynamically registered resource so the generator can await it and apply the selected exception policy.

## Unmanaged cleanup and finalization

Prefer [wrapping native handles in `SafeHandle`](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/unmanaged) and owning that wrapper with `[DisposeMember]`. When a raw unmanaged resource genuinely needs handwritten release logic, opt into the generated hook:

```csharp
[GenerateDisposable(
    GenerateUnmanagedCleanup = true,
    GenerateFinalizer = true)]
public sealed partial class NativeBuffer
{
    private nint _buffer;

    partial void DisposeUnmanaged()
    {
        System.Runtime.InteropServices.Marshal.FreeHGlobal(_buffer);
        _buffer = 0;
    }
}
```

`DisposeUnmanaged()` runs after managed cleanup during deterministic sync or async disposal, and also from `Dispose(false)` when the generated finalizer runs. Finalizer-path exceptions are contained. The hook must touch only unmanaged state: other managed objects may already have been finalized. `GenerateFinalizer` implies unmanaged cleanup and requires synchronous generation. Handwritten finalizers remain rejected by `DISP017` so there is only one finalization contract.

## Inheritance

```csharp
[GenerateDisposable]
public partial class DocumentViewModel
{
    [DisposeMember]
    private readonly IDisposable _documentSubscription;
}

[GenerateDisposable]
public sealed partial class EditorViewModel : DocumentViewModel
{
    [DisposeMember]
    private readonly IDisposable _editorSubscription;
}
```

Only the root implements public `Dispose()`. With the default exception policy, a generated derived class disposes its own resources and then calls the generated base hook in a `finally` block. This prevents a derived type from forgetting `base.Dispose(disposing)`.

The default managed cleanup order is deterministic:

1. Most-derived `OnDisposing()`.
2. Most-derived owned members in reverse source-declaration order.
3. Most-derived `OnDisposed()`.
4. The next generated base level, following the same sequence.
5. At the generated root, dynamically registered resources in reverse registration order, after root-owned members and before root `OnDisposed()`.

`Dispose()` is idempotent. By default, registration after disposal throws `ObjectDisposedException` and does not take ownership of the supplied resource. `StopOnFirst` preserves normal exception propagation while still chaining generated base levels. `ContinueAndAggregate` attempts every hook, member, registered resource, and base level, then throws one flattened `AggregateException`.

## Modern C# support

The generator is tested with current Roslyn and preview parsing. It supports class primary constructors, C# 14 field-backed properties, partial properties, nullable disposable structs, and nested generated classes inside generic partial classes, record classes, structs, readonly/ref structs, record structs, and interfaces. Escaped namespace/type/member identifiers and generic parameter shadowing are handled in generated source. A record may contain a generated class, but a record cannot itself be the generated disposable owner and reports `DISP007`.

File-local types intentionally report `DISP011`: a generator adds a different source file, while a `file` type is visible only inside its declaring file.

## MSBuild configuration

Set these properties in the consuming project. Package defaults are imported through `buildTransitive` and exposed to the compiler automatically.

| Property | Default | Allowed values | Effect |
|---|---|---|---|
| `DisposableGenerator_GenerateRegistrationMethod` | `true` | `true`, `false` | Generates dynamic registration on root types. |
| `DisposableGenerator_RegistrationMethodName` | `RegisterDisposable` | Valid non-keyword C# identifier | Renames the registration API. |
| `DisposableGenerator_AsyncRegistrationMethodName` | `RegisterAsyncDisposable` | Valid non-keyword C# identifier | Renames the awaitable async-registration API. |
| `DisposableGenerator_PostDisposeRegistrationBehavior` | `Throw` | `Throw`, `DisposeImmediately` | Defines late-registration behavior. |
| `DisposableGenerator_MemberDisposalOrder` | `ReverseDeclaration` | `ReverseDeclaration`, `Declaration` | Controls owned-member order within each type. |
| `DisposableGenerator_RegisteredResourceDisposalOrder` | `ReverseRegistration` | `ReverseRegistration`, `Registration` | Controls dynamic-resource order at the generated root. |
| `DisposableGenerator_DisposalExceptionBehavior` | `StopOnFirst` | `StopOnFirst`, `ContinueAndAggregate` | Selects fail-fast or exhaustive aggregate cleanup. |
| `DisposableGenerator_GenerateDisposalHooks` | `true` | `true`, `false` | Generates and calls `OnDisposing`/`OnDisposed`. |
| `DisposableGenerator_ReportUnownedDisposableFields` | `true` | `true`, `false` | Reports informational `DISP006` reminders for readable disposable fields and properties. |

Example:

```xml
<PropertyGroup>
  <DisposableGenerator_RegistrationMethodName>Own</DisposableGenerator_RegistrationMethodName>
  <DisposableGenerator_AsyncRegistrationMethodName>OwnAsync</DisposableGenerator_AsyncRegistrationMethodName>
  <DisposableGenerator_PostDisposeRegistrationBehavior>DisposeImmediately</DisposableGenerator_PostDisposeRegistrationBehavior>
  <DisposableGenerator_MemberDisposalOrder>Declaration</DisposableGenerator_MemberDisposalOrder>
  <DisposableGenerator_RegisteredResourceDisposalOrder>Registration</DisposableGenerator_RegisteredResourceDisposalOrder>
  <DisposableGenerator_DisposalExceptionBehavior>ContinueAndAggregate</DisposableGenerator_DisposalExceptionBehavior>
</PropertyGroup>
```

Invalid settings report `DISP009` and use the documented fallback.

## Diagnostics

| ID | Default severity | Meaning |
|---|---|---|
| `DISP001` | Error | The generated type or a containing type is not partial. |
| `DISP002` | Warning | `[DisposeMember]` is used outside a `[GenerateDisposable]` type. |
| `DISP003` | Error | An owned member implements neither `IDisposable` nor `IAsyncDisposable`. |
| `DISP004` | Error | A root has a non-generated `IDisposable` base, whose contract could conflict. |
| `DISP005` | Error | A generated type manually declares a generated sync or async disposal method. |
| `DISP006` | Info | A readable disposable field or property has neither `[DisposeMember]` nor `[BorrowedMember]`. |
| `DISP007` | Error | The annotated type is not a non-static, non-record class. |
| `DISP008` | Error | An owned member is not a readable instance field or property. |
| `DISP009` | Warning | An MSBuild generator option is invalid. |
| `DISP010` | Error | A member declares both owned and borrowed lifetime semantics. |
| `DISP011` | Error | A generated type or containing type is file-local. |
| `DISP012` | Error | A handwritten member conflicts with generated infrastructure. |
| `DISP013` | Warning | `[BorrowedMember]` is used outside a `[GenerateDisposable]` type. |
| `DISP014` | Warning | A borrowed target is not a readable instance `IDisposable` or `IAsyncDisposable` member. |
| `DISP015` | Error | A disposal hook does not use the supported implementation-only partial signature. |
| `DISP016` | Error | A non-generated base exposes an accessible `Dispose()` or `Dispose(bool)` member. |
| `DISP017` | Error | A handwritten finalizer conflicts with generator-owned finalization. |
| `DISP018` | Info | A mutable owned member can leak a resource when its value is replaced. |
| `DISP019` | Error | Async generation was requested where `IAsyncDisposable` is unavailable. |
| `DISP020` | Error | An async-only owned member requires `GenerateAsyncDispose`. |
| `DISP021` | Error | A generated inheritance chain uses inconsistent sync/async modes. |
| `DISP022` | Warning | `Dispose()` cannot release an async-only member; use an async-only owner or ensure callers await disposal. |
| `DISP023` | Error | A root has a non-generated `IAsyncDisposable` base contract. |
| `DISP024` | Error | `DisposeUnmanaged()` does not use the supported partial implementation signature. |
| `DISP025` | Error | No disposal interface is enabled, or finalization was requested without synchronous generation. |

Standard `.editorconfig`, `NoWarn`, and `#pragma warning` controls work for diagnostic severity and intentional suppressions.

## Scope and limitations

The generator supports synchronous, asynchronous, and conjunctive disposal, plus opt-in unmanaged cleanup and finalization. Safe handles remain strongly preferred over raw handles and handwritten release code. A class with a non-generated base that already implements `IDisposable` or `IAsyncDisposable` receives `DISP004` or `DISP023`; integrate with that base's documented disposal hook manually instead of creating a competing public contract.

See [design rationale](docs/design-rationale.md), [configuration](docs/configuration.md), and the [migration guide](docs/migration.md).

Coding agents working in this repository also discover the checked-in [`use-disposable-generator`](.agents/skills/use-disposable-generator/SKILL.md) skill. It provides an installation and consumption workflow, full option and diagnostic guidance, and the explicit owned-versus-borrowed decision process. Repository implementation requirements are authoritative in [`AGENTS.md`](AGENTS.md).

## Samples

- [Console sample](samples/DisposableGenerator.Sample/Program.cs) demonstrates the compact base/derived disposal pattern and borrowed resources.
- [Avalonia + ReactiveUI 24 sample](samples/DisposableGenerator.AvaloniaReactiveUI.Sample/README.md) is a complete desktop application using Avalonia 12, ReactiveUI 24, `ReactiveUI.SourceGenerators` properties and commands, and DisposableGenerator across `ReactiveObject` → generated base → intermediate base → sealed leaf view-model chains. It also demonstrates one generated disposable view model owning other generated disposable view models.

Run the UI application normally, or use its headless smoke mode to verify generated reactive properties, commands, nested ownership, idempotency, borrowed lifetime, and exact leaf-to-base cleanup order:

```bash
dotnet run --project samples/DisposableGenerator.AvaloniaReactiveUI.Sample
dotnet run --project samples/DisposableGenerator.AvaloniaReactiveUI.Sample -- --smoke-test
```

## Build and test

The repository uses the .NET 10 SDK:

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

`eng/run-integration-tests.sh` restores standalone consumers from the packed local NuGet package in a fresh cache with all non-local package sources disabled. The default local run targets `net10.0`; pass `net8.0`, `net9.0`, or `net10.0` as the second argument to select a framework. The separate NuGet Integration workflow runs the full 3 operating system × 3 framework matrix on every branch push and pull request, and the release workflow cannot publish until that reusable integration workflow succeeds. See [the package integration matrix](tests/PackageIntegration/README.md) for coverage and commands.

Tags named `vMAJOR.MINOR.PATCH` run the release workflow, verify that exact package version, publish it to NuGet using the `NUGET_API_KEY` repository secret, and create a GitHub release containing the `.nupkg` file.

## License

MIT. See [LICENSE](LICENSE).
