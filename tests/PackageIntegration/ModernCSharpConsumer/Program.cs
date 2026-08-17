using DisposableGenerator;

var events = new List<string>();
new PrimaryOwner(new Tracked("primary", events)).Dispose();
new Outer<string>.Nested<object>(new Tracked("nested", events)).Dispose();

var modern = new ModernProperties();
modern.Resource = new Tracked("field-backed", events);
modern.PartialResource = new Tracked("partial-property", events);
modern.Dispose();

var expected = new[] { "primary", "nested", "partial-property", "field-backed" };
if (!events.SequenceEqual(expected))
{
    throw new InvalidOperationException(
        $"Unexpected modern C# behavior. Expected [{string.Join(", ", expected)}], actual [{string.Join(", ", events)}].");
}

[GenerateDisposable]
internal sealed partial class PrimaryOwner(IDisposable resource)
{
    [DisposeMember]
    private IDisposable Resource { get; } = resource;
}

internal partial record class Outer<T>
    where T : class
{
    [GenerateDisposable]
    internal sealed partial class Nested<U>(IDisposable resource)
        where U : class
    {
        [DisposeMember]
        private readonly IDisposable @event = resource;
    }
}

[GenerateDisposable]
internal sealed partial class ModernProperties
{
    [BorrowedMember]
    private IDisposable? _partialResource;

    [DisposeMember]
    internal IDisposable Resource
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = null!;

    [DisposeMember]
    internal partial IDisposable? PartialResource { get; set; }

    internal partial IDisposable? PartialResource
    {
        get => _partialResource;
        set => _partialResource = value;
    }
}

internal sealed class Tracked(string name, ICollection<string> events) : IDisposable
{
    public void Dispose() => events.Add(name);
}
