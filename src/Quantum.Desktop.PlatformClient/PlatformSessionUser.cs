namespace Quantum.Mobile.Internal;

public sealed class PlatformSessionUser
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public int Roles { get; set; }
}
