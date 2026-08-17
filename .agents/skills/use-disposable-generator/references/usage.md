# DisposableGenerator consumer reference

## Install

Use the version requested by the repository's package policy. For a project that manages versions directly:

```xml
<ItemGroup>
  <PackageReference Include="DisposableGenerator" Version="1.0.0" PrivateAssets="all" />
</ItemGroup>
```

With NuGet central package management:

```xml
<!-- Directory.Packages.props -->
<ItemGroup>
  <PackageVersion Include="DisposableGenerator" Version="1.0.0" />
</ItemGroup>
```

```xml
<!-- Consumer.csproj -->
<ItemGroup>
  <PackageReference Include="DisposableGenerator" PrivateAssets="all" />
</ItemGroup>
```

The package injects `GenerateDisposableAttribute`, `DisposeMemberAttribute`, and `BorrowedMemberAttribute` into the compilation. Add `using DisposableGenerator;`; do not define copies of the attributes.

For a locally packed build, add the package directory as a NuGet source and pin the exact `.nupkg` version. Use an isolated package cache when proving package contents, and ensure source mapping resolves `DisposableGenerator` from the local source rather than nuget.org.

## Ownership-first pattern

```csharp
using System;
using DisposableGenerator;

[GenerateDisposable]
public sealed partial class EditorSession
{
    [BorrowedMember]
    private readonly IDisposable _borrowedService;

    [DisposeMember]
    private readonly IDisposable _subscription;

    public EditorSession(IDisposable borrowedService, Func<IDisposable> createSubscription)
    {
        _borrowedService = borrowedService;
        _subscription = createSubscription();
        RegisterDisposable(createSubscription());
    }
}
```

Apply `[DisposeMember(Order = -100)]` when one owned member must be released before ordinary members. Lower values dispose first. Otherwise, default cleanup unwinds members in reverse declaration order and registrations in reverse registration order.

`DISP006` asks for an ownership decision. Mark DI- or framework-supplied references `[BorrowedMember]`; do not use a pragma merely because the member came from DI. Suppress the diagnostic only for a documented case that neither ownership annotation can represent.

## Choose the disposal mode

Synchronous only (default):

```csharp
[GenerateDisposable]
public sealed partial class SyncOwner
{
    [DisposeMember] private readonly IDisposable _resource;
}
```

Both interfaces, when every async resource has a meaningful sync fallback:

```csharp
[GenerateDisposable(GenerateAsyncDispose = true)]
public sealed partial class DualOwner
{
    [DisposeMember] private readonly Stream _stream;
}
```

Async only, when any ownership is genuinely async-only:

```csharp
[GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
public sealed partial class AsyncOwner
{
    [DisposeMember] private readonly IAsyncDisposable _connection;

    public ValueTask<T> OwnAsync<T>(T value)
        where T : IAsyncDisposable => RegisterAsyncDisposable(value);
}
```

Consume async-only owners with `await using` or `await DisposeAsync()`. Await `RegisterAsyncDisposable`, because late registration may immediately dispose the value.

All generated types in an inheritance chain must select the same sync/async mode. Annotate every generated derived level; the root owns public entry points and registration state, while generated derived cores chain automatically.

## Hooks and raw unmanaged resources

Implement optional synchronous notifications without redeclaring them:

```csharp
partial void OnDisposing() { }
partial void OnDisposed() { }
```

Represent awaitable custom cleanup as an owned or registered `IAsyncDisposable`; the notification hooks are synchronous.

Prefer an owned `SafeHandle`. For an unavoidable raw unmanaged resource:

```csharp
[GenerateDisposable(GenerateUnmanagedCleanup = true, GenerateFinalizer = true)]
public sealed partial class NativeOwner
{
    partial void DisposeUnmanaged()
    {
        // Release unmanaged state only and make the operation idempotent.
    }
}
```

Do not access managed objects from `DisposeUnmanaged()` when it may run on the finalizer path. Do not add a handwritten finalizer.

## MSBuild configuration

| Property | Default | Allowed values |
|---|---|---|
| `DisposableGenerator_GenerateRegistrationMethod` | `true` | `true`, `false` |
| `DisposableGenerator_RegistrationMethodName` | `RegisterDisposable` | Valid non-keyword identifier |
| `DisposableGenerator_AsyncRegistrationMethodName` | `RegisterAsyncDisposable` | Distinct valid non-keyword identifier |
| `DisposableGenerator_PostDisposeRegistrationBehavior` | `Throw` | `Throw`, `DisposeImmediately` |
| `DisposableGenerator_MemberDisposalOrder` | `ReverseDeclaration` | `ReverseDeclaration`, `Declaration` |
| `DisposableGenerator_RegisteredResourceDisposalOrder` | `ReverseRegistration` | `ReverseRegistration`, `Registration` |
| `DisposableGenerator_DisposalExceptionBehavior` | `StopOnFirst` | `StopOnFirst`, `ContinueAndAggregate` |
| `DisposableGenerator_GenerateDisposalHooks` | `true` | `true`, `false` |
| `DisposableGenerator_ReportUnownedDisposableFields` | `true` | `true`, `false` |

`Throw` rejects late registration without taking ownership. `DisposeImmediately` releases it synchronously or asynchronously. `ContinueAndAggregate` attempts all cleanup and throws one flattened `AggregateException`; the default `StopOnFirst` preserves normal propagation while generated derived levels still chain their bases.

## Diagnostic guide

| ID | Required response |
|---|---|
| `DISP001` | Make the owner and every containing type partial. |
| `DISP002` | Move `[DisposeMember]` into a generated owner or remove it. |
| `DISP003` | Mark only an `IDisposable` or `IAsyncDisposable` member as owned. |
| `DISP004` | Use the non-generated disposable base's documented hook. |
| `DISP005` | Remove handwritten generated entry or core methods. |
| `DISP006` | Add `[DisposeMember]` or `[BorrowedMember]` after deciding ownership. |
| `DISP007` | Use a non-static, non-record class owner. |
| `DISP008` | Use a readable instance field or property. |
| `DISP009` | Correct the named MSBuild property; generation used its fallback. |
| `DISP010` | Choose exactly one ownership annotation. |
| `DISP011` | Remove `file` locality from the owner or container. |
| `DISP012` | Rename or remove the colliding handwritten member or configured method. |
| `DISP013` | Move `[BorrowedMember]` into a generated owner or remove it. |
| `DISP014` | Borrow only a readable disposable instance field or property. |
| `DISP015` | Implement only `partial void OnDisposing()` or `OnDisposed()`. |
| `DISP016` | Integrate with the base's existing accessible disposal hook. |
| `DISP017` | Remove the handwritten finalizer; opt into generated finalization if required. |
| `DISP018` | Make ownership readonly or explicitly handle replacement lifetime. |
| `DISP019` | Target a framework that provides `IAsyncDisposable` or disable async generation. |
| `DISP020` | Enable async generation for an async-only owned member. |
| `DISP021` | Align sync and async modes across the generated hierarchy. |
| `DISP022` | Prefer async-only ownership or guarantee consumers await the async path. |
| `DISP023` | Use the non-generated async-disposable base's documented hook. |
| `DISP024` | Implement only `partial void DisposeUnmanaged()`. |
| `DISP025` | Enable at least one interface; finalization also requires sync generation. |

## Verification checklist

- Build with nullable analysis and warnings-as-errors as the consumer normally does.
- Verify each disposable member has an explicit owned or borrowed decision.
- Assert exact cleanup order, borrowed-resource survival, and repeated or concurrent disposal behavior.
- For async owners, verify async-only and dual-capability resources take the intended path.
- Verify late registration and exception behavior when configured away from defaults.
- For local package validation, restore from the `.nupkg`, not a project reference or stale global cache.
