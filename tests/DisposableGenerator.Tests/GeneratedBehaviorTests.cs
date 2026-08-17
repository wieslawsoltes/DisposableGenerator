using System.Collections.Concurrent;

namespace DisposableGenerator.Tests;

public sealed class GeneratedBehaviorTests
{
    [Fact]
    public void Owned_members_dynamic_resources_and_hooks_have_defined_order()
    {
        var events = new List<string>();
        var owner = new LeafOwner(events);

        owner.Dispose();

        Assert.Equal(
            ["hook:disposing", "member:second", "member:first", "dynamic:second", "dynamic:first", "hook:disposed"],
            events);
    }

    [Fact]
    public void Dispose_is_idempotent_and_thread_safe()
    {
        var events = new ConcurrentQueue<string>();
        var owner = new ConcurrentOwner(events);

        Parallel.For(0, 32, _ => owner.Dispose());

        Assert.Equal(["resource"], events);
    }

    [Fact]
    public void Registration_after_disposal_throws_without_taking_ownership()
    {
        var events = new List<string>();
        var owner = new LeafOwner(events);
        owner.Dispose();

        var late = new TrackingDisposable("late", events);
        Assert.Throws<ObjectDisposedException>(() => owner.Own(late));
        Assert.DoesNotContain("late", events);
    }

    [Fact]
    public void Registration_is_closed_before_generated_derived_cleanup_begins()
    {
        var events = new List<string>();
        var owner = new RegisteringDerivedOwner(events);

        owner.Dispose();

        Assert.Equal(["derived:registration-rejected"], events);
    }

    [Fact]
    public void Null_registration_is_rejected()
    {
        var owner = new LeafOwner([]);

        Assert.Throws<ArgumentNullException>(() => owner.Own<IDisposable>(null!));
    }

    [Fact]
    public void Derived_cleanup_precedes_base_cleanup_and_base_is_chained()
    {
        var events = new List<string>();
        var owner = new DerivedOwner(events);

        owner.Dispose();
        owner.Dispose();

        Assert.Equal(
            [
                "derived:disposing",
                "derived:member",
                "derived:disposed",
                "base:disposing",
                "base:member",
                "dynamic",
                "base:disposed",
            ],
            events);
    }

    [Fact]
    public void Borrowed_disposable_is_not_implicitly_disposed()
    {
        var events = new List<string>();
        var borrowed = new TrackingDisposable("borrowed", events);
        var owner = new BorrowingOwner(borrowed);

        owner.Dispose();

        Assert.Empty(events);
    }

    [Fact]
    public void Base_cleanup_is_chained_when_derived_cleanup_throws()
    {
        var events = new List<string>();
        var owner = new ThrowingDerivedOwner(events);

        Assert.Throws<InvalidOperationException>(() => owner.Dispose());
        Assert.Equal(["derived:throw", "base:after-throw"], events);
    }
}

[GenerateDisposable]
internal sealed partial class LeafOwner
{
    private readonly List<string> _events;

    [DisposeMember]
    private readonly IDisposable _first;

    [DisposeMember]
    private readonly IDisposable _second;

    internal LeafOwner(List<string> events)
    {
        _events = events;
        _first = new TrackingDisposable("member:first", events);
        _second = new TrackingDisposable("member:second", events);
        RegisterDisposable(new TrackingDisposable("dynamic:first", events));
        RegisterDisposable(new TrackingDisposable("dynamic:second", events));
    }

    internal T Own<T>(T resource)
        where T : class, IDisposable => RegisterDisposable(resource);

    partial void OnDisposing() => _events.Add("hook:disposing");

    partial void OnDisposed() => _events.Add("hook:disposed");
}

[GenerateDisposable]
internal sealed partial class ConcurrentOwner
{
    [DisposeMember]
    private readonly IDisposable _resource;

    internal ConcurrentOwner(ConcurrentQueue<string> events)
    {
        _resource = new CallbackDisposable(() => events.Enqueue("resource"));
    }
}

[GenerateDisposable]
internal partial class BaseOwner
{
    private readonly List<string> _events;

    [DisposeMember]
    private readonly IDisposable _baseResource;

    protected BaseOwner(List<string> events)
    {
        _events = events;
        _baseResource = new TrackingDisposable("base:member", events);
        RegisterDisposable(new TrackingDisposable("dynamic", events));
    }

    partial void OnDisposing() => _events.Add("base:disposing");

    partial void OnDisposed() => _events.Add("base:disposed");
}

[GenerateDisposable]
internal sealed partial class DerivedOwner : BaseOwner
{
    private readonly List<string> _events;

    [DisposeMember]
    private readonly IDisposable _derivedResource;

    internal DerivedOwner(List<string> events)
        : base(events)
    {
        _events = events;
        _derivedResource = new TrackingDisposable("derived:member", events);
    }

    partial void OnDisposing() => _events.Add("derived:disposing");

    partial void OnDisposed() => _events.Add("derived:disposed");
}

[GenerateDisposable]
internal partial class RegistrationBaseOwner
{
    protected RegistrationBaseOwner()
    {
    }
}

[GenerateDisposable]
internal sealed partial class RegisteringDerivedOwner : RegistrationBaseOwner
{
    private readonly List<string> _events;

    internal RegisteringDerivedOwner(List<string> events)
    {
        _events = events;
    }

    partial void OnDisposing()
    {
        try
        {
            RegisterDisposable(new TrackingDisposable("late:disposed", _events));
        }
        catch (ObjectDisposedException)
        {
            _events.Add("derived:registration-rejected");
        }
    }
}

[GenerateDisposable]
internal sealed partial class BorrowingOwner
{
    [BorrowedMember]
    private readonly IDisposable _borrowed;

    internal BorrowingOwner(IDisposable borrowed)
    {
        _borrowed = borrowed;
    }
}

[GenerateDisposable]
internal partial class BaseAfterDerivedFailure
{
    [DisposeMember]
    private readonly IDisposable _baseResource;

    protected BaseAfterDerivedFailure(List<string> events)
    {
        _baseResource = new TrackingDisposable("base:after-throw", events);
    }
}

[GenerateDisposable]
internal sealed partial class ThrowingDerivedOwner : BaseAfterDerivedFailure
{
    [DisposeMember]
    private readonly IDisposable _derivedResource;

    internal ThrowingDerivedOwner(List<string> events)
        : base(events)
    {
        _derivedResource = new CallbackDisposable(() =>
        {
            events.Add("derived:throw");
            throw new InvalidOperationException("Expected test failure.");
        });
    }
}

internal sealed class TrackingDisposable(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name);
}

internal sealed class CallbackDisposable(Action callback) : IDisposable
{
    public void Dispose() => callback();
}
