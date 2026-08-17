using DisposableGenerator;

var events = new List<string>();
using (var editor = new EditorViewModel(events))
{
    editor.OpenDocument();
}

Console.WriteLine(string.Join(Environment.NewLine, events));

[GenerateDisposable]
internal partial class ViewModel
{
    private readonly List<string> _events;

    [DisposeMember]
    private readonly IDisposable _baseSubscription;

    protected ViewModel(List<string> events)
    {
        _events = events;
        _baseSubscription = new Subscription("base subscription", events);
    }

    protected void Track(IDisposable resource) => RegisterDisposable(resource);

    partial void OnDisposing() => _events.Add("base cleanup starting");
}

[GenerateDisposable]
internal sealed partial class EditorViewModel : ViewModel
{
    private readonly List<string> _events;

    [DisposeMember]
    private readonly IDisposable _editorSubscription;

    [BorrowedMember]
    private readonly IDisposable _borrowedService;

    internal EditorViewModel(List<string> events)
        : base(events)
    {
        _events = events;
        _editorSubscription = new Subscription("editor subscription", events);
        _borrowedService = new Subscription("borrowed service (not disposed)", events);
    }

    internal void OpenDocument() => Track(new Subscription("document subscription", _events));

    partial void OnDisposing() => _events.Add("editor cleanup starting");
}

internal sealed class Subscription(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name);
}
