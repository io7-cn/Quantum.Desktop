using System.Text.Json;
using Quantum.Plugin.Abstraction;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class PluginExtensionRegistryTests
{
    private const string Owner = "example.owner";
    private const string Consumer = "example.consumer";
    private const string Point = "example.owner/actions";

    [Fact]
    public void PendingContributionsBindOnlyToAnActiveMatchingDefinition()
    {
        var catalog = new PluginCatalog([]);
        var owner = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        var consumer = catalog.Extensions.CreateScope(Consumer, Guid.NewGuid());
        QuantumExtensionSnapshot? latest = null;
        using var observation = consumer.Observe(Point, 1, snapshot => latest = snapshot);
        using var contribution = consumer.Register(Point, 1, "action", Json("[1,true,null]"));
        using var definition = owner.Define("actions", 1);
        Assert.False(latest!.IsAvailable);
        catalog.Replace([Plugin(Consumer, consumer.RuntimeId)]);
        Assert.Empty(latest.Contributions);
        catalog.Replace([Plugin(Owner, owner.RuntimeId), Plugin(Consumer, consumer.RuntimeId)]);
        Assert.True(latest.IsAvailable);
        Assert.Equal("[1,true,null]", Assert.Single(latest.Contributions).Value.GetRawText());
        definition.Dispose();
        Assert.False(latest.IsAvailable);
        Assert.Empty(latest.Contributions);
    }

    [Fact]
    public void IncompatibleVersionsStayPendingAndOrderingIsDeterministic()
    {
        var catalog = new PluginCatalog([]);
        using var owner = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        using var consumer = catalog.Extensions.CreateScope(Consumer, Guid.NewGuid());
        catalog.Replace([Plugin(Owner, owner.RuntimeId), Plugin(Consumer, consumer.RuntimeId)]);
        using var definition = owner.Define("actions", 1);
        using var incompatible = consumer.Register(Point, 2, "wrong", Json("null"));
        using var second = consumer.Register(Point, 1, "z", Json("null"), 1);
        using var first = owner.Register(Point, 1, "a", Json("null"), 0);
        QuantumExtensionSnapshot? latest = null;
        using var observation = consumer.Observe(Point, 1, snapshot => latest = snapshot);
        Assert.Equal(new[] { "a", "z" }, latest!.Contributions.Select(item => item.LocalId));
    }

    [Fact]
    public void ReentrantObserversReceiveOrderedSnapshotsAndFailuresAreIsolated()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        catalog.Replace([Plugin(Owner, scope.RuntimeId)]);
        using var definition = scope.Define("actions", 1);
        using var contribution = scope.Register(Point, 1, "a", Json("1"));
        using var failing = scope.Observe(Point, 1, _ => throw new InvalidOperationException("observer"));
        var values = new List<int>();
        var revisions = new List<long>();
        using var observation = scope.Observe(Point, 1, snapshot =>
        {
            values.Add(snapshot.Contributions[0].Value.GetInt32());
            revisions.Add(snapshot.Revision);
            if (values.Count == 1)
            {
                contribution.Update(Json("2"));
            }
        });
        Assert.Equal(new[] { 1, 2 }, values);
        Assert.True(revisions[1] > revisions[0]);
    }

    [Fact]
    public void OldHandlesCannotRemoveOrUpdateRegistrationsAfterRestart()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        catalog.Replace([Plugin(Owner, scope.RuntimeId)]);
        var oldDefinition = scope.Define("actions", 1);
        var oldContribution = scope.Register(Point, 1, "a", Json("1"));
        scope.Clear();
        using var definition = scope.Define("actions", 1);
        using var contribution = scope.Register(Point, 1, "a", Json("2"));
        oldDefinition.Dispose();
        oldContribution.Dispose();
        Assert.Throws<ObjectDisposedException>(() => oldContribution.Update(Json("3")));
        QuantumExtensionSnapshot? latest = null;
        using var observation = scope.Observe(Point, 1, snapshot => latest = snapshot);
        Assert.True(latest!.IsAvailable);
        Assert.Equal(2, Assert.Single(latest.Contributions).Value.GetInt32());
    }

    [Fact]
    public void CatalogReplacementHidesOldGenerationAndCanRestoreItOnRollback()
    {
        var catalog = new PluginCatalog([]);
        using var oldScope = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        using var candidate = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        using var oldDefinition = oldScope.Define("actions", 1);
        using var definition = candidate.Define("actions", 1);
        using var oldValue = oldScope.Register(Point, 1, "a", Json("1"));
        using var newValue = candidate.Register(Point, 1, "a", Json("2"));
        QuantumExtensionSnapshot? latest = null;
        using var observation = catalog.Extensions.Observe(Point, 1, snapshot => latest = snapshot);
        catalog.Replace([Plugin(Owner, oldScope.RuntimeId)]);
        Assert.Equal(1, Assert.Single(latest!.Contributions).Value.GetInt32());
        catalog.Replace([Plugin(Owner, candidate.RuntimeId)]);
        Assert.Equal(2, Assert.Single(latest.Contributions).Value.GetInt32());
        catalog.Replace([Plugin(Owner, oldScope.RuntimeId)]);
        Assert.Equal(1, Assert.Single(latest.Contributions).Value.GetInt32());
    }

    [Fact]
    public void PayloadsOutliveCallerDocumentsAndDisposedScopesRejectMutations()
    {
        var catalog = new PluginCatalog([]);
        var scope = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        catalog.Replace([Plugin(Owner, scope.RuntimeId)]);
        using var definition = scope.Define("actions", 1);
        using (var document = JsonDocument.Parse("{\"key\":true}"))
        {
            scope.Register(Point, 1, "a", document.RootElement);
        }

        QuantumExtensionSnapshot? latest = null;
        using var observation = catalog.Extensions.Observe(Point, 1, snapshot => latest = snapshot);
        Assert.True(Assert.Single(latest!.Contributions).Value.GetProperty("key").GetBoolean());
        scope.Dispose();
        Assert.Empty(latest.Contributions);
        Assert.Throws<ObjectDisposedException>(() => scope.Define("other", 1));
        Assert.Throws<ObjectDisposedException>(() => scope.Observe(Point, 1, _ => { }));
    }

    [Fact]
    public void DuplicateIdsAndHostNamespaceImpersonationAreRejected()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope(Owner, Guid.NewGuid());
        using var definition = scope.Define("actions", 1);
        Assert.Throws<InvalidOperationException>(() => scope.Define("actions", 1));
        using var contribution = scope.Register(Point, 1, "a", Json("null"));
        Assert.Throws<InvalidOperationException>(() => scope.Register(Point, 1, "a", Json("null")));
        Assert.Throws<ArgumentException>(() => catalog.Extensions.CreateScope("quantum.settings", Guid.NewGuid()));
    }

    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static LoadedPlugin Plugin(string id, Guid runtimeId) => new(
        new PluginManifest(id, "1.0.0", PluginRuntimeDefinition.Web("main.js")), ".", null, [], runtimeId);
}
