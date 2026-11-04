namespace Quantum.Mobile.Internal;

public class PlatformRpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
