namespace Quantum.Mobile.Internal;

public sealed class PlatformAuthenticationException() : PlatformRpcException("authentication_required", "Your login has expired. Sign in again.");
