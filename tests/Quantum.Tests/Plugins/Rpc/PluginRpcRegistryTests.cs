using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NOF.Application;
using NOF.Contract;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class PluginRpcRegistryTests
{
    [Theory]
    [InlineData("ordering.ping", "domain", "probe_invalid")]
    [InlineData("ordering.ping", "unexpected", "500")]
    [InlineData("ordering.reset", "domain", "probe_invalid")]
    [InlineData("ordering.reset", "unexpected", "500")]
    public async Task NativeInboundPipelineReturnsFailuresAndRecovers(string operation, string trigger, string errorCode)
    {
        await using var fixture = CreateRuntime("quantum.plugin.a");
        var registry = PluginRpcRegistry.Create([fixture.Runtime]);
        var callContext = new PluginRpcCallContext("quantum.plugin.caller", Guid.NewGuid().ToString("N"), new Dictionary<string, System.Text.Json.JsonElement>());
        var response = await registry.RequestAsync(operation,
            System.Text.Json.JsonSerializer.SerializeToElement(new OrderingRequest(trigger)), callContext,
            CancellationToken.None);
        var failure = Assert.Single(response.Value!.Outcomes);
        Assert.Equal(QuantumRequestStatus.Failed, failure.Status);
        Assert.Equal(errorCode, failure.ErrorCode);
        Assert.Equal(trigger == "domain" ? "Invalid probe." : "Internal server error", failure.Message);
        Assert.True((await DispatchAsync(registry, "ordering.ping", "")).IsSuccess);
    }

    [Fact]
    public async Task RequestQueriesEveryProviderAndSingleProjectionRefusesAmbiguity()
    {
        await using var last = CreateRuntime("quantum.plugin.z");
        await using var first = CreateRuntime("quantum.plugin.a");
        var registry = PluginRpcRegistry.Create([last.Runtime, first.Runtime]);
        using var invoker = new PluginRpcInvoker(PluginId.Of("quantum.plugin.caller"), Guid.NewGuid(), first.Serializer);
        invoker.UseRegistry(registry);

        var catalog = await invoker.DiscoverCapabilitiesAsync();
        Assert.True(catalog.IsSuccess, catalog.Message);
        var capability = Assert.Single(catalog.Value!.Capabilities, item => item.Name == "ordering.ping");
        Assert.Equal(QuantumDispatch.All, capability.Dispatch);
        Assert.Equal(["quantum.plugin.a", "quantum.plugin.z"], capability.ProviderPluginIds);

        var result = await invoker.RequestAsync<string>(capability.Name, new OrderingRequest(), Context.Empty);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["quantum.plugin.a", "quantum.plugin.z"], result.Value!.Outcomes.Select(item => item.Value));
        var single = await invoker.RequestSingleAsync<string>(capability.Name, new OrderingRequest(), Context.Empty);
        Assert.Equal("capability_multiple_outcomes", single.ErrorCode);

        await first.Runtime.PauseAsync(CancellationToken.None);
        Assert.Equal("quantum.plugin.z", (await invoker.RequestSingleAsync<string>(capability.Name, new OrderingRequest(), Context.Empty)).Value);
    }

    [Theory]
    [InlineData("owned-by-last", QuantumRequestStatus.Pass, 0, 1)]
    [InlineData("write-fails", QuantumRequestStatus.Failed, 1, 0)]
    [InlineData("owned-by-first", QuantumRequestStatus.Handled, 1, 0)]
    public async Task OrderedOwnerContinuesOnlyOnPass(
        string topic, QuantumRequestStatus firstStatus, int firstWrites, int lastWrites)
    {
        await using var first = CreateRuntime("quantum.plugin.a");
        await using var last = CreateRuntime("quantum.plugin.z");
        var registry = PluginRpcRegistry.Create([last.Runtime, first.Runtime]);

        var result = await DispatchAsync(registry, "ordering.write", topic);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(firstStatus, result.Value!.Outcomes[0].Status);
        Assert.True(result.Value.Stopped);
        Assert.Equal(firstWrites, first.Services.GetRequiredService<RpcProviderMarker>().Writes);
        Assert.Equal(lastWrites, last.Services.GetRequiredService<RpcProviderMarker>().Writes);
    }

    [Fact]
    public async Task OrderedContributorCanStopBeforeInvokingLaterProviders()
    {
        await using var first = CreateRuntime("quantum.plugin.a");
        await using var last = CreateRuntime("quantum.plugin.z");
        var registry = PluginRpcRegistry.Create([last.Runtime, first.Runtime]);

        var result = await DispatchAsync(registry, "ordering.chain", "stop");
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Value!.Stopped);
        Assert.Equal("quantum.plugin.a", Assert.Single(result.Value.Outcomes).ProviderPluginId);
        Assert.Equal(0, last.Services.GetRequiredService<RpcProviderMarker>().Writes);
    }

    private static Task<Result<QuantumRequestReport<System.Text.Json.JsonElement?>>> DispatchAsync(
        PluginRpcRegistry registry, string name, string topic)
        => registry.RequestAsync(name, System.Text.Json.JsonSerializer.SerializeToElement(new { value = topic }),
            new PluginRpcCallContext("test.caller", "", new Dictionary<string, System.Text.Json.JsonElement>()),
            CancellationToken.None);

    [Fact]
    public async Task MobileBindingDoesNotFallBackWhenItsProviderStops()
    {
        await using var last = CreateRuntime("quantum.plugin.z");
        await using var first = CreateRuntime("quantum.plugin.a");
        var registry = PluginRpcRegistry.Create([last.Runtime, first.Runtime]);
        Assert.Throws<InvalidOperationException>(() => registry.ResolveMobileTarget("ordering.ping"));
        var boundRuntime = registry.ResolveMobileTarget("ordering.ping", first.Runtime.RuntimeId);
        Assert.Equal(first.Runtime.RuntimeId, boundRuntime);

        await first.Runtime.PauseAsync(CancellationToken.None);
        Assert.Equal(last.Runtime.RuntimeId, registry.ResolveMobileTarget("ordering.ping"));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = registry.InvokeMobileAsync(
            "ordering.ping", boundRuntime, System.Text.Json.JsonSerializer.SerializeToElement(new OrderingRequest()),
            new PluginRpcCallContext("quantum.plugin.caller", "", new Dictionary<string, System.Text.Json.JsonElement>()),
                CancellationToken.None);
        });
    }

    [Theory]
    [InlineData("ordering.alias", "rpc_not_found")]
    [InlineData("quantum.plugin.a.ordering.ping", "rpc_not_found")]
    [InlineData("missing.rpc", "rpc_not_found")]
    [InlineData("ORDERING.PING", "rpc_invalid_name")]
    public async Task OnlyTheDeclaredNameCanInvokeACapability(string name, string error)
    {
        await using var runtime = CreateRuntime("quantum.plugin.a");
        var registry = PluginRpcRegistry.Create([runtime.Runtime]);
        var invocation = await registry.RequestAsync(
            name,
            System.Text.Json.JsonSerializer.SerializeToElement(new { }),
            new PluginRpcCallContext("quantum.plugin.caller", Guid.NewGuid().ToString("N"),
                new Dictionary<string, System.Text.Json.JsonElement>()),
            CancellationToken.None);

        if (error == "rpc_not_found")
        {
            Assert.Empty(invocation.Value!.Outcomes);
        }
        else
        {
            Assert.Equal(error, invocation.ErrorCode);
        }
    }

    [Fact]
    public async Task RuntimeRejectsCapabilitiesMissingFromTheManifest()
    {
        await using var fixture = CreateRuntime("quantum.plugin.a");
        var error = Assert.Throws<InvalidOperationException>(() => PluginRpcRuntime.Create(
            fixture.Runtime.PluginId, Guid.NewGuid(), typeof(OrderingRpcServer).Assembly,
            fixture.Services.GetRequiredService<IServiceScopeFactory>(), fixture.Serializer, NullLogger.Instance,
            ["ordering.unknown"]));
        Assert.Contains("Missing methods: ordering.unknown", error.Message, StringComparison.Ordinal);
        Assert.Contains("ordering.ping", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonGenericRpcInvokerReturnsResultWithoutResponsePayload()
    {
        await using var runtime = CreateRuntime("quantum.plugin.a");
        var registry = PluginRpcRegistry.Create([runtime.Runtime]);
        using var callerSerializer = new PluginRpcSerializer();
        using var invoker = new PluginRpcInvoker(
            PluginId.Of("quantum.plugin.caller"),
            Guid.NewGuid(),
            callerSerializer);
        invoker.UseRegistry(registry);

        var result = await invoker.RequestSingleAsync(
            "ordering.reset",
            new OrderingRequest(),
            Context.Empty);

        Assert.True(result.IsSuccess, $"{result.ErrorCode}: {result.Message}");
    }

    [Fact]
    public async Task CatalogExportsDescriptionsSchemasAndAllAttributeMetadata()
    {
        await using var runtime = CreateRuntime("quantum.plugin.a");
        var registry = PluginRpcRegistry.Create([runtime.Runtime]);
        using var callerSerializer = new PluginRpcSerializer();
        using var invoker = new PluginRpcInvoker(
            PluginId.Of("quantum.plugin.caller"),
            Guid.NewGuid(),
            callerSerializer);
        invoker.UseRegistry(registry);

        var result = await invoker.RequestSingleAsync<System.Text.Json.JsonElement>(
            QuantumCapabilityCatalog.CapabilityName,
            new { },
            Context.Empty);

        Assert.True(result.IsSuccess, $"{result.ErrorCode}: {result.Message}");
        Assert.Equal(2, result.Value.GetProperty("schemaVersion").GetInt32());
        var method = Assert.Single(result.Value.GetProperty("capabilities").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "ordering.ping");
        Assert.Equal("Returns the selected provider.", method.GetProperty("description").GetString());
        Assert.Equal("quantum.plugin.a", method.GetProperty("providerPluginIds")[0].GetString());
        var probe = Assert.Single(method.GetProperty("attributes").EnumerateArray(), attribute =>
            attribute.GetProperty("type").GetString() == typeof(RpcCatalogProbeAttribute).FullName);
        Assert.Equal("method", probe.GetProperty("constructorArguments")[0].GetProperty("value").GetString());
        Assert.True(probe.GetProperty("namedArguments").GetProperty("Enabled").GetProperty("value").GetBoolean());
        Assert.Contains(
            method.GetProperty("attributes").EnumerateArray(),
            attribute => attribute.GetProperty("type").GetString()
                == typeof(CategoryAttribute).FullName);
        Assert.Contains(
            method.GetProperty("parameterAttributes").EnumerateArray(),
            attribute => attribute.GetProperty("type").GetString()
                == typeof(DescriptionAttribute).FullName);
        Assert.Contains(
            method.GetProperty("returnAttributes").EnumerateArray(),
            attribute => attribute.GetProperty("type").GetString()
                == typeof(DescriptionAttribute).FullName);
        Assert.Equal(
            "Ordering request payload.",
            method.GetProperty("requestSchema").GetProperty("description").GetString());
        Assert.Equal(
            "Selected provider plugin id.",
            method.GetProperty("responseSchema").GetProperty("description").GetString());
        Assert.Equal(
            "Probe input value.",
            method.GetProperty("requestSchema")
                .GetProperty("properties")
                .GetProperty("value")
                .GetProperty("description")
                .GetString());
    }

    private static RpcRuntimeFixture CreateRuntime(string pluginId, IEnumerable<string>? provides = null)
    {
        var services = new ServiceCollection()
            .AddQuantumPluginRpc()
            .AddSingleton(new RpcProviderMarker(pluginId))
            .AddTransient<OrderingRpcServer.Ping, OrderingPing>()
            .AddTransient<OrderingRpcServer.Reset, OrderingReset>()
            .AddTransient<OrderingRpcServer.Write, OrderingWrite>()
            .AddTransient<OrderingRpcServer.Chain, OrderingChain>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
        var serializer = new PluginRpcSerializer();
        var runtime = PluginRpcRuntime.Create(
            PluginId.Of(pluginId),
            Guid.NewGuid(),
            typeof(OrderingRpcServer).Assembly,
            services.GetRequiredService<IServiceScopeFactory>(),
            serializer,
            NullLogger.Instance,
            provides);
        runtime.Resume();
        return new RpcRuntimeFixture(runtime, serializer, services);
    }

    private sealed record RpcRuntimeFixture(
        PluginRpcRuntime Runtime,
        PluginRpcSerializer Serializer,
        ServiceProvider Services) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Services.DisposeAsync();
            Serializer.Dispose();
        }
    }
}

public sealed record OrderingRequest(
    [property: Description("Probe input value.")] string? Value = null);

public sealed record RpcProviderMarker(string PluginId)
{
    public int Writes { get; set; }
}

[Description("Ordering RPC service.")]
[Category("testing")]
[TransportOverQuantum]
public interface IOrderingRpcService : IRpcService
{
    [Description("Returns the selected provider.")]
    [Category("testing")]
    [return: Description("Selected provider plugin id.")]
    [RpcCatalogProbe("method", Enabled = true)]
    [RequestCapability("ordering.ping", QuantumDispatch.All)]
    Result<string> Ping([Description("Ordering request payload.")] OrderingRequest request);

    [RequestCapability("ordering.reset", QuantumDispatch.Ordered, UniqueOwner = true, RequireSingleProvider = true)]
    Result Reset(OrderingRequest request);

    [RequestCapability("ordering.write", QuantumDispatch.Ordered, UniqueOwner = true)]
    Result Write(OrderingRequest request);

    [RequestCapability("ordering.chain", QuantumDispatch.Ordered, AllowStop = true)]
    QuantumRequestResult<string> Chain(OrderingRequest request);
}

[AttributeUsage(AttributeTargets.All)]
public sealed class RpcCatalogProbeAttribute(string label) : Attribute
{
    public string Label { get; } = label;

    public bool Enabled { get; set; }
}

public partial class OrderingRpcServer : RpcServer<IOrderingRpcService>;

public sealed class OrderingPing(RpcProviderMarker provider) : OrderingRpcServer.Ping
{
    public override Task<Result<string>> HandleAsync(
        OrderingRequest request,
        Context context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Value == "domain")
        {
            throw new NOF.Domain.DomainException("probe_invalid", "Invalid probe.");
        }
        if (request.Value == "unexpected")
        {
            throw new InvalidOperationException("Private implementation detail.");
        }
        return Task.FromResult<Result<string>>(provider.PluginId);
    }
}

public sealed class OrderingReset : OrderingRpcServer.Reset
{
    public override Task<Result> HandleAsync(
        OrderingRequest request,
        Context context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Value == "domain")
        {
            throw new NOF.Domain.DomainException("probe_invalid", "Invalid probe.");
        }
        if (request.Value == "unexpected")
        {
            throw new InvalidOperationException("Private implementation detail.");
        }
        return Task.FromResult(Result.Success());
    }
}

public sealed class OrderingWrite(RpcProviderMarker provider) : OrderingRpcServer.Write
{
    public override Task<Result> HandleAsync(OrderingRequest request, Context context, CancellationToken cancellationToken)
    {
        if (request.Value == "owned-by-last" && provider.PluginId == "quantum.plugin.a")
        {
            return Task.FromResult<Result>(Result.Fail(QuantumRequestContext.PassCode, "Not owned."));
        }

        provider.Writes++;
        return Task.FromResult(request.Value == "write-fails"
            ? Result.Fail("write_failed", "Write failed.")
            : Result.Success());
    }
}

public sealed class OrderingChain(RpcProviderMarker provider) : OrderingRpcServer.Chain
{
    public override Task<QuantumRequestResult<string>> HandleAsync(
        OrderingRequest request, Context context, CancellationToken cancellationToken)
    {
        provider.Writes++;
        return Task.FromResult(QuantumRequestResult<string>.AnswerAndStop(provider.PluginId));
    }
}
