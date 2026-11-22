using Quantum.Storage;

namespace Quantum.Tests;

public sealed class DesktopBuildProfileTests
{
    [Fact]
    public void StableIdentityIsRetainedAndPreviewUsesADifferentIdentity()
    {
        Assert.Equal("io.quantum.desktop", DesktopBuildProfile.Stable.ApplicationId);
        Assert.Equal("io.quantum.desktop.preview", DesktopBuildProfile.Preview.ApplicationId);
        Assert.Equal("Quantum Preview", DesktopBuildProfile.Preview.ApplicationTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Development")]
    public void AnUnknownProfileCannotFallBackToProduction(string? name)
        => Assert.Throws<InvalidOperationException>(() => DesktopBuildProfile.Parse(name));
}
