namespace DisposableGenerator.AvaloniaReactiveUI.Sample.Infrastructure;

public sealed class ApplicationService(DisposalLog log) : IDisposable
{
    private int _disposed;

    public string EnvironmentName => "ReactiveUI 24 / Avalonia 12";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            log.Add("application-service:disposed");
        }
    }
}

