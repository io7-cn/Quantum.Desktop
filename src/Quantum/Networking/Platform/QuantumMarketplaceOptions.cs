using System.Reflection;
using Quantum.Mobile.Internal;

namespace Quantum.Marketplace;

public sealed record QuantumMarketplaceOptions(Uri BaseAddress, string SdkVersion)
{
    public static QuantumMarketplaceOptions FromEnvironment()
    {
        var configuredAddress = Environment.GetEnvironmentVariable("QUANTUM_PLATFORM_URL");
        var address = string.IsNullOrWhiteSpace(configuredAddress)
            ? PlatformAddress.DefaultUrl
            : configuredAddress.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            baseAddress = new Uri(PlatformAddress.DefaultUrl);
        }
        var configuredVersion = Environment.GetEnvironmentVariable("QUANTUM_SDK_VERSION");
        var sdkVersion = string.IsNullOrWhiteSpace(configuredVersion)
            ? typeof(IQuantumPlugin).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0]
            : configuredVersion.Trim();
        return new QuantumMarketplaceOptions(baseAddress, sdkVersion);
    }
}
