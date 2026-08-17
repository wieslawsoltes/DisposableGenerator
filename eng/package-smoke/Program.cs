using DisposableGenerator;

var events = new List<string>();
var owner = new Owner(events);
owner.AddDynamic(new Tracked("dynamic:first", events));
owner.AddDynamic(new Tracked("dynamic:second", events));
owner.Dispose();
owner.AddDynamic(new Tracked("late", events));

var expected = new[] { "second", "first", "dynamic:first", "dynamic:second", "late" };
if (!events.SequenceEqual(expected))
{
    throw new InvalidOperationException($"Unexpected disposal order: {string.Join(", ", events)}");
}

var asyncEvents = new List<string>();
var asyncOwner = new AsyncOwner(asyncEvents);
await asyncOwner.AddDynamicAsync(new AsyncTracked("dynamic:async", asyncEvents));
await asyncOwner.DisposeAsync();
await asyncOwner.AddDynamicAsync(new AsyncTracked("late:async", asyncEvents));

var expectedAsync = new[] { "member:async", "dynamic:async", "late:async" };
if (!asyncEvents.SequenceEqual(expectedAsync))
{
    throw new InvalidOperationException($"Unexpected async disposal order: {string.Join(", ", asyncEvents)}");
}

[GenerateDisposable]
internal sealed partial class Owner
{
    [DisposeMember]
    private readonly IDisposable _first;

    [DisposeMember(Order = -10)]
    private readonly IDisposable _second;

    [BorrowedMember]
    private readonly IDisposable _borrowed;

    internal Owner(ICollection<string> events)
    {
        _first = new Tracked("first", events);
        _second = new Tracked("second", events);
        _borrowed = new Tracked("borrowed", events);
    }

    internal void AddDynamic(IDisposable disposable) => Own(disposable);
}

internal sealed class Tracked(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name);
}

[GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
internal sealed partial class AsyncOwner
{
    [DisposeMember]
    private readonly IAsyncDisposable _resource;

    internal AsyncOwner(ICollection<string> events)
    {
        _resource = new AsyncTracked("member:async", events);
    }

    internal ValueTask<T> AddDynamicAsync<T>(T disposable)
        where T : IAsyncDisposable => OwnAsync(disposable);
}

internal sealed class AsyncTracked(string name, ICollection<string> events) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        events.Add(name);
        return default;
    }
}

[GenerateDisposable(GenerateFinalizer = true)]
internal sealed partial class FinalizableOwner
{
    partial void DisposeUnmanaged()
    {
        // Package compilation verifies the opt-in finalizer and unmanaged hook contract.
    }
}
