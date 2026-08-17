namespace DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;

public sealed class TrackedDisposable(string name, DisposalLog log, Action? onDispose = null) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        onDispose?.Invoke();
        log.Add(name);
    }
}

