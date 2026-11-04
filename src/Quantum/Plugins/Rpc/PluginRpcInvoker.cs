using System.Text.Json;
using NOF.Contract;
using Quantum.Plugin.Abstraction;

namespace Quantum.Plugins;

internal sealed class PluginRpcInvoker(
    string callerPluginId,
    Guid callerRuntimeId,
    PluginRpcSerializer serializer) : IQuantumRequester, IDisposable
{
    private PluginRpcRegistry? _registry;
    private int _disposed;

    public void UseRegistry(PluginRpcRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _registry, registry);
    }

    public async Task<Result<QuantumRequestReport<T>>> RequestAsync<T>(
        string capabilityName, object payload, Context context, CancellationToken cancellationToken = default)
    {
        var request = PrepareRequest(payload, context);
        var registry = Volatile.Read(ref _registry);
        if (request.Failure is not null || registry is null)
        {
            return Result<QuantumRequestReport<T>>.From(request.Failure
                ?? Result.Fail(PluginRpcErrors.Unavailable, "The plugin capability registry is not available."));
        }

        var result = await registry.RequestAsync(capabilityName, request.Payload!.Value, request.Context!, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Result.Success(new QuantumRequestReport<T>(result.Value!.Outcomes.Select(DecodeOutcome<T>).ToArray(), result.Value.Stopped))
            : Result<QuantumRequestReport<T>>.From(result);
    }

    private QuantumRequestOutcome<T> DecodeOutcome<T>(QuantumRequestOutcome<JsonElement?> outcome)
    {
        try
        {
            return new(outcome.ProviderPluginId, outcome.Status,
                outcome.Value is { } value ? serializer.Deserialize<T>(value) : default,
                outcome.ErrorCode, outcome.Message)
            { Extra = outcome.Extra, HasValue = outcome.HasValue };
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new(outcome.ProviderPluginId, QuantumRequestStatus.Failed, default,
                PluginRpcErrors.InvalidResult, "The provider reply does not match the requested response type.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Volatile.Write(ref _registry, null);
        }
    }

    private PreparedRpcRequest PrepareRequest(object payload, Context context)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return PreparedRpcRequest.Fail(
                PluginRpcErrors.Unavailable,
                "The plugin RPC invoker has been disposed.");
        }

        if (payload is null)
        {
            return PreparedRpcRequest.Fail(
                PluginRpcErrors.InvalidPayload,
                "An RPC payload is required.");
        }

        if (context is null)
        {
            return PreparedRpcRequest.Fail(
                PluginRpcErrors.InvalidContext,
                "An RPC context is required.");
        }

        try
        {
            var payloadElement = serializer.Serialize(payload, payload.GetType());
            var contextItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var item in context.Items)
            {
                if (item.Key is not string key
                    || IsReservedContextKey(key)
                    || contextItems.ContainsKey(key))
                {
                    return PreparedRpcRequest.Fail(
                        PluginRpcErrors.InvalidContext,
                        "RPC context keys must be unique strings and cannot use Quantum reserved keys.");
                }

                contextItems.Add(
                    key,
                    item.Value is JsonElement element
                        ? element.Clone()
                        : serializer.Serialize(
                            item.Value,
                            item.Value?.GetType() ?? typeof(object)));
            }

            return new PreparedRpcRequest(
                null,
                payloadElement,
                new PluginRpcCallContext(
                    (string)callerPluginId,
                    callerRuntimeId.ToString("N"),
                    contextItems));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return PreparedRpcRequest.Fail(
                PluginRpcErrors.InvalidPayload,
                "The RPC payload or context could not be serialized.");
        }
    }

    internal static bool IsReservedContextKey(string key)
        => string.Equals(key, QuantumRequestContext.Dispatched, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, QuantumRpcContextKeys.CallerPluginId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, QuantumRpcContextKeys.CallerRuntimeId, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("quantum.mobile.", StringComparison.OrdinalIgnoreCase);

    private sealed record PreparedRpcRequest(
        Result? Failure,
        JsonElement? Payload,
        PluginRpcCallContext? Context)
    {
        public static PreparedRpcRequest Fail(string errorCode, string message)
            => new(Result.Fail(errorCode, message), null, null);
    }
}
