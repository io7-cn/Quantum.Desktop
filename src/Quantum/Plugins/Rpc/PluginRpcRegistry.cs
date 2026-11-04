using System.Text.Json;
using NOF.Contract;
using Quantum.Plugin.Abstraction;

namespace Quantum.Plugins;

internal sealed class PluginRpcRegistry
{
    private static readonly JsonSerializerOptions CatalogJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<PluginRpcEndpoint>> _routes;
    private readonly Func<PluginRpcHandler?>? _getInstallation;
    private readonly IReadOnlyList<PluginNotificationProvider> _notifications;

    private PluginRpcRegistry(
        IReadOnlyDictionary<string, IReadOnlyList<PluginRpcEndpoint>> routes,
        Func<PluginRpcHandler?>? getInstallation = null,
        IReadOnlyList<PluginNotificationProvider>? notifications = null)
    {
        _routes = routes;
        _getInstallation = getInstallation;
        _notifications = notifications ?? [];
    }

    public static PluginRpcRegistry Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<PluginRpcEndpoint>>(StringComparer.Ordinal));

    public static PluginRpcRegistry Create(
        IEnumerable<PluginRpcRuntime> runtimes,
        Func<PluginRpcHandler?>? getInstallation = null,
        IReadOnlyList<PluginNotificationProvider>? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(runtimes);

        var routes = runtimes
            .SelectMany(static runtime => runtime.Methods.Select(method => new PluginRpcEndpoint(runtime, method)))
            .GroupBy(static endpoint => endpoint.Method.Capability.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<PluginRpcEndpoint>)group
                    .OrderBy(static endpoint => (string)endpoint.Runtime.PluginId, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        return new PluginRpcRegistry(routes, getInstallation, notifications);
    }

    internal Guid ResolveMobileTarget(string name, Guid? pageOwner = null)
    {
        var active = (_routes.GetValueOrDefault(name) ?? []).Where(endpoint => endpoint.Runtime.IsActive).ToArray();
        var own = active.FirstOrDefault(endpoint => endpoint.Runtime.RuntimeId == pageOwner);
        if (own is not null)
        {
            return own.Runtime.RuntimeId;
        }

        return active.Length == 1
            ? active[0].Runtime.RuntimeId
            : throw new InvalidOperationException("The mobile action requires one available provider.");
    }

    internal Task<PluginRpcDispatchResult> InvokeMobileAsync(string name, Guid runtimeId, JsonElement payload,
        PluginRpcCallContext context, CancellationToken cancellationToken)
    {
        var endpoint = FindEndpoint(name, runtimeId);
        return endpoint.Runtime.InvokeAsync(endpoint.Method, payload, context, cancellationToken);
    }

    private PluginRpcEndpoint FindEndpoint(string name, Guid? runtimeId = null)
    {
        if (_routes.TryGetValue(name, out var candidates)
            && candidates.FirstOrDefault(endpoint => endpoint.Runtime.IsActive
                && (runtimeId is null || endpoint.Runtime.RuntimeId == runtimeId)) is { } endpoint)
        {
            return endpoint;
        }

        throw new InvalidOperationException("The mobile capability provider is unavailable. Refresh the page.");
    }

    public async Task<Result<QuantumRequestReport<JsonElement?>>> RequestAsync(
        string name, JsonElement payload, PluginRpcCallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CapabilityName.IsValid(name))
        {
            return RequestFailure(PluginRpcErrors.InvalidName, "The capability name is invalid.");
        }

        if (payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Result<QuantumRequestReport<JsonElement?>>.From(Result.Fail(PluginRpcErrors.InvalidPayload,
                "The request payload at $ must be non-null.", new Dictionary<string, string> { ["path"] = "$" }));
        }

        if (name is QuantumCapabilityCatalog.CapabilityName
            or QuantumPluginInstallationExtensions.InstallMarketplaceCapabilityName
            or QuantumPluginInstallationExtensions.InstallLocalCapabilityName
            or QuantumPluginInstallationExtensions.GetPluginInstallStatusCapabilityName)
        {
            var response = name == QuantumCapabilityCatalog.CapabilityName
                ? SerializeResult(CreateCapabilityCatalog())
                : _getInstallation?.Invoke() is { } installation
                    ? await installation(name, payload, context, cancellationToken).ConfigureAwait(false)
                    : PluginRpcDispatchResult.Fail(PluginRpcErrors.Unavailable, "Plugin installation is unavailable in this host.");
            return Report([ReadReply("quantum.host", response, false, false).Outcome]);
        }

        if (_notifications.Any(provider => provider.Name == name))
        {
            return RequestFailure("capability_kind_mismatch", $"Capability '{name}' is a notification.");
        }

        var candidates = _routes.GetValueOrDefault(name) ?? [];
        var active = candidates.Where(endpoint => endpoint.Runtime.IsActive)
            .OrderBy(endpoint => endpoint.Method.Policy.Order)
            .ThenBy(endpoint => (string)endpoint.Runtime.PluginId, StringComparer.Ordinal).ToArray();
        if (active.Length == 0)
        {
            return Report([]);
        }

        var strategy = StrategyOf(active[0]);
        if (candidates.Any(endpoint => StrategyOf(endpoint) != strategy))
        {
            return RequestFailure("capability_policy_conflict", $"Providers of '{name}' disagree on its execution policy.");
        }

        if (strategy.RequireSingleProvider && active.Length != 1)
        {
            return RequestFailure("capability_owner_required", $"Capability '{name}' requires one eligible provider.");
        }

        context = context with { IsRequest = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        if (strategy.Dispatch == QuantumDispatch.All)
        {
            var replies = await Task.WhenAll(active.Select(endpoint => DispatchAsync(endpoint, payload, context, deadline.Token)))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Report(replies.Select(reply => reply.Outcome).ToArray());
        }

        var outcomes = new List<QuantumRequestOutcome<JsonElement?>>(active.Length);
        foreach (var endpoint in active)
        {
            if (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Report(outcomes, stopped: true);
            }

            var reply = await DispatchAsync(endpoint, payload, context, deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(reply.Outcome);
            if (strategy.UniqueOwner && reply.Outcome.Status != QuantumRequestStatus.Pass
                || strategy.AllowStop && reply.Stop)
            {
                return Report(outcomes, stopped: true);
            }
        }

        return Report(outcomes);
    }

    private static (QuantumDispatch Dispatch, bool UniqueOwner, bool AllowStop, bool RequireSingleProvider)
        StrategyOf(PluginRpcEndpoint endpoint)
        => (endpoint.Method.Policy.Dispatch, endpoint.Method.Policy.UniqueOwner,
            endpoint.Method.Policy.AllowStop, endpoint.Method.Policy.RequireSingleProvider);

    private static async Task<(QuantumRequestOutcome<JsonElement?> Outcome, bool Stop)> DispatchAsync(
        PluginRpcEndpoint endpoint, JsonElement payload, PluginRpcCallContext context, CancellationToken deadline)
    {
        var provider = (string)endpoint.Runtime.PluginId;
        try
        {
            var response = await Task.Run(
                    () => endpoint.Runtime.InvokeAsync(endpoint.Method, payload, context, deadline), deadline)
                .WaitAsync(deadline).ConfigureAwait(false);
            return ReadReply(provider, response, endpoint.Method.HasRequestResult, endpoint.Method.Policy.AllowStop);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return (new(provider, QuantumRequestStatus.TimedOut, null, "request_timeout", "The provider did not finish before the deadline."), false);
        }
    }

    private static (QuantumRequestOutcome<JsonElement?> Outcome, bool Stop) ReadReply(
        string provider, PluginRpcDispatchResult response, bool hasRequestResult, bool allowStop)
    {
        if (response.Failure is { } failure)
        {
            return (new(provider, QuantumRequestStatus.Failed, null, failure.ErrorCode, failure.Message) { Extra = failure.Extra }, false);
        }

        var result = response.SerializedResult!.Value;
        var metadata = result.TryGetProperty("extra", out var extra)
            ? extra.Deserialize<Dictionary<string, string>>() : null;
        if (!result.GetProperty("isSuccess").GetBoolean())
        {
            var code = result.GetProperty("errorCode").GetString();
            return code == QuantumRequestContext.PassCode
                ? (new(provider, QuantumRequestStatus.Pass, null, null, null), false)
                : (new(provider, QuantumRequestStatus.Failed, null, code, result.GetProperty("message").GetString())
                {
                    Extra = metadata
                }, false);
        }

        JsonElement? value = result.TryGetProperty("value", out var answer) && answer.ValueKind != JsonValueKind.Null
            ? answer : null;
        var stop = hasRequestResult && result.TryGetProperty("stop", out var stopValue) && stopValue.GetBoolean();
        if (stop && !allowStop)
        {
            return (new(provider, QuantumRequestStatus.Failed, null, PluginRpcErrors.InvalidResult,
                "This capability does not allow the provider to stop propagation."), false);
        }

        return (new(provider, QuantumRequestStatus.Handled, value, null, null) { HasValue = value.HasValue, Extra = metadata }, stop);
    }

    private static Result<QuantumRequestReport<JsonElement?>> RequestFailure(string code, string message)
        => Result<QuantumRequestReport<JsonElement?>>.From(Result.Fail(code, message));

    private static Result<QuantumRequestReport<JsonElement?>> Report(
        IReadOnlyList<QuantumRequestOutcome<JsonElement?>> outcomes, bool stopped = false)
        => Result.Success(new QuantumRequestReport<JsonElement?>(outcomes, stopped));

    private QuantumCapabilityCatalog CreateCapabilityCatalog()
    {
        var entries = GetHostCapabilities().ToList();

        foreach (var candidates in _routes.Values)
        {
            var active = candidates.Where(endpoint => endpoint.Runtime.IsActive).ToArray();
            if (active.FirstOrDefault() is { } selected)
            {
                var strategy = StrategyOf(selected);
                entries.Add(selected.Method.Capability with
                {
                    ProviderPluginIds = active.Select(endpoint => endpoint.Runtime.PluginId).ToArray(),
                    Dispatch = strategy.Dispatch,
                    UniqueOwner = strategy.UniqueOwner,
                    AllowStop = strategy.AllowStop,
                    RequireSingleProvider = strategy.RequireSingleProvider
                });
            }
        }

        foreach (var providers in _notifications.Where(provider => provider.IsActive()).GroupBy(provider => provider.Name))
        {
            var ordered = providers.OrderBy(provider => provider.PluginId, StringComparer.Ordinal).ToArray();
            var contract = ordered[0].Contract;
            entries.Add(new QuantumCapabilityDescription(providers.Key, QuantumCapabilityKind.Notify, 1,
                contract.Description, ordered.Select(provider => provider.PluginId).ToArray(),
                null, null, contract.MessageSchema)
            { Dispatch = contract.Dispatch, AllowStop = contract.StopObservers.Count > 0 });
        }

        return new QuantumCapabilityCatalog(2, entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray());
    }

    private static PluginRpcDispatchResult SerializeResult<T>(T value)
    {
        var result = Result.Success(value);
        return PluginRpcDispatchResult.Success(
            JsonSerializer.SerializeToElement(result, result.GetType(), CatalogJsonOptions));
    }

    private IEnumerable<QuantumCapabilityDescription> GetHostCapabilities()
    {
        yield return CreateHostCatalogCapability();
        if (_getInstallation?.Invoke() is not null)
        {
            foreach (var capability in CreateInstallationCapabilities())
            {
                yield return capability;
            }
        }
    }

    private static QuantumCapabilityDescription[] CreateInstallationCapabilities()
        =>
        [
            InstallationCapability(QuantumPluginInstallationExtensions.InstallMarketplaceCapabilityName,
                "Queues installation of the compatible Marketplace release after user approval.",
                ["pluginId"]),
            InstallationCapability(QuantumPluginInstallationExtensions.InstallLocalCapabilityName,
                "Queues installation of a ZIP supplied as base64 after user approval. The filename is not a filesystem path.",
                ["archiveFileName", "archiveBase64"]),
            InstallationCapability(QuantumPluginInstallationExtensions.GetPluginInstallStatusCapabilityName,
                "Returns the status of an installation requested by this plugin.",
                ["operationId"])
        ];

    private static QuantumCapabilityDescription InstallationCapability(
        string name, string description, string[] fields)
        => new(name, QuantumCapabilityKind.Request, 1, description, ["quantum.host"],
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = fields.ToDictionary(field => field, _ => new { type = "string" }),
                required = fields
            }, CatalogJsonOptions),
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    operationId = new { type = "string" },
                    status = new { type = "string", @enum = new[] { "pending", "succeeded", "failed" } },
                    message = new { type = new[] { "string", "null" } }
                },
                required = new[] { "operationId", "status" }
            }, CatalogJsonOptions),
            null)
        { Dispatch = QuantumDispatch.Ordered, UniqueOwner = true, RequireSingleProvider = true };

    private static QuantumCapabilityDescription CreateHostCatalogCapability()
        => new(
            QuantumCapabilityCatalog.CapabilityName, QuantumCapabilityKind.Request, 1,
            "Lists available capabilities, their providers and JSON schemas.", ["quantum.host"],
            RequestSchema: JsonSerializer.SerializeToElement(
                new { type = "object", additionalProperties = false }, CatalogJsonOptions),
            ResponseSchema: JsonSerializer.SerializeToElement(
                new
                {
                    type = "object",
                    properties = new
                    {
                        schemaVersion = new { type = "integer", @const = 2 },
                        capabilities = new { type = "array" }
                    },
                    required = new[] { "schemaVersion", "capabilities" }
                }, CatalogJsonOptions),
            MessageSchema: null)
        { Dispatch = QuantumDispatch.Ordered, UniqueOwner = true, RequireSingleProvider = true };
}

internal sealed record PluginNotificationProvider(
    string Name, string PluginId, QuantumNotificationContract Contract, Func<bool> IsActive);

internal delegate Task<PluginRpcDispatchResult> PluginRpcHandler(
    string capabilityName, JsonElement payload, PluginRpcCallContext context, CancellationToken cancellationToken);

internal sealed record PluginRpcEndpoint(
    PluginRpcRuntime Runtime,
    PluginRpcMethodDefinition Method);

internal sealed record PluginRpcCallContext(
    string CallerPluginId,
    string CallerRuntimeId,
    IReadOnlyDictionary<string, JsonElement> Items, bool IsRequest = false);

internal sealed record PluginRpcDispatchResult(
    Result? Failure,
    JsonElement? SerializedResult)
{
    public static PluginRpcDispatchResult Fail(string errorCode, string message, IDictionary<string, string>? extra = null)
        => new(Result.Fail(errorCode, message, extra), null);

    public static PluginRpcDispatchResult Success(JsonElement result)
        => new(null, result);
}

internal static class PluginRpcErrors
{
    public const string InvalidName = "rpc_invalid_name";
    public const string NotFound = "rpc_not_found";
    public const string InvalidPayload = "rpc_invalid_payload";
    public const string InvalidContext = "rpc_invalid_context";
    public const string InvocationFailed = "rpc_invocation_failed";
    public const string InvalidResult = "rpc_invalid_result";
    public const string Unavailable = "rpc_unavailable";
}
