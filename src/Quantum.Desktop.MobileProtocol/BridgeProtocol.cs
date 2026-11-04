using System.Text.Json;

namespace Quantum.Mobile.Internal;

public sealed record BridgeRequest(string Operation, string? DeviceId = null, string? DeviceSecret = null, JsonElement Body = default);
public sealed record BridgeDevice(string Id, string Name, string Kind, DateTimeOffset SeenAt, string SessionId);
public sealed record BridgeRegistration(BridgeDevice Device, string Secret);
public sealed record BridgePageInfo(string PluginId, string PageId, string Title)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? IconImage { get; init; }
}
public sealed record BridgeBinding(string DesktopId, string MobileId, string MobileName, bool Approved);
public sealed record BridgeMessage(string Id, string SenderId, string TargetId, string Kind, JsonElement Body,
    DateTimeOffset ExpiresAt, string? Lease = null);
public sealed record BridgeSnapshot(string ViewId, string DesktopSessionId, string PluginId, string RuntimeId,
    DateTimeOffset ExpiresAt, MobilePage Page, string? RequestId = null);
public sealed record BridgeOpen(string PluginId, string PageId);
public sealed record BridgePageError(string RequestId, string Code, string Message);
public sealed record BridgeInvoke(string ViewId, string ActionId, Dictionary<string, JsonElement> Inputs);
public sealed record BridgeOutcome(string RequestId, string Status, string Message, JsonElement? Result = null);
public sealed record BridgeSend(string Id, string TargetId, string Kind, JsonElement Body);
public sealed record BridgeAck(string Id, string Lease);
