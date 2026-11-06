using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quantum.Plugin.Abstraction;
using Quantum.Plugins;
using System.Text.Json;

namespace Quantum.HostIntegration.Tests.Plugins;

public sealed class CalendarHostTests
{
    [Fact]
    public async Task CalendarRequestsUseBareIdsAndPublishCommittedChangesToAllObservers()
    {
        using var fixture = new RuntimeFixture();
        var root = Path.Combine(fixture.ModulesRoot, "quantum.plugin.calendar");
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "PluginFixtures", "Calendar")))
        {
            var destination = Path.GetExtension(file) switch
            {
                ".sql" => Path.Combine(root, "migrations"),
                ".ico" => Path.Combine(root, "wwwroot", "icons"),
                _ => root
            };
            Directory.CreateDirectory(destination);
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        await using var manager = fixture.CreateManager();
        await manager.InitializeAsync(fixture.HostServices);
        Assert.Empty(fixture.Catalog.Failures);
        using var router = new PluginRpcRouter(fixture.Catalog);
        var context = new PluginRpcCallContext("test.caller", "", new Dictionary<string, JsonElement>());
        var factory = fixture.HostServices.GetRequiredService<QuantumPluginEventBusFactory>();
        await using var first = factory.Create(new QuantumPluginInfo("test.observer-one", "1.0.0"));
        await using var second = factory.Create(new QuantumPluginInfo("test.observer-two", "1.0.0"));
        var deleted = new System.Collections.Concurrent.ConcurrentQueue<(string Observer, Guid Id, bool Missing)>();
        await using var firstObserver = first.Subscribe("calendar.task.deleted", (eventData, _) => observeAsync("one", eventData));
        await using var secondObserver = second.Subscribe("calendar.task.deleted", (eventData, _) => observeAsync("two", eventData));
        var changes = new List<string>();
        await using var createdObserver = first.Subscribe("calendar.task.created", (eventData, _) =>
        {
            changes.Add(eventData.CapabilityName);
            return Task.CompletedTask;
        });
        await using var updatedObserver = first.Subscribe("calendar.task.updated", (eventData, _) =>
        {
            changes.Add(eventData.CapabilityName);
            return Task.CompletedTask;
        });

        var item = new
        {
            title = "New item",
            notes = "",
            kind = "Task",
            date = "2026-11-04",
            startTime = "12:00:00",
            endTime = (string?)null,
            style = "green"
        };
        var created = await callAsync("calendar.task.create", item);
        var entry = Assert.Single(created.Outcomes);
        Assert.Equal(QuantumRequestStatus.Handled, entry.Status);
        var id = entry.Value!.Value.GetProperty("id").GetGuid();

        var update = await callAsync("calendar.task.update", new { id, entry = item });
        Assert.Equal(QuantumRequestStatus.Handled, Assert.Single(update.Outcomes).Status);
        Assert.Equal(["calendar.task.created", "calendar.task.updated"], changes);

        var deletion = await callAsync("calendar.task.delete", new { id });
        Assert.Equal(QuantumRequestStatus.Handled, Assert.Single(deletion.Outcomes).Status);
        Assert.Equal(["one", "two"], deleted.Select(observation => observation.Observer).Order(StringComparer.Ordinal));
        Assert.All(deleted, observation =>
        {
            Assert.Equal(id, observation.Id);
            Assert.True(observation.Missing);
        });

        var repeated = await callAsync("calendar.task.delete", new { id });
        Assert.Equal(QuantumRequestStatus.Pass, Assert.Single(repeated.Outcomes).Status);
        Assert.Equal(2, deleted.Count);

        async Task<QuantumRequestReport<JsonElement?>> callAsync(string name, object payload)
        {
            var result = await router.RequestAsync(name, JsonSerializer.SerializeToElement(payload), context, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Message);
            return result.Value!;
        }

        async Task observeAsync(string observer, QuantumNotification eventData)
        {
            var deletedId = eventData.Payload.GetProperty("id").GetGuid();
            var read = await callAsync("calendar.entry.get", new { id = deletedId });
            deleted.Enqueue((observer, deletedId, Assert.Single(read.Outcomes).Status == QuantumRequestStatus.Pass));
        }
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("quantum-official-");

        public string ModulesRoot => Path.Combine(_root.FullName, "Modules");
        private string ShadowRoot => Path.Combine(_root.FullName, "Shadow");
        public PluginCatalog Catalog { get; } = new([]);
        public ServiceProvider HostServices { get; } = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddQuantumPluginEventBus()
            .BuildServiceProvider();

        public PluginRuntimeManager CreateManager() => new(
            Catalog,
            new PluginRuntimeOptions(
                ModulesRoot,
                ShadowRoot,
                Path.Combine(_root.FullName, "quantum.db")));

        public void Dispose()
        {
            HostServices.Dispose();
            // Windows can keep a plugin dependency mapped until its load context is collected.
            if (PluginShadowCopy.TryDelete(ShadowRoot))
            {
                _root.Delete(recursive: true);
            }
        }
    }
}
