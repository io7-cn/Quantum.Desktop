using Quantum.Marketplace;
using Quantum.Mobile;
using Quantum.Plugins;

namespace Quantum.Tests.Networking.Relay;

public sealed class MobileDesktopBridgeTests
{
    [Fact]
    public async Task ShutdownStopsAutomaticLoopBeforeContainerDisposalWithoutRevokingRetainedCredentials()
    {
        using var http = new HttpClient();
        var session = new QuantumMarketplaceSession(
            () => Task.FromResult<string?>(null), _ => Task.CompletedTask, () => Task.CompletedTask);
        var options = new QuantumMarketplaceOptions(new Uri("https://example.invalid/"), "0.3.0-rc.2");
        var marketplace = new QuantumMarketplaceClient(http, options, session);
        var catalog = new PluginCatalog([]);
        using var router = new PluginRpcRouter(catalog);
        var writes = 0;
        await using var bridge = new MobileDesktopBridge(
            http, session, options, new MobileActionGateway(catalog, router),
            Path.Combine(Path.GetTempPath(), "unused-quantum-journal"),
            _ => Task.FromResult<string?>(null), (_, _) => { writes++; return Task.CompletedTask; }, _ => writes++);
        bridge.Initialize(marketplace);
        await bridge.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await bridge.ShutdownAsync();
        Assert.False(bridge.IsRunning);
        Assert.Equal(0, writes);
    }
}
