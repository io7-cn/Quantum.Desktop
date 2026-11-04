using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quantum.Plugin.Abstraction;
using NOF.Abstraction;
using NOF.Contract;

namespace Quantum.Plugins;

internal sealed class PluginEventBus(
    QuantumPluginInfo plugin,
    PluginEventHub hub,
    IReadOnlyDictionary<string, QuantumNotificationContract>? notifications = null) : IQuantumNotifier, IDisposable, IAsyncDisposable
{
    private readonly JsonSerializerOptions _serializerOptions = QuantumJsonDefaults.CreatePluginOptions();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, IDisposable> _subscriptions = [];
    private int _state;
    private bool _receiveNotifications;
    private int _notificationHandlers;
    private TaskCompletionSource? _notificationsDrained;

    public ValueTask PublishAsync<TMessage>(
        string capabilityName, TMessage message, CancellationToken cancellationToken = default)
    {
        ValidateNotificationName(capabilityName);
        if (notifications is null || !notifications.TryGetValue(capabilityName, out var contract))
        {
            throw new InvalidOperationException($"Plugin '{plugin.Id}' does not provide notification '{capabilityName}'.");
        }

        return PublishAsync(capabilityName, message, contract, cancellationToken);
    }

    public IQuantumNotificationSubscription Subscribe(
        string capabilityName, Func<QuantumNotification, CancellationToken, Task> handler, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Observe(capabilityName, async (message, token) =>
        {
            await handler(message, token).ConfigureAwait(false);
            return QuantumPropagation.Continue;
        }, order);
    }

    public IQuantumNotificationSubscription Observe(
        string capabilityName, Func<QuantumNotification, CancellationToken, Task<QuantumPropagation>> handler, int order = 0)
    {
        ValidateNotificationName(capabilityName);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            ThrowIfDisposed();
            var id = Guid.NewGuid();
            var subscription = hub.Subscribe(capabilityName, HandleNotificationAsync, plugin.Id, order);
            _subscriptions.Add(id, subscription);
            return new PluginNotificationSubscription(this, id, capabilityName);
        }

        async Task<QuantumPropagation> HandleNotificationAsync(PluginEventTransportMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!_receiveNotifications)
                {
                    return QuantumPropagation.Continue;
                }

                _notificationHandlers++;
            }

            try
            {
                var notification = new QuantumNotification(
                    message.Id, message.CapabilityName, message.Payload, message.Publisher, message.PublishedAt);
                return await handler(notification, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    if (--_notificationHandlers == 0)
                    {
                        _notificationsDrained?.TrySetResult();
                        _notificationsDrained = null;
                    }
                }
            }
        }
    }

    internal Task PauseNotificationsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _receiveNotifications = false;
            if (_notificationHandlers == 0)
            {
                return Task.CompletedTask;
            }

            _notificationsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _notificationsDrained.Task.WaitAsync(cancellationToken);
        }
    }

    private static void ValidateNotificationName(string name)
    {
        if (!CapabilityName.IsPluginCapability(name))
        {
            throw new ArgumentException("Expected a notification capability name.", nameof(name));
        }
    }

    internal void Resume(bool receiveNotifications = true)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Volatile.Write(ref _state, 1);
            _receiveNotifications = receiveNotifications;
        }
    }

    internal void Pause()
    {
        lock (_gate)
        {
            if (_state != 2)
            {
                Volatile.Write(ref _state, 0);
                _receiveNotifications = false;
            }
        }
    }

    public void Dispose()
    {
        IDisposable[] subscriptions;
        lock (_gate)
        {
            if (_state == 2)
            {
                return;
            }

            Volatile.Write(ref _state, 2);
            _receiveNotifications = false;
            subscriptions = _subscriptions.Values.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }

        QuantumJsonDefaults.ClearPluginCaches(_serializerOptions);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await PauseNotificationsAsync(CancellationToken.None).ConfigureAwait(false);
        Dispose();
    }

    private async ValueTask PublishAsync<TMessage>(
        string capabilityName,
        TMessage message,
        QuantumNotificationContract contract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.SerializeToElement(
            message,
            message.GetType(),
            _serializerOptions);
        if (payload.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException("A notification payload cannot be null.", nameof(message));
        }

        await hub.PublishAsync(
                new PluginEventTransportMessage(
                    Guid.NewGuid(),
                    capabilityName,
                    payload,
                    plugin,
                    DateTimeOffset.UtcNow, contract.Dispatch, contract.StopObservers),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void RemoveSubscription(Guid id)
    {
        IDisposable? subscription;
        lock (_gate)
        {
            _subscriptions.Remove(id, out subscription);
        }

        subscription?.Dispose();
    }

    private void EnsureActive()
    {
        var state = Volatile.Read(ref _state);
        ObjectDisposedException.ThrowIf(state == 2, this);
        if (state != 1)
        {
            throw new InvalidOperationException(
                $"The notifier for plugin '{plugin.Id}' is not active.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == 2, this);

    private sealed class PluginNotificationSubscription(
        PluginEventBus owner, Guid id, string capabilityName) : IQuantumNotificationSubscription
    {
        public string CapabilityName { get; } = capabilityName;

        public void Dispose() => owner.RemoveSubscription(id);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class PluginEventHub(IServiceScopeFactory scopeFactory, ILogger<PluginEventHub>? logger = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<Guid, PluginEventSink>> _subscriptions = new(StringComparer.Ordinal);

    public IDisposable Subscribe(
        string capabilityName,
        Func<PluginEventTransportMessage, CancellationToken, Task<QuantumPropagation>> handler, string pluginId, int order)
    {
        var id = Guid.NewGuid();
        var sink = new PluginEventSink(() => Remove(capabilityName, id), handler, pluginId, order);
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(capabilityName, out var subscriptions))
            {
                subscriptions = [];
                _subscriptions.Add(capabilityName, subscriptions);
            }

            subscriptions.Add(id, sink);
        }

        return sink;
    }

    public async ValueTask PublishAsync(
        PluginEventTransportMessage message,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.ResolveDaemonServices();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        await publisher.PublishAsync(message, Context.Empty, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DispatchAsync(
        PluginEventTransportMessage message,
        CancellationToken cancellationToken)
    {
        PluginEventSink[] subscribers;
        lock (_gate)
        {
            subscribers = _subscriptions.TryGetValue(message.CapabilityName, out var current) ? current.Values.ToArray() : [];
        }

        if (message.Dispatch == QuantumDispatch.All)
        {
            await Task.WhenAll(subscribers.Select(DeliverAsync)).ConfigureAwait(false);
            return;
        }

        foreach (var subscriber in subscribers.OrderBy(item => item.Order).ThenBy(item => item.PluginId, StringComparer.Ordinal))
        {
            var disposition = await DeliverAsync(subscriber).ConfigureAwait(false);
            if (disposition == QuantumPropagation.Stop && message.StopObservers.Contains(subscriber.PluginId, StringComparer.Ordinal))
            {
                return;
            }
        }

        async Task<QuantumPropagation> DeliverAsync(PluginEventSink sink)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await sink.InvokeAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogWarning(exception, "A subscriber failed handling notification {CapabilityName}.", message.CapabilityName);
                return QuantumPropagation.Continue;
            }
        }
    }

    private void Remove(string capabilityName, Guid id)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(capabilityName, out var subscriptions))
            {
                subscriptions.Remove(id);
                if (subscriptions.Count == 0)
                {
                    _subscriptions.Remove(capabilityName);
                }
            }
        }
    }

    private sealed class PluginEventSink(
        Action remove,
        Func<PluginEventTransportMessage, CancellationToken, Task<QuantumPropagation>> handler, string pluginId, int order) : IDisposable
    {
        private int _disposed;

        public string PluginId { get; } = pluginId;

        public int Order { get; } = order;

        public Task<QuantumPropagation> InvokeAsync(
            PluginEventTransportMessage message,
            CancellationToken cancellationToken)
            => Volatile.Read(ref _disposed) == 0
                ? handler(message, cancellationToken)
                : Task.FromResult(QuantumPropagation.Continue);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                remove();
            }
        }
    }
}

internal sealed class PluginEventTransportHandler(PluginEventHub hub)
    : InMemoryEventHandler<PluginEventTransportMessage>
{
    public override Task HandleAsync(
        PluginEventTransportMessage @event,
        Context context,
        CancellationToken cancellationToken)
        => hub.DispatchAsync(@event, cancellationToken);
}

internal sealed record PluginEventTransportMessage(
    Guid Id,
    string CapabilityName,
    JsonElement Payload,
    QuantumPluginInfo Publisher,
    DateTimeOffset PublishedAt,
    QuantumDispatch Dispatch,
    IReadOnlyList<string> StopObservers);
