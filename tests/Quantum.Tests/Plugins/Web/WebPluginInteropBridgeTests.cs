using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NOF.Application;
using NOF.Contract;
using Quantum.Plugins;
using Quantum.WebPlugins;
using System.Text.Json;

namespace Quantum.Tests;

public sealed class WebPluginInteropBridgeTests
{
    [Fact]
    public async Task ExtensionsBridgeRegistersUpdatesObservesAndReleasesRuntimeOwnedHandles()
    {
        await using var host = CreateHost();
        var catalog = new PluginCatalog([]);
        var runtimeId = Guid.NewGuid();
        var template = LoadedWebPlugin(runtimeId);
        var scope = catalog.Extensions.CreateScope(PluginId, runtimeId);
        var plugin = new LoadedPlugin(template.Manifest, template.RootPath, null, [], runtimeId)
        {
            Extensions = scope
        };
        catalog.Replace([plugin]);
        using var router = new PluginRpcRouter(catalog);
        var javaScript = new ExtensionDeliveryJavaScriptRuntime();
        using var bridge = CreateBridge(catalog, router, host, javaScript);
        await Call("define", new { handleId = "point", localId = "actions", contractVersion = 1 });
        await Call("register", new
        {
            handleId = "value",
            localId = "entry",
            pointId = PluginId + "/actions",
            contractVersion = 1,
            valueJson = "{\"label\":\"before\"}"
        });
        await Call("observe", new { handleId = "watch", pointId = PluginId + "/actions", contractVersion = 1 });
        Assert.Equal("before", Assert.Single(javaScript.Latest!.Contributions).Value.GetProperty("label").GetString());
        await Call("update", new { handleId = "value", valueJson = "{\"label\":\"after\"}" });
        Assert.Equal("after", Assert.Single(javaScript.Latest!.Contributions).Value.GetProperty("label").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.InvokeAsync(PluginId, Guid.NewGuid().ToString("N"),
            "stale", "extensions", "dispose", JsonSerializer.SerializeToElement(new { handleId = "value" })));
        await bridge.ReleaseRuntimeAsync(PluginId, runtimeId.ToString("N"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Call("update", new { handleId = "value", valueJson = "1" }));
        // The same runtime can activate a fresh frame after release.
        await Call("define", new { handleId = "point", localId = "actions", contractVersion = 1 });

        Task<JsonElement> Call(string method, object arguments) => bridge.InvokeAsync(PluginId, runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"), "extensions", method, JsonSerializer.SerializeToElement(arguments));
    }

    [Fact]
    public async Task MaximumDepthExtensionPayloadSurvivesBothInteropEnvelopes()
    {
        await using var host = CreateHost();
        var catalog = new PluginCatalog([]);
        var runtimeId = Guid.NewGuid();
        var template = LoadedWebPlugin(runtimeId);
        var scope = catalog.Extensions.CreateScope(PluginId, runtimeId);
        catalog.Replace([new LoadedPlugin(template.Manifest, template.RootPath, null, [], runtimeId) { Extensions = scope }]);
        using var router = new PluginRpcRouter(catalog);
        var javaScript = new ExtensionDeliveryJavaScriptRuntime();
        using var bridge = CreateBridge(catalog, router, host, javaScript);
        var json = new string('[', 64) + "0" + new string(']', 64);
        await Call("define", new { handleId = "point", localId = "actions", contractVersion = 1 });
        await Call("register", new
        {
            handleId = "value",
            localId = "entry",
            pointId = PluginId + "/actions",
            contractVersion = 1,
            valueJson = json
        });
        await Call("observe", new { handleId = "watch", pointId = PluginId + "/actions", contractVersion = 1 });
        Assert.Equal(json, Assert.Single(javaScript.Latest!.Contributions).Value.GetRawText());
        await Assert.ThrowsAnyAsync<JsonException>(() => Call("update", new { handleId = "value", valueJson = "[" + json + "]" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call("update", new { handleId = "value", valueJson = JsonSerializer.Serialize(new string('x', 131072)) }));
        Assert.Equal(json, Assert.Single(javaScript.Latest.Contributions).Value.GetRawText());

        Task<JsonElement> Call(string method, object arguments) => bridge.InvokeAsync(PluginId, runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"), "extensions", method, JsonSerializer.SerializeToElement(arguments));
    }

    private sealed class ExtensionDeliveryJavaScriptRuntime : IJSRuntime
    {
        public Quantum.Plugin.Abstraction.QuantumExtensionSnapshot? Latest { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("quantum.plugins.deliverExtensionSnapshot", identifier);
            // The real JS interop envelope must be shallow even for a maximum-depth payload.
            JsonSerializer.Serialize(args);
            Latest = JsonSerializer.Deserialize<Quantum.Plugin.Abstraction.QuantumExtensionSnapshot>(
                Assert.IsType<string>(args![3]), new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 128 });
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    [Fact]
    public async Task MobileDeclarationsAreOwnedByWebRuntimeAndReleasedWithIt()
    {
        await using var host = CreateHost();
        var runtimeId = Guid.NewGuid();
        var plugin = LoadedWebPlugin(runtimeId);
        var catalog = new PluginCatalog([plugin]);
        using var router = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(catalog, router, host, new RejectingEventDeliveryJavaScriptRuntime());
        var page = Quantum.Plugin.Abstraction.Mobile.Page("status", "Status", Quantum.Plugin.Abstraction.Mobile.Attachments("files"));
        await bridge.InvokeAsync(PluginId, runtimeId.ToString("N"), "publish", "mobile", "publish", JsonSerializer.SerializeToElement(new { page }, Quantum.Mobile.Internal.MobileJson.Options));
        Assert.Equal(2, Assert.Single(plugin.MobilePages.Snapshot()).SchemaVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.InvokeAsync(PluginId, Guid.NewGuid().ToString("N"), "stale", "mobile", "remove", JsonSerializer.SerializeToElement(new { pageId = "status" })));
        await bridge.ReleaseRuntimeAsync(PluginId, runtimeId.ToString("N"));
        Assert.Empty(plugin.MobilePages.Snapshot());
    }
    private const string PluginId = "quantum.plugin.web";
    private const string TargetPluginId = "quantum.plugin.target";
    private const string Topic = "example.web.status";

    [Fact]
    public async Task ReplacementPublishSkipsSubscribersFromStaleRuntime()
    {
        await using var host = CreateHost();
        var oldRuntimeId = Guid.NewGuid();
        var newRuntimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(oldRuntimeId, capabilities: NotificationCapabilities())]);
        var javaScript = new RejectingEventDeliveryJavaScriptRuntime();
        using var rpcRouter = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(catalog, rpcRouter, host, javaScript);
        await SubscribeAsync(bridge, oldRuntimeId);

        catalog.Replace([LoadedWebPlugin(newRuntimeId, capabilities: NotificationCapabilities())]);

        var result = await PublishAsync(bridge, newRuntimeId);

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        Assert.Equal(0, javaScript.DispatchCount);
    }

    [Fact]
    public async Task ActiveRuntimeObserverFailureDoesNotRejectPublication()
    {
        await using var host = CreateHost();
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(runtimeId, capabilities: NotificationCapabilities())]);
        var javaScript = new RejectingEventDeliveryJavaScriptRuntime();
        using var rpcRouter = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(catalog, rpcRouter, host, javaScript);
        await SubscribeAsync(bridge, runtimeId);

        await PublishAsync(bridge, runtimeId);

        Assert.Equal(1, javaScript.DispatchCount);
    }

    [Fact]
    public async Task RpcInvocationDoesNotRequireAnIntegrationDeclaration()
    {
        await using var host = CreateHost();
        await using var target = CreateRpcTarget(host);
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([
            LoadedWebPlugin(runtimeId),
            target.Plugin
        ]);
        using var rpcRouter = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(
            catalog,
            rpcRouter,
            host,
            new RejectingEventDeliveryJavaScriptRuntime());

        var result = await bridge.InvokeAsync(
            PluginId,
            runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"),
            "request", "request",
            JsonSerializer.SerializeToElement(new
            {
                capabilityName = "test.echo",
                payload = new { value = "hello" },
                context = new { trace = "web-test" }
            }));

        Assert.True(result.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(
            $"{PluginId}:echo:hello",
            SingleOutcome(result).GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("\"Expense\"", true)]
    [InlineData("0", false)]
    public async Task WebRpcUsesStringEnumsAndPreservesFieldDiagnostics(string kind, bool succeeds)
    {
        await using var host = CreateHost();
        await using var target = CreateRpcTarget(host);
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(runtimeId), target.Plugin]);
        using var router = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(catalog, router, host, new RejectingEventDeliveryJavaScriptRuntime());
        var payload = JsonSerializer.Deserialize<JsonElement>(
            "{\"date\":\"2026-10-19\",\"kind\":" + kind + ",\"amount\":0.01,\"category\":\"Test\"}");
        var result = await bridge.InvokeAsync(PluginId, runtimeId.ToString("N"), Guid.NewGuid().ToString("N"),
            "request", "request", JsonSerializer.SerializeToElement(new { capabilityName = "json-contract.create", payload }));
        Assert.True(result.GetProperty("isSuccess").GetBoolean());
        result = SingleOutcome(result);
        Assert.Equal(succeeds ? "handled" : "failed", result.GetProperty("status").GetString());
        if (succeeds)
        {
            Assert.Equal("Expense", result.GetProperty("value").GetProperty("kind").GetString());
            Assert.Equal("", result.GetProperty("value").GetProperty("note").GetString());
        }
        else
        {
            Assert.Equal("rpc_invalid_payload", result.GetProperty("errorCode").GetString());
            Assert.Equal("$.kind", result.GetProperty("extra").GetProperty("path").GetString());
            Assert.Contains("$.kind", result.GetProperty("message").GetString());
            Assert.Equal(0, host.GetRequiredService<JsonContractCounter>().Calls);
        }
    }

    [Fact]
    public async Task RpcCatalogIsAvailableThroughTheWebPluginBridge()
    {
        await using var host = CreateHost();
        await using var target = CreateRpcTarget(host);
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([
            LoadedWebPlugin(runtimeId),
            target.Plugin
        ]);
        using var rpcRouter = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(
            catalog,
            rpcRouter,
            host,
            new RejectingEventDeliveryJavaScriptRuntime());

        var result = await bridge.InvokeAsync(
            PluginId,
            runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"),
            "request", "request",
            JsonSerializer.SerializeToElement(new
            {
                capabilityName = QuantumCapabilityCatalog.CapabilityName,
                payload = new { },
                context = new { }
            }));

        Assert.True(result.GetProperty("isSuccess").GetBoolean());
        var value = SingleOutcome(result).GetProperty("value");
        Assert.Equal(2, value.GetProperty("schemaVersion").GetInt32());
        Assert.Contains(value.GetProperty("capabilities").EnumerateArray(),
            capability => capability.GetProperty("providerPluginIds")[0].GetString() == TargetPluginId
                && capability.GetProperty("name").GetString() == "test.echo");
    }

    [Theory]
    [InlineData("rpc", "invoke")]
    [InlineData("collect", "request")]
    [InlineData("eventBus", "publish")]
    public async Task RetiredPluginTransportsAreRejected(string transport, string method)
    {
        await using var host = CreateHost();
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(runtimeId)]);
        using var router = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(catalog, router, host, new RejectingEventDeliveryJavaScriptRuntime());

        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.InvokeAsync(
            PluginId, runtimeId.ToString("N"), Guid.NewGuid().ToString("N"), transport, method,
            JsonSerializer.SerializeToElement(new { capabilityName = "test.echo", payload = new { value = "hello" } })));
    }

    [Fact]
    public async Task MissingRequestReturnsAnEmptyReport()
    {
        await using var host = CreateHost();
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(runtimeId)]);
        using var rpcRouter = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(
            catalog,
            rpcRouter,
            host,
            new RejectingEventDeliveryJavaScriptRuntime());

        var result = await bridge.InvokeAsync(
            PluginId,
            runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"),
            "request", "request",
            JsonSerializer.SerializeToElement(new
            {
                capabilityName = "missing.service.call",
                payload = new { },
                context = new { }
            }));

        Assert.True(result.GetProperty("isSuccess").GetBoolean());
        Assert.Empty(result.GetProperty("value").GetProperty("outcomes").EnumerateArray());
    }

    [Fact]
    public async Task EnvironmentSnapshotReportsVersionRangeAndCompatibility()
    {
        await using var host = CreateHost();
        var runtimeId = Guid.NewGuid();
        await using var target = CreateRpcTarget(host);
        var catalog = new PluginCatalog([
            LoadedWebPlugin(
                runtimeId,
                [new PluginIntegration(
                    Quantum.Plugin.Abstraction.PluginId.Of(TargetPluginId),
                    VersionRange.Of("[1.0.0,2.0.0)"))]),
            target.Plugin
        ]);
        using var rpcRouter = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(
            catalog,
            rpcRouter,
            host,
            new RejectingEventDeliveryJavaScriptRuntime());

        var result = await bridge.InvokeAsync(
            PluginId,
            runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"),
            "environment",
            "snapshot",
            JsonSerializer.SerializeToElement(new { }));

        var integration = Assert.Single(result.GetProperty("integrations").EnumerateArray());
        Assert.Equal(TargetPluginId, integration.GetProperty("pluginId").GetString());
        Assert.Equal("[1.0.0,2.0.0)", integration.GetProperty("versionRange").GetString());
        Assert.True(integration.GetProperty("active").GetBoolean());
        Assert.False(integration.TryGetProperty("minimumVersion", out _));
    }

    [Fact]
    public async Task NotifyDeliversToDotNetAndIframeAndReleasesTheIframeSubscription()
    {
        await using var host = CreateHost();
        var contract = new QuantumNotificationContract("Reports state.", JsonSerializer.SerializeToElement(new { type = "object" }));
        var capabilities = new PluginCapabilities(provides: [Topic], notifications: new Dictionary<string, QuantumNotificationContract>
        {
            [Topic] = contract
        });
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(runtimeId, capabilities: capabilities)]);
        using var router = new PluginRpcRouter(catalog);
        var javaScript = new RejectingEventDeliveryJavaScriptRuntime();
        using var bridge = CreateBridge(catalog, router, host, javaScript);

        await using var notifier = host.GetRequiredService<QuantumPluginEventBusFactory>()
            .Create(new QuantumPluginInfo(TargetPluginId, "1.0.0"), capabilities.Notifications);
        var received = new List<QuantumNotification>();
        await using var subscription = notifier.Subscribe(Topic, (message, _) =>
        {
            received.Add(message);
            return Task.CompletedTask;
        });
        await InvokeAsync("notify", "subscribe", new { capabilityName = Topic, subscriptionId = "notification" });
        var first = await InvokeAsync("notify", "publish", new { capabilityName = Topic, payload = new { state = "ready" } });
        Assert.Equal(JsonValueKind.Null, first.ValueKind);
        Assert.Equal(1, javaScript.DispatchCount);
        var notification = Assert.Single(received);
        Assert.Equal("ready", notification.Payload.GetProperty("state").GetString());
        Assert.Equal(PluginId, notification.Publisher.Id);
        Assert.Equal("ready", javaScript.LastDelivery!.Payload.GetProperty("state").GetString());

        await notifier.PublishAsync(Topic, new { state = "from-dotnet" });
        Assert.Equal(2, javaScript.DispatchCount);
        Assert.Equal(TargetPluginId, javaScript.LastDelivery!.Publisher.Id);
        Assert.Equal("from-dotnet", javaScript.LastDelivery.Payload.GetProperty("state").GetString());

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync("notify", "publish",
            new { capabilityName = "undeclared.status", payload = new { } }));

        await bridge.ReleaseRuntimeAsync(PluginId, runtimeId.ToString("N"));
        await InvokeAsync("notify", "publish", new { capabilityName = Topic, payload = new { state = "updated" } });
        Assert.Equal(3, received.Count);
        Assert.Equal(2, javaScript.DispatchCount);

        var catalogResult = await InvokeAsync("request", "request",
            new { capabilityName = QuantumCapabilityCatalog.CapabilityName, payload = new { } });
        var entry = Assert.Single(SingleOutcome(catalogResult).GetProperty("value").GetProperty("capabilities").EnumerateArray(),
            entry => entry.GetProperty("name").GetString() == Topic);
        Assert.Equal("notify", entry.GetProperty("kind").GetString());
        Assert.Equal("object", entry.GetProperty("messageSchema").GetProperty("type").GetString());

        Task<JsonElement> InvokeAsync(string transport, string operation, object arguments)
            => bridge.InvokeAsync(PluginId, runtimeId.ToString("N"), Guid.NewGuid().ToString("N"),
                transport, operation, JsonSerializer.SerializeToElement(arguments));
    }

    [Fact]
    public async Task RequestKeepsEveryProviderOutcomeAndCatalogListsAllProviders()
    {
        await using var host = CreateHost();
        await using var failedHost = CreateHost("fail");
        await using var skippedHost = CreateHost("skip");
        await using var slowHost = CreateHost("slow");
        await using var success = CreateRpcTarget(host, "test.a");
        await using var failed = CreateRpcTarget(failedHost, "test.b");
        await using var skipped = CreateRpcTarget(skippedHost, "test.c");
        await using var slow = CreateRpcTarget(slowHost, "test.d");
        var runtimeId = Guid.NewGuid();
        var catalog = new PluginCatalog([LoadedWebPlugin(runtimeId), slow.Plugin, skipped.Plugin, failed.Plugin, success.Plugin]);
        using var router = new PluginRpcRouter(catalog);
        using var bridge = CreateBridge(catalog, router, host, new RejectingEventDeliveryJavaScriptRuntime());

        var collected = await InvokeAsync("request", "request", "test.suggestions");
        Assert.True(collected.GetProperty("isSuccess").GetBoolean());
        var outcomes = collected.GetProperty("value").GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(["test.a", "test.b", "test.c", "test.d"],
            outcomes.Select(item => item.GetProperty("providerPluginId").GetString()));
        Assert.Equal(["handled", "failed", "pass", "timedOut"],
            outcomes.Select(item => item.GetProperty("status").GetString()));
        Assert.Equal($"{PluginId}:hello", outcomes[0].GetProperty("value").GetString());
        Assert.Equal("test_rejected", outcomes[1].GetProperty("errorCode").GetString());
        Assert.Empty((await InvokeAsync("request", "request", "test.absent")).GetProperty("value").GetProperty("outcomes").EnumerateArray());


        using var serializer = new PluginRpcSerializer();
        using var invoker = new PluginRpcInvoker(Quantum.Plugin.Abstraction.PluginId.Of("test.collector"), Guid.NewGuid(), serializer);
        invoker.UseRegistry(PluginRpcRouter.CreateRegistry([success.Plugin, failed.Plugin, skipped.Plugin], host));
        var typed = await invoker.RequestAsync<string>("test.suggestions", new { value = "hello" }, Context.Empty);
        Assert.True(typed.IsSuccess);
        Assert.Equal("test.collector:hello", typed.Value!.Outcomes[0].Value);
        var typedCatalog = await invoker.DiscoverCapabilitiesAsync();
        Assert.True(typedCatalog.IsSuccess, typedCatalog.Message);
        Assert.Contains(typedCatalog.Value!.Capabilities, item =>
            item.Name == "test.suggestions" && item.Kind == QuantumCapabilityKind.Request);

        using var cancellation = new CancellationTokenSource();
        invoker.UseRegistry(PluginRpcRouter.CreateRegistry([slow.Plugin], host));
        var pending = invoker.RequestAsync<string>("test.suggestions", new { value = "hello" }, Context.Empty, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var requestReport = await InvokeAsync("request", "request", "test.echo");
        var replies = requestReport.GetProperty("value").GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(4, replies.Length);
        Assert.All(replies, reply => Assert.Equal("handled", reply.GetProperty("status").GetString()));

        var unified = await InvokeAsync("request", "request", QuantumCapabilityCatalog.CapabilityName);
        var entry = Assert.Single(SingleOutcome(unified).GetProperty("value").GetProperty("capabilities").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "test.suggestions");
        Assert.Equal("request", entry.GetProperty("kind").GetString());
        Assert.Equal(4, entry.GetProperty("providerPluginIds").GetArrayLength());
        Assert.Equal(JsonValueKind.Object, entry.GetProperty("responseSchema").ValueKind);

        Task<JsonElement> InvokeAsync(string transport, string operation, string capabilityName)
            => bridge.InvokeAsync(PluginId, runtimeId.ToString("N"), Guid.NewGuid().ToString("N"),
                transport, operation, JsonSerializer.SerializeToElement(new { capabilityName, payload = new { value = "hello" } }));
    }

    private static JsonElement SingleOutcome(JsonElement result)
        => Assert.Single(result.GetProperty("value").GetProperty("outcomes").EnumerateArray());

    private static ServiceProvider CreateHost(string contributorBehavior = "contribute")
        => new ServiceCollection()
            .AddQuantumPluginRpc()
            .AddQuantumPluginEventBus()
            .AddTransient<WebBridgeTestRpcService.Echo, WebBridgeEcho>()
            .AddTransient<WebBridgeTestRpcService.Suggest, WebBridgeSuggest>()
            .AddSingleton(new WebBridgeContributorBehavior(contributorBehavior))
            .AddSingleton<JsonContractCounter>()
            .AddTransient<JsonContractRpcService.Create, JsonContractCreate>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

    private static WebPluginInteropBridge CreateBridge(
        PluginCatalog catalog,
        PluginRpcRouter rpcRouter,
        ServiceProvider host,
        IJSRuntime javaScript)
        => new(
            catalog,
            rpcRouter,
            new TestNavigationManager(),
            javaScript,
            host.GetRequiredService<QuantumPluginEventBusFactory>(),
            NullLogger<WebPluginInteropBridge>.Instance);

    private static Task<JsonElement> SubscribeAsync(
        WebPluginInteropBridge bridge,
        Guid runtimeId)
        => bridge.InvokeAsync(
            PluginId,
            runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"),
            "notify",
            "subscribe",
            JsonSerializer.SerializeToElement(new
            {
                capabilityName = Topic,
                subscriptionId = "subscription"
            }));

    private static Task<JsonElement> PublishAsync(
        WebPluginInteropBridge bridge,
        Guid runtimeId)
        => bridge.InvokeAsync(
            PluginId,
            runtimeId.ToString("N"),
            Guid.NewGuid().ToString("N"),
            "notify",
            "publish",
            JsonSerializer.SerializeToElement(new
            {
                capabilityName = Topic,
                payload = new { state = "activated" }
            }));

    private static PluginCapabilities NotificationCapabilities()
        => new(provides: [Topic], notifications: new Dictionary<string, QuantumNotificationContract>
        {
            [Topic] = new(null, JsonSerializer.SerializeToElement(new { type = "object" }))
        });

    private static LoadedPlugin LoadedWebPlugin(
        Guid runtimeId,
        IEnumerable<PluginIntegration>? integrations = null,
        PluginCapabilities? capabilities = null)
        => new(
            new PluginManifest(
                Quantum.Plugin.Abstraction.PluginId.Of(PluginId),
                SemanticVersion.Of("1.0.0"),
                PluginRuntimeDefinition.Web("plugin.js"),
                integrations: integrations,
                capabilities: capabilities),
            Path.Combine("plugins", PluginId),
            entryAssembly: null,
            routes: [],
            runtimeId);

    private static RpcTarget CreateRpcTarget(ServiceProvider services, string providerId = TargetPluginId)
    {
        var runtimeId = Guid.NewGuid();
        var serializer = new PluginRpcSerializer();
        var rpcRuntime = PluginRpcRuntime.Create(
            Quantum.Plugin.Abstraction.PluginId.Of(providerId),
            runtimeId,
            typeof(WebBridgeTestRpcService).Assembly,
            services.GetRequiredService<IServiceScopeFactory>(),
            serializer,
            NullLogger.Instance);
        rpcRuntime.Resume();
        var plugin = new LoadedPlugin(
            new PluginManifest(
                Quantum.Plugin.Abstraction.PluginId.Of(providerId),
                SemanticVersion.Of("1.0.0"),
                "Target.dll"),
            Path.Combine("plugins", providerId),
            typeof(WebPluginInteropBridgeTests).Assembly,
            routes: [],
            runtimeId,
            services)
        {
            RpcRuntime = rpcRuntime
        };
        return new RpcTarget(plugin, rpcRuntime, serializer);
    }

    private sealed record RpcTarget(
        LoadedPlugin Plugin,
        PluginRpcRuntime Runtime,
        PluginRpcSerializer Serializer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Serializer.Dispose();
        }
    }

    private sealed class RejectingEventDeliveryJavaScriptRuntime : IJSRuntime
    {
        public int DispatchCount { get; private set; }

        public QuantumNotification? LastDelivery { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("quantum.plugins.dispatchEvent", identifier);
            DispatchCount++;
            LastDelivery = Assert.IsType<QuantumNotification>(args![3]);
            return ValueTask.FromException<TValue>(new JSException("delivery failed"));
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("https://quantum.local/", "https://quantum.local/");
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
        }
    }
}

public sealed record WebBridgeEchoRequest(string Value);

[TransportOverQuantum]
public interface IWebBridgeTestRpcService : IRpcService
{
    [RequestCapability("test.echo", QuantumDispatch.All)]
    Result<string> Echo(WebBridgeEchoRequest request);

    [RequestCapability("test.suggestions", QuantumDispatch.All)]
    QuantumRequestResult<string> Suggest(WebBridgeEchoRequest request);
}

public partial class WebBridgeTestRpcService : RpcServer<IWebBridgeTestRpcService>;

public sealed class WebBridgeEcho : WebBridgeTestRpcService.Echo
{
    public override Task<Result<string>> HandleAsync(
        WebBridgeEchoRequest request,
        Context context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var caller = context[QuantumRpcContextKeys.CallerPluginId] as string ?? "unknown";
        return Task.FromResult<Result<string>>($"{caller}:echo:{request.Value}");
    }
}


public sealed record WebBridgeContributorBehavior(string Mode);

public sealed class WebBridgeSuggest(WebBridgeContributorBehavior behavior) : WebBridgeTestRpcService.Suggest
{
    public override async Task<QuantumRequestResult<string>> HandleAsync(
        WebBridgeEchoRequest request, Context context, CancellationToken cancellationToken)
    {
        if (behavior.Mode == "slow")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (behavior.Mode == "fail")
        {
            throw new NOF.Domain.DomainException("test_rejected", "The Contributor declined the request with an error.");
        }

        var caller = context[QuantumRpcContextKeys.CallerPluginId] as string;
        return behavior.Mode == "skip"
            ? QuantumRequestResult<string>.Pass()
            : QuantumRequestResult<string>.Answer($"{caller}:{request.Value}");
    }
}
