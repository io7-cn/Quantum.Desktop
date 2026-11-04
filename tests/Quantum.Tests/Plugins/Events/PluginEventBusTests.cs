using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class PluginEventBusTests
{
    [Theory]
    [InlineData(QuantumDispatch.All, 3)]
    [InlineData(QuantumDispatch.Ordered, 2)]
    public async Task OnlyAuthorizedOrderedObserversCanStopPropagation(QuantumDispatch dispatch, int count)
    {
        const string name = "test.changed";
        await using var host = CreateHost();
        var factory = host.GetRequiredService<QuantumPluginEventBusFactory>();
        await using var first = factory.Create(new QuantumPluginInfo("test.first", "1.0.0"));
        await using var authorized = factory.Create(new QuantumPluginInfo("test.authorized", "1.0.0"));
        await using var last = factory.Create(new QuantumPluginInfo("test.last", "1.0.0"));
        var contract = new QuantumNotificationContract(null,
            System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }))
        {
            Dispatch = dispatch,
            StopObservers = dispatch == QuantumDispatch.Ordered ? ["test.authorized"] : []
        };
        await using var publisher = factory.Create(new QuantumPluginInfo("test.publisher", "1.0.0"),
            new Dictionary<string, QuantumNotificationContract> { [name] = contract });
        var observed = new System.Collections.Concurrent.ConcurrentQueue<string>();

        await using var one = first.Observe(name, (_, _) => Stop("first"), order: -1);
        await using var two = authorized.Observe(name, (_, _) => Stop("authorized"));
        await using var three = last.Observe(name, (_, _) => Stop("last"), order: 1);
        await publisher.PublishAsync(name, new PublisherMessage("ready", 1));

        Assert.Equal(count, observed.Count);
        if (dispatch == QuantumDispatch.Ordered)
        {
            Assert.Equal(["first", "authorized"], observed);
        }

        Task<QuantumPropagation> Stop(string label)
        {
            observed.Enqueue(label);
            return Task.FromResult(QuantumPropagation.Stop);
        }
    }

    [Fact]
    public async Task PublishAsync_RoutesByCapabilityThroughNofAndMapsAcrossMessageTypes()
    {
        await using var host = CreateHost();
        var hub = host.GetRequiredService<PluginEventHub>();
        await using var publisherBus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.publisher", "1.0.0"),
            hub, Contracts("sensors.camera.status", "device.status"));
        await using var subscriberBus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.subscriber", "2.0.0"),
            hub);
        publisherBus.Resume();
        subscriberBus.Resume();

        QuantumNotification? received = null;
        SubscriberMessage? receivedMessage = null;
        await using var subscription = subscriberBus.Subscribe(
            "sensors.camera.status",
            (@event, _) =>
            {
                received = @event;
                receivedMessage = @event.DeserializeRequired<SubscriberMessage>();
                return Task.CompletedTask;
            });
        await publisherBus.PublishAsync("sensors.camera.status", new PublisherMessage("ready", 7));

        Assert.Equal("sensors.camera.status", subscription.CapabilityName);
        Assert.NotNull(received);
        Assert.NotNull(receivedMessage);
        Assert.NotEqual(Guid.Empty, received.Id);
        Assert.Equal("ready", receivedMessage.State);
        Assert.Equal(7, receivedMessage.Sequence);
        Assert.Equal("ready", received.Payload.GetProperty("state").GetString());
        Assert.Equal("quantum.plugin.publisher", (string)received.Publisher.Id);
        Assert.Equal("1.0.0", received.Publisher.Version.ToString());
        Assert.InRange(
            received.PublishedAt,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task PublishAsync_DeliversOnlyToActiveSubscriptionsOnTheSameCapability()
    {
        await using var host = CreateHost();
        var hub = host.GetRequiredService<PluginEventHub>();
        await using var bus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.example", "1.0.0"),
            hub, Contracts("device.status", "device.status.changed"));
        bus.Resume();
        var matchingCalls = 0;
        var otherCalls = 0;
        var matching = bus.Subscribe(
            "device.status",
            (_, _) =>
            {
                matchingCalls++;
                return Task.CompletedTask;
            });
        await using var other = bus.Subscribe(
            "device.other",
            (_, _) =>
            {
                otherCalls++;
                return Task.CompletedTask;
            });

        await bus.PublishAsync("device.status", new PublisherMessage("first", 1));
        matching.Dispose();
        await bus.PublishAsync("device.status", new PublisherMessage("second", 2));

        Assert.Equal(1, matchingCalls);
        Assert.Equal(0, otherCalls);
    }

    [Fact]
    public async Task Pause_SuspendsDeliveryAndPublishingUntilResume()
    {
        await using var host = CreateHost();
        var hub = host.GetRequiredService<PluginEventHub>();
        await using var publisherBus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.publisher", "1.0.0"),
            hub, Contracts("sensors.camera.status", "device.status"));
        await using var subscriberBus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.subscriber", "1.0.0"),
            hub);
        publisherBus.Resume();
        subscriberBus.Resume();
        var calls = 0;
        await using var subscription = subscriberBus.Subscribe(
            "device.status",
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });

        subscriberBus.Pause();
        await publisherBus.PublishAsync("device.status", new PublisherMessage("ignored", 1));
        subscriberBus.Resume();
        await publisherBus.PublishAsync("device.status", new PublisherMessage("received", 2));
        publisherBus.Pause();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await publisherBus.PublishAsync("device.status", new PublisherMessage("rejected", 3)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PublishAsync_ContinuesOtherSubscribersWhenAnObserverFails()
    {
        await using var host = CreateHost();
        var hub = host.GetRequiredService<PluginEventHub>();
        await using var bus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.example", "1.0.0"),
            hub, Contracts("device.status", "device.status.changed"));
        bus.Resume();
        await using var failing = bus.Subscribe(
            "device.status",
            (_, _) => throw new InvalidOperationException("subscriber failed"));
        var successfulCalls = 0;
        await using var successful = bus.Subscribe(
            "device.status",
            (_, _) =>
            {
                successfulCalls++;
                return Task.CompletedTask;
            });

        await bus.PublishAsync("device.status", new PublisherMessage("ready", 1));

        Assert.Equal(1, successfulCalls);
    }

    [Fact]
    public async Task Subscribe_ExposesPayloadAndConvenienceDeserialization()
    {
        await using var host = CreateHost();
        var hub = host.GetRequiredService<PluginEventHub>();
        await using var bus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.example", "1.0.0"),
            hub, Contracts("device.status", "device.status.changed"));
        bus.Resume();
        QuantumNotification? received = null;
        await using var subscription = bus.Subscribe(
            "device.status.changed",
            (@event, _) =>
            {
                received = @event;
                return Task.CompletedTask;
            });

        await bus.PublishAsync("device.status.changed", new PublisherMessage("ready", 42));

        Assert.NotNull(received);
        Assert.Equal("ready", received.Payload.GetProperty("state").GetString());
        Assert.Equal(42, received.Payload.GetProperty("sequence").GetInt32());
        Assert.Equal(new SubscriberMessage("ready", 42),
            received.DeserializeRequired<SubscriberMessage>());
        Assert.Equal(new SubscriberMessage("ready", 42),
            received.Deserialize(typeof(SubscriberMessage)));
        Assert.True(received.TryDeserialize<SubscriberMessage>(out var deserialized));
        Assert.Equal(new SubscriberMessage("ready", 42), deserialized);
    }

    [Fact]
    public void AddQuantumPluginEventBus_IsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddQuantumPluginEventBus();
        services.AddQuantumPluginEventBus();

        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(PluginEventHub));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(PluginEventTransportHandler));
        Assert.Single(services, static descriptor =>
            descriptor.ServiceType == typeof(QuantumPluginEventBusFactory));
    }

    [Fact]
    public async Task ExternalRuntimeFactory_CreatesAnActiveOwnedEventBus()
    {
        await using var host = CreateHost();
        var factory = host.GetRequiredService<QuantumPluginEventBusFactory>();
        await using var bus = factory.Create(
            new QuantumPluginInfo("quantum.plugin.web", "1.0.0"), Contracts("web.status"));
        QuantumNotification? received = null;
        await using var subscription = bus.Subscribe(
            "web.status",
            (@event, _) =>
            {
                received = @event;
                return Task.CompletedTask;
            });

        await bus.PublishAsync("web.status", new PublisherMessage("ready", 1));

        Assert.NotNull(received);
        Assert.Equal("quantum.plugin.web", (string)received.Publisher.Id);
    }

    [Fact]
    public async Task PublishAndSubscribeRejectInvalidNamesAndPublicationRequiresADeclaration()
    {
        using var host = CreateHost();
        using var bus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.example", "1.0.0"),
            host.GetRequiredService<PluginEventHub>());

        bus.Resume();
        Assert.Throws<ArgumentException>(() => bus.Subscribe("invalid/topic", (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentException>(() => bus.PublishAsync("invalid/topic", new { }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.PublishAsync("undeclared.status", new { }).AsTask());
    }

    [Fact]
    public void Dispose_DoesNotKeepPublishedCollectibleMessageTypeAlive()
    {
        var references = PublishCollectibleMessage();

        for (var attempt = 0; attempt < 10 && references.LoadContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(references.Bus.IsAlive, "The disposed plugin event bus is still rooted.");
        Assert.False(references.LoadContext.IsAlive, "The collectible plugin load context is still rooted.");
    }

    private static IReadOnlyDictionary<string, QuantumNotificationContract> Contracts(params string[] names)
        => names.ToDictionary(name => name, _ => new QuantumNotificationContract(null,
            JsonSerializer.SerializeToElement(new { type = "object" })), StringComparer.Ordinal);

    private static ServiceProvider CreateHost()
        => new ServiceCollection()
            .AddQuantumPluginEventBus()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CollectibleReferences PublishCollectibleMessage()
    {
        using var host = CreateHost();
        var loadContext = new PluginLoadContext(
            Path.Combine(AppContext.BaseDirectory, "Quantum.ExamplePlugin.dll"));
        var loadContextReference = new WeakReference(loadContext, trackResurrection: false);
        var messageType = loadContext
            .LoadEntryAssembly()
            .GetType("Quantum.ExamplePlugin.ExamplePluginHandshake", throwOnError: true)!;
        var message = Activator.CreateInstance(
            messageType,
            "collectible",
            1,
            DateTimeOffset.UtcNow,
            true)!;
        var bus = new PluginEventBus(
            new QuantumPluginInfo("quantum.plugin.collectible", "1.0.0"),
            host.GetRequiredService<PluginEventHub>(), Contracts("collectible.message"));
        var busReference = new WeakReference(bus, trackResurrection: false);
        using (bus)
        {
            bus.Resume();
            using var subscription = bus.Subscribe(
                "collectible.message",
                (@event, _) =>
                {
                    Assert.NotNull(@event.Deserialize(messageType));
                    return Task.CompletedTask;
                });
            bus.PublishAsync("collectible.message", message)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        loadContext.Unload();
        return new CollectibleReferences(loadContextReference, busReference);
    }

    private sealed record PublisherMessage(string State, int Sequence);

    private sealed record SubscriberMessage(string State, int Sequence);

    private sealed record CollectibleReferences(WeakReference LoadContext, WeakReference Bus);
}
