using System.Net;
using Quantum.Marketplace;
using Quantum.Storage;

namespace Quantum.Tests;

public sealed class PreviewPlatformTests
{
    [Fact]
    public async Task PreviewIsOfflineByDefaultEvenWhenAProductionOverrideExists()
    {
        var options = Options(DesktopBuildProfile.Preview, stable: "https://production.example/");
        Assert.True(options.IsOffline);
        Assert.True(options.BaseAddress.IsLoopback);
        using var client = new HttpClient(new OfflinePlatformHttpHandler());
        using var response = await client.GetAsync("https://production.example/private");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void PreviewRequiresItsOwnExplicitPlatformAndStableRetainsItsEndpoint()
    {
        var preview = Options(DesktopBuildProfile.Preview, "https://test.example/api", "https://production.example/");
        Assert.Equal("https://test.example/api/", preview.BaseAddress.AbsoluteUri);
        Assert.False(preview.IsOffline);
        var stable = Options(DesktopBuildProfile.Stable, "https://test.example/", "https://production.example/");
        Assert.Equal("https://production.example/", stable.BaseAddress.AbsoluteUri);
        Assert.False(stable.IsOffline);
    }

    [Theory]
    [InlineData("not a URL")]
    [InlineData("file:///tmp/test")]
    [InlineData("https://quantum.io-vii.com/test")]
    [InlineData("http://quantum.io-vii.com/")]
    [InlineData("https://production.example/another-path")]
    [InlineData("http://production.example/")]
    public void InvalidOrProductionPreviewEndpointsStopStartup(string endpoint)
        => Assert.Throws<InvalidOperationException>(() => Options(DesktopBuildProfile.Preview, endpoint, "https://production.example/"));

    private static QuantumMarketplaceOptions Options(DesktopBuildProfile profile, string? preview = null, string? stable = null)
        => QuantumMarketplaceOptions.FromEnvironment(profile, name => name switch
        {
            "QUANTUM_PREVIEW_PLATFORM_URL" => preview,
            "QUANTUM_PLATFORM_URL" => stable,
            _ => null
        });
}
