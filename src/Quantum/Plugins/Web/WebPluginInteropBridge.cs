using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NOF.Contract;
using Quantum.Plugins;

namespace Quantum.WebPlugins;

public sealed class WebPluginInteropBridge(
    PluginCatalog catalog,
    PluginRpcRouter rpcRouter,
    NavigationManager navigation,
    IJSRuntime javaScript,
    QuantumPluginEventBusFactory eventBusFactory,
    ILogger<WebPluginInteropBridge> logger) : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _invocations = new();
    private readonly ConcurrentDictionary<string, Lazy<WebEventBusRuntime>> _eventBuses = new();
    private readonly ConcurrentDictionary<string, IDisposable> _extensionHandles = new();
    private bool _disposed;

    [JSInvokable]
    public async Task<JsonElement> InvokeAsync(
        string pluginId,
        string runtimeId,
        string requestId,
        string capability,
        string method,
        JsonElement arguments)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var plugin = catalog.FindPlugin(pluginId)
            ?? throw new InvalidOperationException($"Plugin '{pluginId}' is not loaded.");
        if (plugin.Manifest.Runtime.Kind != PluginRuntimeKind.Web)
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' is not a Web plugin.");
        }

        if (!string.Equals(plugin.RuntimeId.ToString("N"), runtimeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Web plugin runtime '{runtimeId}' is stale and can no longer call host capabilities.");
        }

        var invocationKey = CreateInvocationKey(pluginId, runtimeId, requestId);
        using var cancellation = new CancellationTokenSource(InvocationTimeout);
        if (!_invocations.TryAdd(invocationKey, cancellation))
        {
            throw new InvalidOperationException($"RPC request '{requestId}' is already running.");
        }

        var startedAt = Stopwatch.GetTimestamp();
        logger.LogDebug(
            "Web RPC {Capability}.{Method} started for {PluginId}.",
            capability,
            method,
            pluginId);
        try
        {
            var result = capability switch
            {
                "log" => InvokeLog(pluginId, method, arguments),
                "environment" => InvokeEnvironment(plugin, method),
                "navigation" => InvokeNavigation(method, arguments),
                "mobile" => InvokeMobile(plugin, method, arguments),
                "extensions" => InvokeExtensions(plugin, method, arguments),
                "notify" => await InvokeNotifyAsync(
                        plugin, runtimeId, method, arguments, cancellation.Token)
                    .ConfigureAwait(false),
                "assets" => await InvokeAssetsAsync(plugin, method, arguments, cancellation.Token)
                    .ConfigureAwait(false),
                "request" => await InvokePluginRequestAsync(plugin, method, arguments, cancellation.Token)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown Web plugin capability '{capability}'.")
            };
            logger.LogDebug(
                "Web RPC {Capability}.{Method} completed for {PluginId} in {ElapsedMilliseconds:F0} ms.",
                capability,
                method,
                pluginId,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogDebug(
                exception,
                "Web RPC {Capability}.{Method} failed for {PluginId}.",
                capability,
                method,
                pluginId);
            throw;
        }
        finally
        {
            _invocations.TryRemove(invocationKey, out _);
        }
    }

    [JSInvokable]
    public Task CancelAsync(string pluginId, string runtimeId, string requestId)
    {
        if (_invocations.TryGetValue(CreateInvocationKey(pluginId, runtimeId, requestId), out var cancellation))
        {
            TryCancel(cancellation);
        }

        return Task.CompletedTask;
    }

    [JSInvokable]
    public async ValueTask ReleaseRuntimeAsync(string pluginId, string runtimeId)
    {
        var plugin = catalog.FindPlugin(pluginId);
        var prefix = CreateRuntimeKey(pluginId, runtimeId) + ":extensions:";
        foreach (var key in _extensionHandles.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (_extensionHandles.TryRemove(key, out var handle))
            {
                handle.Dispose();
            }
        }

        if (plugin?.RuntimeId.ToString("N") == runtimeId)
        {
            plugin.Extensions?.Clear();
        }
        if (plugin?.RuntimeId.ToString("N") == runtimeId)
        {
            plugin.MobilePages.Clear();
        }

        if (_eventBuses.TryRemove(CreateRuntimeKey(pluginId, runtimeId), out var runtime)
            && runtime.IsValueCreated)
        {
            await runtime.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    [JSInvokable]
    public void LogHostWarning(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        logger.LogWarning("Web plugin host: {HostMessage}", message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var cancellation in _invocations.Values)
        {
            TryCancel(cancellation);
        }

        _invocations.Clear();
        foreach (var runtime in _eventBuses.Values)
        {
            if (runtime.IsValueCreated)
            {
                runtime.Value.Dispose();
            }
        }

        _eventBuses.Clear();
        foreach (var handle in _extensionHandles.Values)
        {
            handle.Dispose();
        }

        _extensionHandles.Clear();
    }

    private JsonElement InvokeExtensions(LoadedPlugin plugin, string method, JsonElement arguments)
    {
        var extensions = plugin.Extensions
            ?? throw new InvalidOperationException("The plugin extension scope is unavailable.");
        var handleId = QuantumExtensionContract.LocalId(RequireString(arguments, "handleId"));
        var key = CreateRuntimeKey(plugin.Manifest.Id, plugin.RuntimeId.ToString("N")) + ":extensions:" + handleId;
        if (method == "dispose")
        {
            if (_extensionHandles.TryRemove(key, out var previous))
            {
                previous.Dispose();
            }

            return Serialize(null);
        }

        if (method == "update")
        {
            if (!_extensionHandles.TryGetValue(key, out var previous) || previous is not IQuantumContribution contribution)
            {
                throw new ObjectDisposedException(handleId);
            }

            contribution.Update(arguments.GetProperty("value"));
            return Serialize(null);
        }

        if (_extensionHandles.ContainsKey(key))
        {
            throw new InvalidOperationException("An extension handle with this ID already exists.");
        }

        var version = arguments.GetProperty("contractVersion").GetInt32();
        IDisposable handle = method switch
        {
            "define" => extensions.Define(RequireString(arguments, "localId"), version),
            "register" => extensions.Register(RequireString(arguments, "pointId"), version,
                RequireString(arguments, "localId"), arguments.GetProperty("value"),
                arguments.TryGetProperty("order", out var order) ? order.GetInt32() : 0),
            "observe" => extensions.Observe(RequireString(arguments, "pointId"), version,
                snapshot => _ = DeliverExtensionSnapshotAsync(plugin, handleId, snapshot)),
            _ => throw new InvalidOperationException("Unknown extension method.")
        };
        if (!_extensionHandles.TryAdd(key, handle))
        {
            handle.Dispose();
            throw new InvalidOperationException("An extension handle with this ID already exists.");
        }

        return Serialize(null);
    }

    private async Task DeliverExtensionSnapshotAsync(LoadedPlugin plugin, string handleId, QuantumExtensionSnapshot snapshot)
    {
        if (_disposed || catalog.FindPlugin(plugin.Manifest.Id)?.RuntimeId != plugin.RuntimeId)
        {
            return;
        }

        try
        {
            await javaScript.InvokeVoidAsync("quantum.plugins.deliverExtensionSnapshot",
                plugin.Manifest.Id, plugin.RuntimeId.ToString("N"), handleId, snapshot).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogDebug(exception, "Could not deliver extension snapshot to {PluginId}.", plugin.Manifest.Id);
        }
    }

    private JsonElement InvokeMobile(LoadedPlugin plugin, string method, JsonElement arguments)
    {
        switch (method)
        {
            case "publish": plugin.MobilePages.Publish(Quantum.Mobile.Internal.MobileJson.Read<Quantum.Plugin.Abstraction.MobilePage>(arguments.GetProperty("page"))); break;
            case "remove": plugin.MobilePages.Remove(RequireString(arguments, "pageId")); break;
            default: throw new InvalidOperationException("Unknown mobile method.");
        }
        return Serialize(null);
    }

    private JsonElement InvokeLog(string pluginId, string method, JsonElement arguments)
    {
        var message = RequireString(arguments, "message");
        var level = method.ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "information" or "info" => LogLevel.Information,
            "warning" or "warn" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => throw new InvalidOperationException($"Unknown log level '{method}'.")
        };
        logger.Log(level, "Web plugin {PluginId}: {PluginMessage}", pluginId, message);
        return Serialize(null);
    }

    private JsonElement InvokeEnvironment(LoadedPlugin plugin, string method)
    {
        if (!string.Equals(method, "snapshot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown environment method '{method}'.");
        }

        var integrations = plugin.Manifest.Integrations.Select(integration =>
        {
            var target = catalog.FindPlugin((string)integration.Id);
            return new
            {
                PluginId = (string)integration.Id,
                VersionRange = integration.VersionRange.ToString(),
                Active = target is not null
                    && VersionRange.Contains(integration.VersionRange, target.Manifest.Version)
            };
        });
        return Serialize(new
        {
            Plugin = new
            {
                Id = (string)plugin.Manifest.Id,
                Version = plugin.Manifest.Version.ToString(),
                RuntimeId = plugin.RuntimeId.ToString("N")
            },
            catalog.LoadedPlugins,
            Integrations = integrations
        });
    }

    private JsonElement InvokeNavigation(string method, JsonElement arguments)
    {
        if (!string.Equals(method, "navigate", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown navigation method '{method}'.");
        }

        var href = RequireString(arguments, "href");
        var isRootRelative = href.StartsWith("/", StringComparison.Ordinal)
            && !href.StartsWith("//", StringComparison.Ordinal);
        if (!string.Equals(href, href.Trim(), StringComparison.Ordinal)
            || href.Contains('\\', StringComparison.Ordinal)
            || href.StartsWith("//", StringComparison.Ordinal)
            || (!isRootRelative && Uri.TryCreate(href, UriKind.Absolute, out _)))
        {
            throw new InvalidOperationException("Web plugins can only navigate to host-relative URLs.");
        }

        navigation.NavigateTo(href);
        return Serialize(null);
    }

    private async Task<JsonElement> InvokeNotifyAsync(
        LoadedPlugin plugin,
        string runtimeId,
        string method,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var runtime = GetOrCreateEventBusRuntime(plugin, runtimeId);
        if (string.Equals(method, "publish", StringComparison.Ordinal))
        {
            var name = RequireString(arguments, "capabilityName");
            var payload = RequireProperty(arguments, "payload");
            await runtime.Bus.PublishAsync(name, payload, cancellationToken).ConfigureAwait(false);

            return Serialize(null);
        }

        var subscriptionId = RequireString(arguments, "subscriptionId");
        if (string.Equals(method, "subscribe", StringComparison.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = RequireString(arguments, "capabilityName");
            var pluginId = (string)plugin.Manifest.Id;
            var order = 0;
            if (arguments.TryGetProperty("order", out var orderValue)
                && (orderValue.ValueKind != JsonValueKind.Number || !orderValue.TryGetInt32(out order)))
            {
                throw new ArgumentException("Expected an integer notification observer order.");
            }

            var subscription = runtime.Bus.Observe(name, async (message, token) =>
                {
                    if (!IsCurrentRuntime(pluginId, runtimeId))
                    {
                        return QuantumPropagation.Continue;
                    }

                    var disposition = await javaScript.InvokeAsync<string?>("quantum.plugins.dispatchEvent", token,
                        pluginId, runtimeId, subscriptionId, message).ConfigureAwait(false);
                    return disposition == "stop" ? QuantumPropagation.Stop : QuantumPropagation.Continue;
                }, order);
            if (!runtime.TryAdd(subscriptionId, subscription))
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Subscription '{subscriptionId}' already exists.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await runtime.RemoveAsync(subscriptionId).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Serialize(null);
        }

        if (string.Equals(method, "unsubscribe", StringComparison.Ordinal))
        {
            await runtime.RemoveAsync(subscriptionId).ConfigureAwait(false);
            return Serialize(null);
        }

        throw new InvalidOperationException($"Unknown Notify method '{method}'.");
    }

    private WebEventBusRuntime GetOrCreateEventBusRuntime(LoadedPlugin plugin, string runtimeId)
    {
        var runtime = _eventBuses.GetOrAdd(
            CreateRuntimeKey((string)plugin.Manifest.Id, runtimeId),
            _ => new Lazy<WebEventBusRuntime>(
                () => new WebEventBusRuntime(eventBusFactory.Create(
                    new QuantumPluginInfo(
                        plugin.Manifest.Id,
                        plugin.Manifest.Version),
                    plugin.Manifest.Capabilities.Notifications)),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return runtime.Value;
    }

    private bool IsCurrentRuntime(string pluginId, string runtimeId)
        => catalog.FindPlugin(pluginId) is { } plugin
            && string.Equals(plugin.RuntimeId.ToString("N"), runtimeId, StringComparison.Ordinal);

    private static async Task<JsonElement> InvokeAssetsAsync(
        LoadedPlugin plugin,
        string method,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(method, "readText", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown assets method '{method}'.");
        }

        var relativePath = RequireString(arguments, "path");
        var path = ResolvePluginAssetPath(plugin, relativePath);
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new FileNotFoundException($"Plugin asset '{relativePath}' was not found.", path);
        }

        if (information.Length > 2 * 1024 * 1024)
        {
            throw new InvalidOperationException("Text assets read through RPC cannot exceed 2 MiB.");
        }

        return Serialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private async Task<JsonElement> InvokePluginRequestAsync(
        LoadedPlugin plugin,
        string method,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(method, "request", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown plugin capability method '{method}'.");
        }

        if (arguments.ValueKind != JsonValueKind.Object
            || OptionalString(arguments, "capabilityName") is not { } capabilityName)
        {
            return Serialize(Result.Fail(
                PluginRpcErrors.InvalidName,
                "RPC argument 'capabilityName' is required."));
        }

        if (!arguments.TryGetProperty("payload", out var payload))
        {
            return Serialize(Result.Fail(
                PluginRpcErrors.InvalidPayload,
                "RPC argument 'payload' is required."));
        }

        var contextItems = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (arguments.TryGetProperty("context", out var context)
            && context.ValueKind != JsonValueKind.Null)
        {
            if (context.ValueKind != JsonValueKind.Object)
            {
                return Serialize(Result.Fail(
                    PluginRpcErrors.InvalidContext,
                    "RPC argument 'context' must be an object."));
            }
            foreach (var item in context.EnumerateObject())
            {
                if (PluginRpcInvoker.IsReservedContextKey(item.Name)
                    || !contextItems.TryAdd(item.Name, item.Value.Clone()))
                {
                    return Serialize(Result.Fail(
                        PluginRpcErrors.InvalidContext,
                        "RPC context keys must be unique and cannot use Quantum reserved keys."));
                }
            }
        }

        var callContext = new PluginRpcCallContext((string)plugin.Manifest.Id, plugin.RuntimeId.ToString("N"), contextItems);
        return Serialize(await rpcRouter.RequestAsync(capabilityName, payload.Clone(), callContext, cancellationToken)
            .ConfigureAwait(false));
    }

    private static string ResolvePluginAssetPath(LoadedPlugin plugin, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Plugin asset paths must be relative and use forward slashes.");
        }

        var root = Path.GetFullPath(Path.Combine(plugin.RootPath, "wwwroot"));
        var candidate = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Plugin asset path escapes the plugin wwwroot directory.");
        }

        return candidate;
    }

    private static string RequireString(JsonElement arguments, string propertyName)
        => OptionalString(arguments, propertyName)
            ?? throw new InvalidOperationException($"RPC argument '{propertyName}' is required.");

    private static string? OptionalString(JsonElement arguments, string propertyName)
        => arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static JsonElement RequireProperty(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidOperationException($"RPC argument '{propertyName}' is required.");
        }

        return value.Clone();
    }

    private static JsonElement Serialize(object? value)
        => JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), SerializerOptions);

    private static string CreateInvocationKey(string pluginId, string runtimeId, string requestId)
        => $"{pluginId}\n{runtimeId}\n{requestId}";

    private static string CreateRuntimeKey(string pluginId, string runtimeId)
        => $"{pluginId}\n{runtimeId}";

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class WebEventBusRuntime(QuantumPluginEventBusHandle bus)
        : IDisposable, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, IAsyncDisposable> _subscriptions = new();

        public QuantumPluginEventBusHandle Bus { get; } = bus;

        public bool TryAdd(string id, IAsyncDisposable subscription)
            => _subscriptions.TryAdd(id, subscription);

        public async ValueTask RemoveAsync(string id)
        {
            if (_subscriptions.TryRemove(id, out var subscription))
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Bus.Dispose();
            _subscriptions.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Bus.DisposeAsync().ConfigureAwait(false);
            _subscriptions.Clear();
        }
    }
}
