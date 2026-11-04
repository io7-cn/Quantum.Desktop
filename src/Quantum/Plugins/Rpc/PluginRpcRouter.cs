using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NOF.Contract;
using Quantum.Plugin.Abstraction;

namespace Quantum.Plugins;

public sealed class PluginRpcRouter : IDisposable
{
    private readonly PluginCatalog _catalog;
    private readonly IServiceProvider? _services;
    private PluginRpcRegistry _registry;
    private int _disposed;

    public PluginRpcRouter(
        PluginCatalog catalog,
        IServiceProvider? services = null)
    {
        _catalog = catalog;
        _services = services;
        _registry = CreateRegistry();
        _catalog.Changed += HandleCatalogChanged;
    }

    internal Task<Result<QuantumRequestReport<JsonElement?>>> RequestAsync(
        string name, JsonElement payload, PluginRpcCallContext context, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Volatile.Read(ref _registry).RequestAsync(name, payload, context, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _catalog.Changed -= HandleCatalogChanged;
            Volatile.Write(ref _registry, PluginRpcRegistry.Empty);
        }
    }

    internal Guid ResolveMobileTarget(string name, Guid? pageOwner = null)
        => Volatile.Read(ref _registry).ResolveMobileTarget(name, pageOwner);
    internal Task<PluginRpcDispatchResult> InvokeMobileAsync(string name, Guid runtimeId, JsonElement payload,
        PluginRpcCallContext context, CancellationToken cancellationToken)
        => Volatile.Read(ref _registry).InvokeMobileAsync(name, runtimeId, payload, context, cancellationToken);

    private void HandleCatalogChanged(object? sender, EventArgs arguments)
        => Volatile.Write(ref _registry, CreateRegistry());

    private PluginRpcRegistry CreateRegistry()
        => CreateRegistry(_catalog.Plugins, _services);

    internal static PluginRpcRegistry CreateRegistry(IEnumerable<LoadedPlugin> loadedPlugins, IServiceProvider? services)
    {
        var plugins = loadedPlugins.ToArray();
        return PluginRpcRegistry.Create(
            plugins.Select(plugin => plugin.RpcRuntime).OfType<PluginRpcRuntime>(),
            () => services?.GetService<PluginInstallationRpc>() is { } installation ? installation.InvokeAsync : null,
            plugins.SelectMany(plugin => plugin.Manifest.Capabilities.Notifications.Select(notification =>
                new PluginNotificationProvider(notification.Key, (string)plugin.Manifest.Id, notification.Value,
                    () => plugin.RpcRuntime?.IsActive ?? plugin.Manifest.Runtime.Kind == PluginRuntimeKind.Web))).ToArray());
    }
}
