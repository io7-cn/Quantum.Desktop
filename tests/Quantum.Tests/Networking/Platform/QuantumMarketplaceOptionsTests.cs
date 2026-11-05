using System.Reflection;
using Quantum.Marketplace;

namespace Quantum.Tests;

[Collection("Marketplace environment")]
public sealed class QuantumMarketplaceOptionsTests
{
    [Fact]
    public void DefaultVersionMatchesReferencedSdkAndOmitsBuildMetadata()
    {
        var previous = Environment.GetEnvironmentVariable("QUANTUM_SDK_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("QUANTUM_SDK_VERSION", null);

            var options = QuantumMarketplaceOptions.FromEnvironment();
            var version = typeof(IQuantumPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0];

            Assert.Equal(version, options.SdkVersion);
            Assert.DoesNotContain('+', options.SdkVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUANTUM_SDK_VERSION", previous);
        }
    }

    [Fact]
    public void HostVersionOverrideDoesNotChangeSdkCompatibility()
    {
        var previousHost = Environment.GetEnvironmentVariable("QUANTUM_VERSION");
        var previousSdk = Environment.GetEnvironmentVariable("QUANTUM_SDK_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("QUANTUM_VERSION", "99.0.0");
            Environment.SetEnvironmentVariable("QUANTUM_SDK_VERSION", null);
            var version = typeof(IQuantumPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0];

            Assert.Equal(version, QuantumMarketplaceOptions.FromEnvironment().SdkVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUANTUM_VERSION", previousHost);
            Environment.SetEnvironmentVariable("QUANTUM_SDK_VERSION", previousSdk);
        }
    }

    [Fact]
    public void ExplicitDevelopmentVersionOverrideStillWorks()
    {
        var previous = Environment.GetEnvironmentVariable("QUANTUM_SDK_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("QUANTUM_SDK_VERSION", " 9.8.7-beta.1 ");

            var options = QuantumMarketplaceOptions.FromEnvironment();

            Assert.Equal("9.8.7-beta.1", options.SdkVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUANTUM_SDK_VERSION", previous);
        }
    }
}
