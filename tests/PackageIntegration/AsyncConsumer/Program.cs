using DisposableGenerator;

var asyncEvents = new List<string>();
var asyncOwner = new AsyncOnlyOwner(asyncEvents);
await asyncOwner.AddAsync(new AsyncTracked("dynamic:first", asyncEvents));
await asyncOwner.AddAsync(new AsyncTracked("dynamic:second", asyncEvents));

await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => asyncOwner.DisposeAsync().AsTask()));
await asyncOwner.DisposeAsync();
AssertSequence(asyncEvents, "member", "dynamic:second", "dynamic:first");

var asyncLate = new AsyncTracked("late", asyncEvents);
try
{
    await asyncOwner.AddAsync(asyncLate);
    throw new InvalidOperationException("Late async registration should throw ObjectDisposedException.");
}
catch (ObjectDisposedException)
{
}

if (asyncLate.AsyncDisposeCount != 0)
{
    throw new InvalidOperationException("Default late async registration took ownership.");
}

var asyncDualEvents = new List<string>();
await new ConjunctiveOwner(asyncDualEvents).DisposeAsync();
AssertSequence(asyncDualEvents, "sync-only:sync", "dual:async");

var syncDualEvents = new List<string>();
new ConjunctiveOwner(syncDualEvents).Dispose();
AssertSequence(syncDualEvents, "sync-only:sync", "dual:sync");

var competingEvents = new List<string>();
var competing = new ConjunctiveSingleOwner(competingEvents);
await Task.WhenAll(Task.Run(competing.Dispose), competing.DisposeAsync().AsTask());
if (competingEvents.Count != 1)
{
    throw new InvalidOperationException($"Competing sync/async disposal cleaned up {competingEvents.Count} times.");
}

var attempts = new List<string>();
try
{
    await new FailingAsyncOwner(attempts).DisposeAsync();
    throw new InvalidOperationException("Aggregate cleanup should throw.");
}
catch (AggregateException exception) when (exception.InnerExceptions.Count == 2)
{
}

AssertSequence(attempts, "second", "first");

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"Unexpected async disposal order. Expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
    }
}

[GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
internal sealed partial class AsyncOnlyOwner
{
    [DisposeMember]
    private readonly IAsyncDisposable _member;

    internal AsyncOnlyOwner(ICollection<string> events)
    {
        _member = new AsyncTracked("member", events);
    }

    internal ValueTask<T> AddAsync<T>(T value)
        where T : IAsyncDisposable => RegisterAsyncDisposable(value);
}

[GenerateDisposable(GenerateAsyncDispose = true)]
internal sealed partial class ConjunctiveOwner
{
    [DisposeMember]
    private readonly DualTracked _dual;

    [DisposeMember]
    private readonly IDisposable _syncOnly;

    internal ConjunctiveOwner(ICollection<string> events)
    {
        _dual = new DualTracked("dual", events);
        _syncOnly = new SyncTracked("sync-only", events);
    }
}

[GenerateDisposable(GenerateAsyncDispose = true)]
internal sealed partial class ConjunctiveSingleOwner
{
    [DisposeMember]
    private readonly DualTracked _resource;

    internal ConjunctiveSingleOwner(ICollection<string> events)
    {
        _resource = new DualTracked("competing", events);
    }
}

[GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
internal sealed partial class FailingAsyncOwner
{
    [DisposeMember]
    private readonly IAsyncDisposable _first;

    [DisposeMember]
    private readonly IAsyncDisposable _second;

    internal FailingAsyncOwner(ICollection<string> attempts)
    {
        _first = new ThrowingAsyncTracked("first", attempts);
        _second = new ThrowingAsyncTracked("second", attempts);
    }
}

internal sealed class AsyncTracked(string name, ICollection<string> events) : IAsyncDisposable
{
    internal int AsyncDisposeCount { get; private set; }

    public ValueTask DisposeAsync()
    {
        AsyncDisposeCount++;
        events.Add(name);
        return default;
    }
}

internal sealed class DualTracked(string name, ICollection<string> events) : IDisposable, IAsyncDisposable
{
    public void Dispose() => events.Add(name + ":sync");

    public ValueTask DisposeAsync()
    {
        events.Add(name + ":async");
        return default;
    }
}

internal sealed class SyncTracked(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name + ":sync");
}

internal sealed class ThrowingAsyncTracked(string name, ICollection<string> attempts) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        attempts.Add(name);
        return ValueTask.FromException(new InvalidOperationException(name));
    }
}
