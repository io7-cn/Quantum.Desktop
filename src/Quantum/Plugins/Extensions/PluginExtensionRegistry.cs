using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Quantum.Plugins;

/// <summary>Publishes only scopes in the committed catalog; staged runtimes remain invisible.</summary>
internal sealed class PluginExtensionRegistry
{
    private readonly object _gate = new();
    private readonly PluginCatalog _catalog;
    private readonly ILogger _logger;
    private readonly HashSet<PluginExtensionScope> _scopes = [];
    private readonly List<Observation> _observations = [];
    private long _revision;

    public PluginExtensionRegistry(PluginCatalog catalog, ILogger logger)
    {
        _catalog = catalog;
        _logger = logger;
        catalog.Changed += (_, _) => Change(() => { });
    }

    public PluginExtensionScope CreateScope(string pluginId, Guid runtimeId)
    {
        return CreateScope(pluginId, runtimeId, isHost: false);
    }

    internal PluginExtensionScope CreateHostScope(string ownerId) => CreateScope(ownerId, Guid.Empty, isHost: true);

    private PluginExtensionScope CreateScope(string pluginId, Guid runtimeId, bool isHost)
    {
        var scope = new PluginExtensionScope(this, PluginId.Of(pluginId), runtimeId, isHost);
        lock (_gate)
        {
            if (!isHost && _scopes.Any(item => item.IsHost && item.PluginId == scope.PluginId))
            {
                throw new ArgumentException("The namespace is reserved for the host.", nameof(pluginId));
            }

            _scopes.Add(scope);
        }

        return scope;
    }

    public IDisposable Observe(string pointId, int version, Action<QuantumExtensionSnapshot> observer,
        PluginExtensionScope? owner = null)
    {
        QuantumExtensionContract.PointId(pointId);
        QuantumExtensionContract.Version(version);
        ArgumentNullException.ThrowIfNull(observer);
        var observation = new Observation(pointId, version, observer, owner, _logger);
        lock (_gate)
        {
            owner?.ThrowIfDisposed();
            if (owner is not null && _observations.Count(item => item.Owner == owner) >= 128)
            {
                throw new InvalidOperationException("A runtime may have at most 128 extension observations.");
            }

            _observations.Add(observation);
            observation.Enqueue(Snapshot(pointId, version));
        }

        observation.Drain();
        return new Registration(() =>
        {
            lock (_gate)
            {
                _observations.Remove(observation);
                observation.Stop();
            }
        });
    }

    internal void Change(Action mutation)
    {
        Observation[] observers;
        lock (_gate)
        {
            mutation();
            _revision++;
            observers = _observations.ToArray();
            foreach (var observer in observers)
            {
                if (observer.Owner is null || observer.Owner.IsHost
                    || _catalog.FindPlugin(observer.Owner.PluginId)?.RuntimeId == observer.Owner.RuntimeId)
                {
                    observer.Enqueue(Snapshot(observer.PointId, observer.Version));
                }
            }
        }

        foreach (var observer in observers)
        {
            observer.Drain();
        }
    }

    internal void Clear(PluginExtensionScope scope, bool dispose)
    {
        Change(() =>
        {
            foreach (var observation in _observations.Where(item => item.Owner == scope).ToArray())
            {
                observation.Stop();
                _observations.Remove(observation);
            }

            scope.Definitions.Clear();
            scope.Contributions.Clear();
            if (dispose)
            {
                scope.IsDisposed = true;
                _scopes.Remove(scope);
            }
        });
    }

    private QuantumExtensionSnapshot Snapshot(string pointId, int version)
    {
        var published = _catalog.Plugins.ToDictionary(plugin => plugin.Manifest.Id, plugin => plugin.RuntimeId, StringComparer.Ordinal);
        var active = _scopes.Where(scope => !scope.IsDisposed
            && (scope.IsHost || published.TryGetValue(scope.PluginId, out var runtimeId) && runtimeId == scope.RuntimeId)).ToArray();
        var available = active.Any(scope =>
            scope.Definitions.Values.Any(point => point.PointId == pointId && point.Version == version));
        var contributions = available
            ? active.SelectMany(scope => scope.Contributions.Values
                .Where(item => item.PointId == pointId && item.Version == version)
                .Select(item => new QuantumExtensionContribution(scope.PluginId, item.LocalId, item.Order, item.Value)))
                .OrderBy(item => item.Order).ThenBy(item => item.PluginId, StringComparer.Ordinal)
                .ThenBy(item => item.LocalId, StringComparer.Ordinal).ToArray()
            : [];
        return new QuantumExtensionSnapshot(pointId, version, _revision, available,
            Array.AsReadOnly(contributions));
    }

    private sealed class Observation(string pointId, int version, Action<QuantumExtensionSnapshot> observer,
        PluginExtensionScope? owner, ILogger logger)
    {
        private readonly object _gate = new();
        private readonly Queue<QuantumExtensionSnapshot> _pending = new();
        private Action<QuantumExtensionSnapshot>? _observer = observer;
        private bool _draining;
        public string PointId { get; } = pointId;
        public int Version { get; } = version;
        public PluginExtensionScope? Owner { get; } = owner;

        public void Enqueue(QuantumExtensionSnapshot snapshot)
        {
            lock (_gate)
            {
                if (_observer is not null)
                {
                    if (_pending.Count >= 32)
                    {
                        // Snapshots are replaceable state, not a durable event stream.
                        _pending.Clear();
                    }

                    _pending.Enqueue(snapshot);
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _observer = null;
                _pending.Clear();
            }
        }

        public void Drain()
        {
            lock (_gate)
            {
                if (_draining)
                {
                    return;
                }

                _draining = true;
            }

            while (true)
            {
                Action<QuantumExtensionSnapshot>? callback;
                QuantumExtensionSnapshot snapshot;
                lock (_gate)
                {
                    if (!_pending.TryDequeue(out snapshot!))
                    {
                        _draining = false;
                        return;
                    }

                    callback = _observer;
                }

                try
                {
                    callback?.Invoke(snapshot);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Extension observer failed for {PointId}.", PointId);
                }
            }
        }
    }

    internal sealed class Registration(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
