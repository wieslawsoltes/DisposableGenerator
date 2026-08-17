using DisposableGenerator;

var events = new List<string>();
var borrowed = new Tracked("borrowed", events);
var owner = new DerivedOwner(events, borrowed);
owner.AddDynamic(new Tracked("dynamic:first", events));
owner.AddDynamic(new Tracked("dynamic:second", events));

await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(owner.Dispose)));
owner.Dispose();

AssertSequence(
    events,
    "derived:disposing",
    "derived:owned",
    "derived:disposed",
    "base:disposing",
    "base:second",
    "base:first",
    "dynamic:second",
    "dynamic:first",
    "base:disposed");

if (borrowed.DisposeCount != 0)
{
    throw new InvalidOperationException("A borrowed dependency was disposed.");
}

var late = new Tracked("late", events);
try
{
    owner.AddDynamic(late);
    throw new InvalidOperationException("Late registration should throw ObjectDisposedException.");
}
catch (ObjectDisposedException)
{
}

if (late.DisposeCount != 0)
{
    throw new InvalidOperationException("Default late registration took ownership of the resource.");
}

static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
{
    if (!actual.SequenceEqual(expected))
    {
        throw new InvalidOperationException(
            $"Unexpected sync disposal order. Expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");
    }
}

[GenerateDisposable]
internal partial class BaseOwner
{
    private readonly ICollection<string> _events;

    [DisposeMember]
    private readonly IDisposable _baseFirst;

    [DisposeMember]
    private readonly IDisposable _baseSecond;

    protected BaseOwner(ICollection<string> events)
    {
        _events = events;
        _baseFirst = new Tracked("base:first", events);
        _baseSecond = new Tracked("base:second", events);
    }

    internal void AddDynamic(IDisposable value) => RegisterDisposable(value);

    partial void OnDisposing() => _events.Add("base:disposing");

    partial void OnDisposed() => _events.Add("base:disposed");
}

[GenerateDisposable]
internal sealed partial class DerivedOwner : BaseOwner
{
    private readonly ICollection<string> _events;

    [DisposeMember]
    private readonly IDisposable _owned;

    [BorrowedMember]
    private readonly IDisposable _borrowed;

    internal DerivedOwner(ICollection<string> events, IDisposable borrowed)
        : base(events)
    {
        _events = events;
        _owned = new Tracked("derived:owned", events);
        _borrowed = borrowed;
    }

    partial void OnDisposing() => _events.Add("derived:disposing");

    partial void OnDisposed() => _events.Add("derived:disposed");
}

internal sealed class Tracked(string name, ICollection<string> events) : IDisposable
{
    internal int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
        events.Add(name);
    }
}
