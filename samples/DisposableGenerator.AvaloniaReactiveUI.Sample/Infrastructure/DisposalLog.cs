namespace DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;

public sealed class DisposalLog
{
    private readonly List<string> _entries = [];
    private readonly object _gate = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Add(string entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }
}

