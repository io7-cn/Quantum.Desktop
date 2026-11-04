using Quantum.Plugin.Abstraction;

namespace Quantum.Plugins;

/// <summary>
/// Creates active, explicitly owned notifiers for non-.NET plugin runtimes.
/// </summary>
public sealed class QuantumPluginEventBusFactory
{
    private readonly PluginEventHub _hub;

    internal QuantumPluginEventBusFactory(PluginEventHub hub)
    {
        _hub = hub;
    }

    public QuantumPluginEventBusHandle Create(QuantumPluginInfo plugin,
        IReadOnlyDictionary<string, QuantumNotificationContract>? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var bus = new PluginEventBus(plugin, _hub, notifications);
        bus.Resume();
        return new QuantumPluginEventBusHandle(bus);
    }
}

/// <summary>
/// Owns an active notifier for one external plugin runtime generation.
/// </summary>
public sealed class QuantumPluginEventBusHandle : IQuantumNotifier, IDisposable, IAsyncDisposable
{
    private readonly PluginEventBus _bus;

    internal QuantumPluginEventBusHandle(PluginEventBus bus)
    {
        _bus = bus;
    }

    public ValueTask PublishAsync<TMessage>(string capabilityName, TMessage message, CancellationToken cancellationToken = default)
        => _bus.PublishAsync(capabilityName, message, cancellationToken);

    public IQuantumNotificationSubscription Subscribe(
        string capabilityName, Func<QuantumNotification, CancellationToken, Task> handler, int order = 0)
        => _bus.Subscribe(capabilityName, handler, order);

    public IQuantumNotificationSubscription Observe(string capabilityName,
        Func<QuantumNotification, CancellationToken, Task<QuantumPropagation>> handler, int order = 0)
        => _bus.Observe(capabilityName, handler, order);

    public void Dispose() => _bus.Dispose();

    public ValueTask DisposeAsync() => _bus.DisposeAsync();
}
