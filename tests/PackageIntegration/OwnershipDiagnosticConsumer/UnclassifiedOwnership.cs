using DisposableGenerator;

[GenerateDisposable]
internal sealed partial class UnclassifiedOwnership
{
    private readonly IDisposable _resource = new MemoryStream();
}
