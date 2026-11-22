using Quantum.Mobile.Internal;
using Quantum.Storage;
using System.Reflection;

namespace Quantum.Marketplace;

public sealed record QuantumMarketplaceOptions(Uri BaseAddress, string SdkVersion)
{
    internal bool IsOffline { get; init; }

    public static QuantumMarketplaceOptions FromEnvironment() => FromEnvironment(
        DesktopBuildProfile.Current, Environment.GetEnvironmentVariable);

    internal static QuantumMarketplaceOptions FromEnvironment(DesktopBuildProfile profile, Func<string, string?> environment)
    {
        var configuredAddress = environment(profile.IsPreview ? "QUANTUM_PREVIEW_PLATFORM_URL" : "QUANTUM_PLATFORM_URL");
        var offline = profile.IsPreview && string.IsNullOrWhiteSpace(configuredAddress);
        var address = string.IsNullOrWhiteSpace(configuredAddress)
            ? profile.IsPreview ? "http://127.0.0.1:51999/" : PlatformAddress.DefaultUrl
            : configuredAddress.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var baseAddress)
            || baseAddress.Scheme is not ("http" or "https"))
        {
            if (profile.IsPreview)
            {
                throw new InvalidOperationException("QUANTUM_PREVIEW_PLATFORM_URL must be an absolute HTTP(S) test platform URL.");
            }

            baseAddress = new Uri(PlatformAddress.DefaultUrl);
        }

        if (profile.IsPreview && !offline)
        {
            var production = new Uri(PlatformAddress.DefaultUrl);
            var configuredProduction = environment("QUANTUM_PLATFORM_URL");
            if (string.Equals(baseAddress.IdnHost, production.IdnHost, StringComparison.OrdinalIgnoreCase)
                || (Uri.TryCreate(configuredProduction, UriKind.Absolute, out var stableAddress)
                    && ((string.Equals(baseAddress.IdnHost, stableAddress.IdnHost, StringComparison.OrdinalIgnoreCase)
                            && (!baseAddress.IsLoopback || baseAddress.Port == stableAddress.Port))
                        || (baseAddress.IsLoopback && stableAddress.IsLoopback && baseAddress.Port == stableAddress.Port))))
            {
                throw new InvalidOperationException("The preview platform must be separate from the production platform.");
            }
        }

        var configuredVersion = environment("QUANTUM_SDK_VERSION");
        var sdkVersion = string.IsNullOrWhiteSpace(configuredVersion)
            ? typeof(IQuantumPlugin).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+')[0]
            : configuredVersion.Trim();
        return new QuantumMarketplaceOptions(baseAddress, sdkVersion) { IsOffline = offline };
    }
}
