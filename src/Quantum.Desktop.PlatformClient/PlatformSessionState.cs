namespace Quantum.Mobile.Internal;

public sealed class PlatformSessionState
{
    public string AccessToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RefreshAfterUtc { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshExpiresAtUtc { get; set; }
    public PlatformSessionUser? User { get; set; }
    public Guid? PendingRefreshId { get; set; }
    public override string ToString() => "[REDACTED]";
}
