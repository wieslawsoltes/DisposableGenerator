using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using ReactiveUI.SourceGenerators;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;

[GenerateDisposable]
public sealed partial class PreviewViewModel : DocumentViewModelBase
{
    [DisposeMember]
    private readonly IDisposable _previewOwnedResource;

    [Reactive]
    private int _zoomPercent = 100;

    [Reactive]
    private bool _isLive = true;

    public PreviewViewModel(ApplicationService applicationService, DisposalLog log)
        : base("Preview", "Generated preview", applicationService, log)
    {
        _previewOwnedResource = new TrackedDisposable("Preview:leaf-member", log);
    }

    [ReactiveCommand]
    private void ToggleLive() => IsLive = !IsLive;

    partial void OnDisposing() => DisposalLog.Add("Preview:leaf-disposing");

    partial void OnDisposed() => DisposalLog.Add("Preview:leaf-disposed");
}

