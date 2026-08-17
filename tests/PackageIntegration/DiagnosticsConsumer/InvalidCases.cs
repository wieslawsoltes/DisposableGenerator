using DisposableGenerator;

[GenerateDisposable]
internal class NotPartial
{
}

[GenerateDisposable]
internal partial record RecordOwner
{
}

[GenerateDisposable]
internal sealed partial class ConflictingOwnership
{
    [DisposeMember]
    [BorrowedMember]
    private readonly IDisposable _resource = new MemoryStream();
}

[GenerateDisposable]
internal sealed partial class MissingAsyncMode
{
    [DisposeMember]
    private readonly IAsyncDisposable _resource = new AsyncResource();
}

[GenerateDisposable(GenerateSynchronousDispose = false)]
internal sealed partial class NoInterfaceMode
{
}

internal sealed class AsyncResource : IAsyncDisposable
{
    public ValueTask DisposeAsync() => default;
}
