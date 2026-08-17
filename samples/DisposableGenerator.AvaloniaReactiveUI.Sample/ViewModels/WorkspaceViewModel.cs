using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using ReactiveUI.SourceGenerators;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;

[GenerateDisposable]
public sealed partial class WorkspaceViewModel : ViewModelBase
{
    [DisposeMember]
    private readonly EditorViewModel _editor;

    [DisposeMember]
    private readonly PreviewViewModel _preview;

    [Reactive]
    private string _selectedSection = "Editor and preview are both active";

    public WorkspaceViewModel(ApplicationService applicationService, DisposalLog log)
        : base("Workspace", applicationService, log)
    {
        _editor = new EditorViewModel(applicationService, log);
        _preview = new PreviewViewModel(applicationService, log);
    }

    public EditorViewModel Editor => _editor;

    public PreviewViewModel Preview => _preview;

    public string DisposalExplanation =>
        "Workspace owns both sealed leaf view models. Each leaf disposes before its document and ReactiveObject base levels.";

    partial void OnDisposing() => DisposalLog.Add("Workspace:leaf-disposing");

    partial void OnDisposed() => DisposalLog.Add("Workspace:leaf-disposed");
}

