using System.ComponentModel;
using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using ReactiveUI.SourceGenerators;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;

[GenerateDisposable]
public sealed partial class EditorViewModel : DocumentViewModelBase
{
    [DisposeMember]
    private readonly IDisposable _editorOwnedResource;

    [Reactive]
    private string _text = "Edit this ReactiveUI-generated property.";

    [Reactive]
    private string _status = "Ready";

    public EditorViewModel(ApplicationService applicationService, DisposalLog log)
        : base("Editor", "Disposable document", applicationService, log)
    {
        _editorOwnedResource = new TrackedDisposable("Editor:leaf-member", log);

        PropertyChangedEventHandler textObserver = (_, args) =>
        {
            if (args.PropertyName == nameof(Text))
            {
                Status = $"{Text.Length} characters";
            }
        };

        PropertyChanged += textObserver;
        Track(new TrackedDisposable(
            "Editor:text-observer",
            log,
            () => PropertyChanged -= textObserver));
    }

    [ReactiveCommand]
    private void Save()
    {
        Status = $"Saved through {ApplicationService.EnvironmentName}";
        DisposalLog.Add("Editor:save-command");
    }

    partial void OnDisposing() => DisposalLog.Add("Editor:leaf-disposing");

    partial void OnDisposed() => DisposalLog.Add("Editor:leaf-disposed");
}

