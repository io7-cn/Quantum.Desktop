namespace Quantum.Components;

public sealed record OperationNotice(string Message, bool IsError = false)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
}
