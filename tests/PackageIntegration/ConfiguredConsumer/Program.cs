using DisposableGenerator;

var events = new List<string>();
var owner = new ConfiguredOwner(events);
owner.Add(new Tracked("dynamic:first", events));
owner.Add(new Tracked("dynamic:second", events));
owner.Dispose();
owner.Add(new Tracked("late", events));

AssertSequence(
    events,
    "hook:disposing",
    "member:first",
    "member:second",
    "nullable:struct",
    "dynamic:first",
    "dynamic:second",
    "hook:disposed",
    "unmanaged",
    "late");

var attempts = new List<string>();
try
{
    new FailingOwner(attempts).Dispose();
    throw new InvalidOperationException("Configured aggregate cleanup should throw.");
}
catch (AggregateException exception) when (exception.InnerExceptions.Count == 2)
{
}

AssertSequence(attempts, "first", "second");

var asyncEvents = new List<string>();
var asyncOwner = new ConfiguredAsyncOwner(asyncEvents);
await asyncOwner.AddAsync(new AsyncTracked("dynamic:async", asyncEvents));
await asyncOwner.DisposeAsync();
await asyncOwner.AddAsync(new AsyncTracked("late:async", asyncEvents));
AssertSequence(asyncEvents, "member:async", "dynamic:async", "late:async");

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"Unexpected configured disposal order. Expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
    }
}

[GenerateDisposable(GenerateUnmanagedCleanup = true)]
internal sealed partial class ConfiguredOwner
{
    private readonly ICollection<string> _events;

    [DisposeMember]
    private readonly IDisposable _first;

    [DisposeMember]
    private readonly IDisposable _second;

    [DisposeMember(Order = 100)]
    private readonly DisposableToken? _nullableToken;

    internal ConfiguredOwner(ICollection<string> events)
    {
        _events = events;
        _first = new Tracked("member:first", events);
        _second = new Tracked("member:second", events);
        _nullableToken = new DisposableToken("nullable:struct", events);
    }

    internal void Add(IDisposable value) => Own(value);

    partial void OnDisposing() => _events.Add("hook:disposing");

    partial void DisposeUnmanaged() => _events.Add("unmanaged");

    partial void OnDisposed() => _events.Add("hook:disposed");
}

[GenerateDisposable]
internal sealed partial class FailingOwner
{
    [DisposeMember]
    private readonly IDisposable _first;

    [DisposeMember]
    private readonly IDisposable _second;

    internal FailingOwner(ICollection<string> attempts)
    {
        _first = new ThrowingTracked("first", attempts);
        _second = new ThrowingTracked("second", attempts);
    }
}

[GenerateDisposable(GenerateSynchronousDispose = false, GenerateAsyncDispose = true)]
internal sealed partial class ConfiguredAsyncOwner
{
    [DisposeMember]
    private readonly IAsyncDisposable _resource;

    internal ConfiguredAsyncOwner(ICollection<string> events)
    {
        _resource = new AsyncTracked("member:async", events);
    }

    internal ValueTask<T> AddAsync<T>(T value)
        where T : IAsyncDisposable => OwnAsync(value);
}

internal readonly struct DisposableToken(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name);
}

internal sealed class Tracked(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name);
}

internal sealed class ThrowingTracked(string name, ICollection<string> attempts) : IDisposable
{
    public void Dispose()
    {
        attempts.Add(name);
        throw new InvalidOperationException(name);
    }
}

internal sealed class AsyncTracked(string name, ICollection<string> events) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        events.Add(name);
        return default;
    }
}

// Compilation of this type verifies package-level finalizer emission. Runtime ordering is
// validated on ConfiguredOwner so the test does not depend on nondeterministic collection.
[GenerateDisposable(GenerateFinalizer = true)]
internal sealed partial class FinalizableOwner
{
    partial void DisposeUnmanaged()
    {
    }
}
