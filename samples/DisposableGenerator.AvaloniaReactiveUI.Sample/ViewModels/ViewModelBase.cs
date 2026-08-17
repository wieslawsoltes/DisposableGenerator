using DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace DisposableGenerator.AvaloniaReactiveUI.Sample.ViewModels;

[GenerateDisposable]
public abstract partial class ViewModelBase : ReactiveObject
{
    private readonly DisposalLog _log;

    [BorrowedMember]
    private readonly ApplicationService _applicationService;

    [DisposeMember]
    private readonly IDisposable _baseOwnedResource;

    [Reactive]
    private string _displayName;

    protected ViewModelBase(string displayName, ApplicationService applicationService, DisposalLog log)
    {
        _displayName = displayName;
        _applicationService = applicationService;
        _log = log;
        _baseOwnedResource = new TrackedDisposable($"{displayName}:base-member", log);
        RegisterDisposable(new TrackedDisposable($"{displayName}:base-registered", log));
    }

    protected DisposalLog DisposalLog => _log;

    protected ApplicationService ApplicationService => _applicationService;

    protected T Track<T>(T disposable)
        where T : IDisposable => RegisterDisposable(disposable);

    partial void OnDisposing() => _log.Add($"{DisplayName}:base-disposing");

    partial void OnDisposed() => _log.Add($"{DisplayName}:base-disposed");
}

