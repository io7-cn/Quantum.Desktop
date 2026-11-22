using Quantum.Updates;

namespace Quantum.Tests;

public sealed class PreviewUpdateBackendTests
{
    [Fact]
    public async Task PreviewDoesNotOfferOrApplyProductionUpdates()
    {
        var backend = new PreviewUpdateBackend();
        Assert.False(backend.IsInstalled);
        Assert.Null(await backend.CheckAsync());
        Assert.Null(backend.PendingVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.DownloadAsync(_ => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.ApplyAndRestartAsync());
    }
}
