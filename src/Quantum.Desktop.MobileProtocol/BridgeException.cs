namespace Quantum.Mobile.Internal;

public sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
