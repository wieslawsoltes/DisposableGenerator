using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using ReactiveUI.SourceGenerators;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;

[GenerateDisposable]
public abstract partial class DocumentViewModelBase : ViewModelBase
{
    [DisposeMember]
    private readonly IDisposable _documentOwnedResource;

    [Reactive]
    private string _title;

    protected DocumentViewModelBase(
        string displayName,
        string title,
        ApplicationService applicationService,
        DisposalLog log)
        : base(displayName, applicationService, log)
    {
        _title = title;
        _documentOwnedResource = new TrackedDisposable($"{displayName}:document-member", log);
        Track(new TrackedDisposable($"{displayName}:document-registered", log));
    }

    partial void OnDisposing() => DisposalLog.Add($"{DisplayName}:document-disposing");

    partial void OnDisposed() => DisposalLog.Add($"{DisplayName}:document-disposed");
}

