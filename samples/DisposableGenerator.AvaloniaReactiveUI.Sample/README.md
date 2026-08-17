# Avalonia + ReactiveUI 24 sample

This desktop sample combines:

- Avalonia `12.1.0` and `ReactiveUI.Avalonia`;
- ReactiveUI `24.0.0` using its default primitives-based distribution;
- `ReactiveUI.SourceGenerators` `3.2.0` for `[Reactive]` properties and `[ReactiveCommand]` commands;
- DisposableGenerator for explicit owned/borrowed resources and generated disposal inheritance.

## View-model hierarchy

```text
ReactiveObject
└── ViewModelBase                         [GenerateDisposable]
    ├── WorkspaceViewModel (sealed)       [GenerateDisposable]
    │   ├── owns EditorViewModel          [DisposeMember]
    │   └── owns PreviewViewModel         [DisposeMember]
    └── DocumentViewModelBase             [GenerateDisposable]
        ├── EditorViewModel (sealed)       [GenerateDisposable]
        └── PreviewViewModel (sealed)      [GenerateDisposable]
```

`ViewModelBase` is the generated disposable root even though it already inherits `ReactiveObject`. It owns a fixed resource, registers a dynamic resource through the protected API, and marks the application service `[BorrowedMember]` because the Avalonia application owns it.

Each document level and sealed leaf adds owned resources and cleanup hooks. The generator creates the protected overrides and base calls. `WorkspaceViewModel` demonstrates one generated disposable type owning other generated disposable types in the same compilation.

## Run the desktop application

```bash
dotnet run --project samples/DisposableGenerator.AvaloniaReactiveUI.Sample
```

## Run headless verification

The smoke mode does not start Avalonia. It verifies generated reactive properties and commands, idempotency, the complete leaf-to-base disposal order, nested view-model ownership, and that the borrowed application service remains alive:

```bash
dotnet run --project samples/DisposableGenerator.AvaloniaReactiveUI.Sample -- --smoke-test
```

