using System.Text.Json;

namespace Quantum.Plugins;

internal sealed class PluginExtensionScope(PluginExtensionRegistry registry, string pluginId, Guid runtimeId, bool isHost = false)
    : IQuantumExtensions, IDisposable
{
    internal string PluginId { get; } = pluginId;
    internal Guid RuntimeId { get; } = runtimeId;
    internal bool IsHost { get; } = isHost;
    internal bool IsDisposed { get; set; }
    internal Dictionary<string, Point> Definitions { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Contribution> Contributions { get; } = new(StringComparer.Ordinal);

    public IDisposable Define(string localId, int contractVersion)
    {
        QuantumExtensionContract.LocalId(localId);
        QuantumExtensionContract.Version(contractVersion);
        var point = new Point($"{PluginId}/{localId}", contractVersion);
        registry.Change(() =>
        {
            ThrowIfDisposed();
            CheckCapacity();
            if (!Definitions.TryAdd(localId, point))
            {
                throw new InvalidOperationException("An extension point with this local ID is already defined.");
            }
        });
        return new PluginExtensionRegistry.Registration(() => registry.Change(() =>
        {
            if (Definitions.TryGetValue(localId, out var current) && ReferenceEquals(current, point))
            {
                Definitions.Remove(localId);
            }
        }));
    }

    public IQuantumContribution Register(string pointId, int contractVersion, string localId, JsonElement value, int order = 0)
    {
        QuantumExtensionContract.PointId(pointId);
        QuantumExtensionContract.LocalId(localId);
        QuantumExtensionContract.Version(contractVersion);
        var contribution = new Contribution(pointId, contractVersion, localId, order,
            QuantumExtensionContract.Payload(value));
        registry.Change(() =>
        {
            ThrowIfDisposed();
            CheckCapacity();
            if (!Contributions.TryAdd(localId, contribution))
            {
                throw new InvalidOperationException("A contribution with this local ID is already registered.");
            }
        });
        return new ContributionHandle(this, registry, contribution);
    }

    public IDisposable Observe(string pointId, int contractVersion, Action<QuantumExtensionSnapshot> observer)
        => registry.Observe(pointId, contractVersion, observer, this);

    internal void Clear() => registry.Clear(this, dispose: false);
    public void Dispose() => registry.Clear(this, dispose: true);
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private void CheckCapacity()
    {
        if (Definitions.Count + Contributions.Count >= 256)
        {
            throw new InvalidOperationException("A runtime may have at most 256 extension registrations.");
        }
    }

    internal sealed record Point(string PointId, int Version);
    internal sealed class Contribution(string pointId, int version, string localId, int order, JsonElement value)
    {
        public string PointId { get; } = pointId;
        public int Version { get; } = version;
        public string LocalId { get; } = localId;
        public int Order { get; } = order;
        public JsonElement Value { get; set; } = value;
    }

    private sealed class ContributionHandle(PluginExtensionScope owner, PluginExtensionRegistry registry,
        Contribution contribution) : IQuantumContribution
    {
        private bool _disposed;
        public void Update(JsonElement value)
        {
            var detached = QuantumExtensionContract.Payload(value);
            registry.Change(() =>
            {
                owner.ThrowIfDisposed();
                ObjectDisposedException.ThrowIf(_disposed || !owner.Contributions.TryGetValue(contribution.LocalId, out var current)
                    || !ReferenceEquals(current, contribution), this);
                contribution.Value = detached;
            });
        }

        public void Dispose()
        {
            registry.Change(() =>
            {
                _disposed = true;
                if (owner.Contributions.TryGetValue(contribution.LocalId, out var current)
                    && ReferenceEquals(current, contribution))
                {
                    owner.Contributions.Remove(contribution.LocalId);
                }
            });
        }
    }
}
