# Design rationale

## Original problem

The original lifetime pattern exposed resource aggregation publicly:

```csharp
public interface ICompositeDisposable : IDisposable
{
    void AddToDisposables(IDisposable disposable);
}
```

That makes an ownership implementation detail part of every implementing type's public contract. Any unrelated caller can attach an arbitrary resource to the object's lifetime. It also leaves inheritance discipline—idempotency, `GC.SuppressFinalize`, and `base.Dispose(disposing)`—to each implementation.

The baseline .NET pattern solves the public contract and inheritance issues, but repeating it across many view models or components creates mechanical code and opportunities for inconsistency. A `DisposableBase` centralizes that code but consumes the only class inheritance slot. An internal Rx `CompositeDisposable` is excellent aggregation machinery, but by itself does not define the surrounding inheritance contract.

## Selected source-generator contract

The generator follows five rules:

1. `[GenerateDisposable]` opts a partial class into synchronous, asynchronous, or conjunctive generated lifetime management.
2. `[DisposeMember]` explicitly marks a fixed sync or async disposable member as owned; `[BorrowedMember]` explicitly records that its lifetime belongs elsewhere. A disposable type alone never implies ownership.
3. `RegisterDisposable` is available only inside the owning hierarchy: private on sealed roots and protected on inheritable roots.
4. Synchronous roots implement the conventional non-virtual public `Dispose()` plus `Dispose(bool)`. Async roots implement `DisposeAsync()` plus `DisposeAsyncCore()`. Generated derived types override and chain the selected core methods.
5. Partial cleanup hooks cover unusual managed cleanup without inviting a second disposal implementation.

This preserves an existing framework or application base class when that base does not already own an `IDisposable` contract. If the existing base does implement `IDisposable`, diagnostic `DISP004` stops generation because the correct integration depends on that base's documented extensibility point.

## Ownership and ordering

Owned members are disposed in reverse declaration order by default, analogous to unwinding construction. Dynamic resources are disposed in reverse registration order. Derived-level cleanup completes before base-level cleanup. The exact order is documented and tested. Both fixed and dynamic ordering are configurable, and `[DisposeMember(Order = value)]` expresses dependencies that source order cannot.

Dynamic resources registered anywhere in a generated hierarchy are held by the root registration collection. They are therefore disposed during root cleanup, after root `[DisposeMember]` resources and before root `OnDisposed()`.

Registration and disposal synchronize on a generated private gate. The disposed state is set before user cleanup starts. This gives idempotency under concurrent `Dispose()` calls and prevents new ownership from racing into a collection already being drained.

The default late-registration behavior is to throw `ObjectDisposedException` without disposing or retaining the argument. `DisposeImmediately` is available for Rx-like semantics.

## Asynchronous and unmanaged resources

Synchronous disposal remains the default. A type can additionally implement `IAsyncDisposable`, or disable synchronous generation when its ownership is genuinely async-only. Async cleanup prefers `IAsyncDisposable` on dual-capability resources and falls back to `IDisposable`; synchronous cleanup cannot release async-only resources. Both paths claim the same state before user code, which makes competing sync and async calls mutually idempotent.

Raw unmanaged cleanup is opt-in through `DisposeUnmanaged()`. It runs after managed cleanup during deterministic disposal and on the `Dispose(false)` path. A generated finalizer is a second explicit opt-in and contains hook exceptions, but it cannot make managed-object access safe during finalization. `SafeHandle` remains the preferred design because it isolates finalization in a runtime-tested critical-finalizer abstraction.

Record owners are rejected. Synthesized record equality includes instance state, while `with` expressions shallow-copy reference-valued members. Hidden disposal state would therefore affect value equality, and a clone could claim ownership of the same disposable references. Ordinary classes preserve the identity semantics required by unique ownership; records remain valid as containing types for nested generated classes.

The default `StopOnFirst` policy propagates disposal exceptions using normal disposal semantics. State has already transitioned to disposed, so cleanup is not retried. A generated derived override uses `finally` to ensure the generated base level is still invoked. Applications that prioritize best-effort cleanup can select `ContinueAndAggregate`, which attempts every synchronous or asynchronous cleanup action across the hierarchy and reports all flattened failures together.

## Diagnostics as convention enforcement

The generator rejects non-partial and file-local types, invalid member targets, incompatible base contracts, handwritten finalizers, manual disposal implementations, generated-name collisions, malformed hooks, invalid disposal modes, and conflicting ownership annotations. It validates borrowed members and warns when a mutable owned member can leak a replaced resource. `DISP006` is informational because an unannotated disposable requires an ownership decision. Mark owned references `[DisposeMember]` and DI-owned or otherwise borrowed references `[BorrowedMember]`.
