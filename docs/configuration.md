# Configuration reference

DisposableGenerator reads compiler-visible MSBuild properties from the consuming project. The NuGet package's `buildTransitive/DisposableGenerator.props` supplies defaults and registers every property with Roslyn.

## Properties

### `DisposableGenerator_GenerateRegistrationMethod`

Default: `true`.

When `false`, no registration collection or synchronous/asynchronous registration methods are generated. Fixed `[DisposeMember]` ownership, hooks, inheritance, and idempotency remain available.

### `DisposableGenerator_RegistrationMethodName`

Default: `RegisterDisposable`.

The value must be a valid C# identifier and cannot be a keyword. The method remains private on sealed roots and protected on inheritable roots regardless of its name.

### `DisposableGenerator_AsyncRegistrationMethodName`

Default: `RegisterAsyncDisposable`.

The value follows the same identifier rules as the synchronous registration name, and the two names must be distinct. It names the generated `ValueTask<T>` registration method available when `GenerateAsyncDispose` is enabled on the attribute. Callers must await the returned operation.

### `DisposableGenerator_PostDisposeRegistrationBehavior`

Default: `Throw`.

- `Throw` raises `ObjectDisposedException` and leaves the argument undisposed and unowned.
- `DisposeImmediately` invokes `Dispose()` for synchronous registration or awaits `DisposeAsync()` for asynchronous registration, then returns the argument.

Both behaviors reject `null` with `ArgumentNullException`.

### `DisposableGenerator_MemberDisposalOrder`

Default: `ReverseDeclaration`.

- `ReverseDeclaration` unwinds owned fields and properties from last declaration to first.
- `Declaration` follows source declaration order.

Across multiple source files, file paths are compared ordinally before source positions so the result remains deterministic.

`[DisposeMember(Order = value)]` adds an explicit priority before this setting is applied. Lower `Order` values are disposed first. Members with the same value—zero by default—follow `MemberDisposalOrder`.

### `DisposableGenerator_RegisteredResourceDisposalOrder`

Default: `ReverseRegistration`.

- `ReverseRegistration` unwinds dynamically registered resources from last registration to first.
- `Registration` disposes them from first registration to last.

This setting affects the root registration collection. Derived-level fixed members still dispose before the generated base level.
Synchronous and asynchronous registrations share the same sequence, so mixed registration order remains deterministic.

### `DisposableGenerator_DisposalExceptionBehavior`

Default: `StopOnFirst`.

- `StopOnFirst` propagates the first exception from each cleanup level. A generated derived override invokes its base level in `finally`, matching the original behavior.
- `ContinueAndAggregate` catches failures from every hook, owned member, registered resource, and generated base level. Cleanup continues in the configured order, nested `AggregateException` values are flattened, and one final `AggregateException` is thrown.

Both policies transition the owner to disposed before calling user cleanup, so a failed cleanup is not retried by a later `Dispose()` call.

### `DisposableGenerator_GenerateDisposalHooks`

Default: `true`.

When enabled, each generated type declares and calls:

```csharp
partial void OnDisposing();
partial void OnDisposed();
```

Implement either declaration in the handwritten partial type. When disabled, neither hook is declared or called.

### `DisposableGenerator_ReportUnownedDisposableFields`

Default: `true`.

When enabled, `DISP006` reports each readable instance field or property that implements `IDisposable` or `IAsyncDisposable` but has neither `[DisposeMember]` nor `[BorrowedMember]`. This is informational. Use `[BorrowedMember]` when another component owns the resource, or turn the property off project-wide after the ownership audit is complete. The property name retains `Fields` for compatibility even though the audit covers both supported member kinds.

## Example project configuration

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <DisposableGenerator_GenerateRegistrationMethod>true</DisposableGenerator_GenerateRegistrationMethod>
    <DisposableGenerator_RegistrationMethodName>Own</DisposableGenerator_RegistrationMethodName>
    <DisposableGenerator_AsyncRegistrationMethodName>OwnAsync</DisposableGenerator_AsyncRegistrationMethodName>
    <DisposableGenerator_PostDisposeRegistrationBehavior>DisposeImmediately</DisposableGenerator_PostDisposeRegistrationBehavior>
    <DisposableGenerator_MemberDisposalOrder>Declaration</DisposableGenerator_MemberDisposalOrder>
    <DisposableGenerator_RegisteredResourceDisposalOrder>Registration</DisposableGenerator_RegisteredResourceDisposalOrder>
    <DisposableGenerator_DisposalExceptionBehavior>ContinueAndAggregate</DisposableGenerator_DisposalExceptionBehavior>
    <DisposableGenerator_GenerateDisposalHooks>true</DisposableGenerator_GenerateDisposalHooks>
    <DisposableGenerator_ReportUnownedDisposableFields>true</DisposableGenerator_ReportUnownedDisposableFields>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DisposableGenerator" Version="1.0.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

## Per-type generation modes

Disposal capabilities are type-level ownership decisions and are configured on `[GenerateDisposable]`, not project-wide:

```csharp
// Existing default: IDisposable only.
[GenerateDisposable]
partial class SyncOwner { }

// Implements both IDisposable and IAsyncDisposable.
[GenerateDisposable(GenerateAsyncDispose = true)]
partial class ConjunctiveOwner { }

// Implements only IAsyncDisposable.
[GenerateDisposable(
    GenerateSynchronousDispose = false,
    GenerateAsyncDispose = true)]
partial class AsyncOnlyOwner { }

// Adds DisposeUnmanaged() to deterministic cleanup and emits a finalizer.
[GenerateDisposable(
    GenerateUnmanagedCleanup = true,
    GenerateFinalizer = true)]
partial class NativeOwner { }
```

`GenerateSynchronousDispose` defaults to `true`; the other three switches default to `false`. `GenerateFinalizer` implies `GenerateUnmanagedCleanup`. At least one disposal interface must remain enabled, finalization requires synchronous generation, and every generated type in an inheritance chain must select the same interface mode.

For a conjunctive owner, synchronous disposal skips members and dynamic registrations that implement only `IAsyncDisposable`; fixed members report `DISP022`. Use the async-only mode when no sound synchronous fallback exists.

When consuming the generator with a raw analyzer `ProjectReference` during development, package build assets are not imported. Add matching `CompilerVisibleProperty` items yourself for any non-default setting. Normal `PackageReference` consumers do not need those items.
