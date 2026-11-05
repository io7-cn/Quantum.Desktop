using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NOF.Contract;
using Quantum.Mobile.Internal;

namespace Quantum.Marketplace;

public sealed class QuantumMarketplaceClient : IQuantumMarketplaceClient
{
    private readonly HttpClient _httpClient;
    private readonly QuantumMarketplaceSession? _session;

    public QuantumMarketplaceClient(
        HttpClient httpClient,
        QuantumMarketplaceOptions options,
        QuantumMarketplaceSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _session = session;
        BaseAddress = options.BaseAddress;
        _session?.Configure(httpClient, BaseAddress);
    }

    public Uri BaseAddress { get; }

    public Task<MarketplaceLogin> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
        => InvokeAsync<MarketplaceLogin>(
            "Login",
            new { email = email.Trim(), password },
            AuthenticationMode.None,
            cancellationToken);

    public Task<MarketplaceUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => InvokeAsync<MarketplaceUser>("GetCurrentUser", new { }, AuthenticationMode.Required, cancellationToken);

    public async Task<IReadOnlyList<MarketplacePlugin>> ListPluginsAsync(
        string? search = null,
        IReadOnlyCollection<string>? tags = null,
        CancellationToken cancellationToken = default)
        => await InvokeAsync<MarketplacePlugin[]>(
            "ListPlugins",
            new
            {
                search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                tags = tags?.Where(static tag => !string.IsNullOrWhiteSpace(tag)).ToArray() ?? []
            },
            AuthenticationMode.None,
            cancellationToken);

    public Task<MarketplacePluginDetails> GetPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => InvokeAsync<MarketplacePluginDetails>(
            "GetPlugin",
            new { pluginId = RequirePluginId(pluginId) },
            AuthenticationMode.None,
            cancellationToken);

    public Task<MarketplaceCompatibility> CheckCompatibilityAsync(
        string pluginId,
        string sdkVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkVersion);
        return InvokeAsync<MarketplaceCompatibility>(
            "CheckCompatibility",
            new { pluginId = RequirePluginId(pluginId), sdkVersion = sdkVersion.Trim() },
            AuthenticationMode.None,
            cancellationToken);
    }

    public async Task<MarketplaceDownload> DownloadAsync(
        string pluginId,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var response = await InvokeAsync<DownloadResponse>(
            "DownloadPluginRelease",
            new { pluginId = RequirePluginId(pluginId), version = version.Trim() },
            AuthenticationMode.Optional,
            cancellationToken);

        byte[] archive;
        try
        {
            archive = Convert.FromBase64String(response.PackageArchiveBase64);
        }
        catch (FormatException exception)
        {
            throw new MarketplaceException(
                $"The marketplace returned an invalid package for '{pluginId}@{version}'.",
                exception);
        }

        if (archive.LongLength != response.PackageSizeBytes)
        {
            throw new MarketplaceException($"The downloaded package size for '{pluginId}@{version}' does not match its metadata.");
        }

        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(archive));
        if (!string.Equals(actualSha256, response.PackageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new MarketplaceException($"The downloaded package checksum for '{pluginId}@{version}' does not match its metadata.");
        }

        return new MarketplaceDownload(
            response.FileName,
            response.ContentType,
            archive,
            response.PackageSizeBytes,
            response.PackageSha256);
    }

    public async Task<IReadOnlyList<MarketplacePlugin>> ListManagedPluginsAsync(
        CancellationToken cancellationToken = default)
        => await InvokeAsync<MarketplacePlugin[]>(
            "ListManagedPlugins",
            new { },
            AuthenticationMode.Required,
            cancellationToken);

    public async Task<IReadOnlyList<MarketplaceRelease>> ListPluginReleasesAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
        => await InvokeAsync<MarketplaceRelease[]>(
            "ListPluginReleases",
            new { pluginId = RequirePluginId(pluginId) },
            AuthenticationMode.Required,
            cancellationToken);

    private async Task<T> InvokeAsync<T>(
        string method,
        object parameters,
        AuthenticationMode authenticationMode,
        CancellationToken cancellationToken)
    {
        if (authenticationMode != AuthenticationMode.None && _session?.IsAuthenticated == true)
        {
            return await _session.Authentication.ExecuteAsync((token, ct) => InvokeCoreAsync<T>(method, parameters, token, ct), cancellationToken);
        }
        if (authenticationMode == AuthenticationMode.Required)
        {
            throw new PlatformAuthenticationException();
        }

        return await InvokeCoreAsync<T>(method, parameters, null, cancellationToken);
    }

    private async Task<T> InvokeCoreAsync<T>(string method, object parameters, string? accessToken, CancellationToken cancellationToken)
    {
        Result<JsonElement> result;
        try
        {
            result = await PlatformRpcClient.SendAsync(
                _httpClient,
                BaseAddress,
                method,
                JsonSerializer.SerializeToElement(parameters, JsonSerializerOptions.Web),
                accessToken,
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new MarketplaceException($"The marketplace returned an invalid JSON-RPC response for '{method}'.", exception);
        }

        if (!result.IsSuccess)
        {
            if (result.ErrorCode is "401" or "authentication_required" or "user_not_found")
            {
                throw new PlatformAuthenticationException();
            }

            if (int.TryParse(result.ErrorCode, NumberStyles.None, CultureInfo.InvariantCulture, out var status)
                && status is >= 100 and <= 599)
            {
                throw new MarketplaceException($"The marketplace returned HTTP {status}.");
            }

            throw new MarketplaceException(string.IsNullOrWhiteSpace(result.Message)
                ? "The marketplace rejected the request."
                : result.Message);
        }

        JsonElement value = result.Value;
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new MarketplaceException($"The marketplace returned an empty result for '{method}'.");
        }

        return value.Deserialize<T>(JsonSerializerOptions.Web)
            ?? throw new MarketplaceException($"The marketplace returned an empty result for '{method}'.");
    }

    private static string RequirePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return pluginId.Trim();
    }

    private sealed record DownloadResponse(
        string FileName,
        string ContentType,
        string PackageArchiveBase64,
        long PackageSizeBytes,
        string PackageSha256);

    private enum AuthenticationMode
    {
        None,
        Optional,
        Required
    }
}
