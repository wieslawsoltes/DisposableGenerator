# Migration from public composite disposal

This guide maps the original public aggregation pattern to explicit generated ownership. The repository itself was created as a standalone generator and contains no Studio Pro source tree, so application migration remains a consuming-repository change.

## 1. Inventory ownership

Find implementations and uses of the old contract:

```bash
rg 'ICompositeDisposable|AddToDisposables'
```

For every resource, decide whether the object owns it. Mark owned resources `[DisposeMember]`. Mark services, scopes, or subscriptions whose lifetime belongs to dependency injection or another owner `[BorrowedMember]` so the non-ownership decision remains visible without a warning suppression.

## 2. Convert fixed resources

Before:

```csharp
public sealed class EditorViewModel : ICompositeDisposable
{
    private readonly IDisposable _subscription;

    public EditorViewModel(IService service)
    {
        _subscription = service.Subscribe();
        AddToDisposables(_subscription);
    }
}
```

After:

```csharp
[GenerateDisposable]
public sealed partial class EditorViewModel
{
    [DisposeMember]
    private readonly IDisposable _subscription;

    public EditorViewModel(IService service)
    {
        _subscription = service.Subscribe();
    }
}
```

## 3. Convert dynamic resources

Replace internal `AddToDisposables(resource)` calls with `RegisterDisposable(resource)`. Calls from unrelated objects must move behind an ownership method on the owner or be assigned to the correct lifetime owner; do not recreate a public registration interface.

## 4. Convert manual cleanup

Move unusual managed cleanup into `OnDisposing()` or represent it as an `IDisposable`. Remove handwritten `Dispose()` and `Dispose(bool)` methods from generated types; `DISP005` prevents two competing patterns.

For asynchronous resources, enable `GenerateAsyncDispose` and remove handwritten `DisposeAsync()` / `DisposeAsyncCore()` implementations. Choose `GenerateSynchronousDispose = false` when an async-only resource has no honest synchronous fallback; otherwise `DISP022` reminds callers that `Dispose()` cannot release it.

Prefer moving raw handles behind `SafeHandle`. If that is not possible, enable `GenerateUnmanagedCleanup`, implement `partial void DisposeUnmanaged()`, and opt into `GenerateFinalizer` only when nondeterministic fallback cleanup is genuinely required.

## 5. Migrate hierarchies together

Annotate the disposable root and each derived type that owns resources. The generated root exposes the public contract. Derived levels get correct protected overrides automatically. A non-generated disposable base reports `DISP004` and must be handled according to that base type's documented disposal API.

## 6. Verify behavior before removing the old API

Add tests for:

- base and derived resources;
- repeated and concurrent `Dispose()` calls;
- declared and dynamic resource order;
- registration after disposal;
- intentionally borrowed resources;
- cleanup hook order.

Once searches show no remaining implementation or call sites, remove or explicitly obsolete `ICompositeDisposable` and `AddToDisposables`. A final CI search can prevent reintroduction.
