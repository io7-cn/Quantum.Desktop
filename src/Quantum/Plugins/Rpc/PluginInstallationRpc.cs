using System.Text.Json;
using Microsoft.Extensions.Logging;
using NOF.Contract;
using NOF.Domain;
using Quantum.Localization;
using Quantum.Marketplace;
using Quantum.Plugin.Abstraction;

namespace Quantum.Plugins;

internal delegate Task<bool> ConfirmPluginInstallation(string callerId, string source);

internal sealed class PluginInstallationRpc(
    PluginCatalog catalog,
    PluginRuntimeManager runtime,
    IQuantumMarketplaceClient marketplace,
    MarketplacePluginInstaller marketplaceInstaller,
    ConfirmPluginInstallation confirm,
    ILogger<PluginInstallationRpc> logger)
{
    private int _installing;
    private readonly object _operationsLock = new();
    private readonly Dictionary<Guid, (string CallerId, QuantumPluginInstallReceipt Receipt)> _operations = [];
    private readonly Queue<Guid> _operationOrder = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PluginRpcDispatchResult> InvokeAsync(
        string name, JsonElement payload, PluginRpcCallContext context, CancellationToken cancellationToken)
    {
        if (name == QuantumPluginInstallationExtensions.GetPluginInstallStatusCapabilityName)
        {
            if (!Guid.TryParseExact(ReadString(payload, "operationId"), "N", out var id))
                return InvalidPayload();

            lock (_operationsLock)
                return _operations.TryGetValue(id, out var operation) && operation.CallerId == context.CallerPluginId
                    ? Receipt(operation.Receipt)
                    : PluginRpcDispatchResult.Fail(PluginRpcErrors.NotFound, AppText.Get("PluginRpcInstallNotFound"));
        }

        var fromMarket = name == QuantumPluginInstallationExtensions.InstallMarketplaceCapabilityName;
        var source = ReadString(payload, fromMarket ? "pluginId" : "archiveFileName");
        if (string.IsNullOrWhiteSpace(source))
            return InvalidPayload();

        if (fromMarket)
        {
            try { source = PluginId.Of(source).ToString(); }
            catch (DomainValidationException) { return InvalidPayload(); }
        }
        else if (source.Length > 255 || source.Any(char.IsControl) || source.IndexOfAny(['/', '\\', ':']) >= 0
            || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return InvalidPayload();
        }

        if (!CanInstall(context) || Interlocked.Exchange(ref _installing, 1) != 0)
            return PluginRpcDispatchResult.Fail(PluginRpcErrors.Unavailable, AppText.Get("PluginRpcInstallUnavailable"));

        var queued = false;
        Task<bool>? confirmation = null;
        try
        {
            byte[]? archive = null;
            if (!fromMarket)
            {
                var encoded = ReadString(payload, "archiveBase64");
                if (string.IsNullOrEmpty(encoded)
                    || encoded.Length > ((PluginPackageLimits.MaximumArchiveBytes + 2) / 3) * 4)
                    return InvalidPayload();
                try { archive = Convert.FromBase64String(encoded); }
                catch (FormatException) { return InvalidPayload(); }
                if (archive.Length == 0 || archive.LongLength > PluginPackageLimits.MaximumArchiveBytes)
                    return InvalidPayload();
            }

            cancellationToken.ThrowIfCancellationRequested();
            confirmation = confirm(context.CallerPluginId,
                AppText.Get(fromMarket ? "PluginRpcInstallMarketplaceSource" : "PluginRpcInstallLocalSource", source));
            var approved = await confirmation.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!approved)
                return PluginRpcDispatchResult.Fail("plugin_install_declined", AppText.Get("PluginRpcInstallDeclined"));
            if (!CanInstall(context))
                return PluginRpcDispatchResult.Fail(PluginRpcErrors.Unavailable, AppText.Get("PluginRpcInstallUnavailable"));

            var id = Guid.NewGuid();
            var accepted = new QuantumPluginInstallReceipt(id.ToString("N"), "pending", null);
            lock (_operationsLock)
            {
                if (_operationOrder.Count == 64)
                    _operations.Remove(_operationOrder.Dequeue());
                _operations.Add(id, (context.CallerPluginId, accepted));
                _operationOrder.Enqueue(id);
            }

            // Installation drains the old runtimes, including the handler that may be awaiting this RPC.
            // Keep the approved work owned by the host after returning the receipt.
            _ = Task.Run(() => InstallAsync(id, context.CallerPluginId, fromMarket, source, archive));
            queued = true;
            return Receipt(accepted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "RPC installation request failed for {Source} requested by {PluginId}.", source, context.CallerPluginId);
            return PluginRpcDispatchResult.Fail("plugin_install_failed", AppText.Get("PluginRpcInstallFailed"));
        }
        finally
        {
            if (!queued)
            {
                // A cancelled RPC can leave the native dialog open. Do not show another until it closes.
                if (confirmation is { IsCompleted: false })
                {
                    _ = ReleaseAfterConfirmationAsync(confirmation);
                }
                else
                {
                    Volatile.Write(ref _installing, 0);
                }
            }
        }
    }

    private async Task ReleaseAfterConfirmationAsync(Task<bool> confirmation)
    {
        try
        {
            await confirmation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The plugin installation confirmation closed with an error.");
        }
        finally
        {
            Volatile.Write(ref _installing, 0);
        }
    }

    private async Task InstallAsync(Guid operationId, string callerId, bool fromMarket, string source, byte[]? archive)
    {
        var cancellationToken = CancellationToken.None;
        PluginOperationResult result;
        try
        {
            if (fromMarket)
            {
                var details = await marketplace.GetPluginAsync(source, cancellationToken).ConfigureAwait(false);
                result = string.Equals(details.Plugin.PluginId, source, StringComparison.Ordinal)
                    ? await marketplaceInstaller.InstallAsync(details.Plugin, cancellationToken).ConfigureAwait(false)
                    : PluginOperationResult.Failure(AppText.Get("MarketplacePackageIdentityMismatch"));
            }
            else
            {
                using var stream = new MemoryStream(archive!, writable: false);
                var preview = await runtime.PrepareInstallAsync(stream, source, cancellationToken).ConfigureAwait(false);
                try
                {
                    result = preview.CanInstall && preview.PreviewId is { } id
                        ? await runtime.InstallAsync(id, cancellationToken).ConfigureAwait(false)
                        : PluginOperationResult.Failure(preview.Issues.Count == 0
                            ? AppText.Get("LocalPreflightFailed")
                            : string.Join(" ", preview.Issues.Select(issue => issue.Reason)));
                }
                finally
                {
                    if (preview.PreviewId is { } id)
                        await runtime.CancelInstallAsync(id, CancellationToken.None).ConfigureAwait(false);
                }
            }

        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "RPC installation failed for {Source} requested by {PluginId}.", source, callerId);
            result = PluginOperationResult.Failure(AppText.Get("PluginRpcInstallFailed"));
        }

        lock (_operationsLock)
            _operations[operationId] = (callerId, new QuantumPluginInstallReceipt(operationId.ToString("N"),
                result.Succeeded ? "succeeded" : "failed", result.Message));
        Volatile.Write(ref _installing, 0);
    }

    private static PluginRpcDispatchResult Receipt(QuantumPluginInstallReceipt receipt)
        => PluginRpcDispatchResult.Success(JsonSerializer.SerializeToElement(Result.Success(receipt), JsonOptions));

    private bool CanInstall(PluginRpcCallContext context)
        => runtime.CanAcceptInstallationRpc
            && Guid.TryParseExact(context.CallerRuntimeId, "N", out var runtimeId)
            && catalog.Plugins.Any(plugin => plugin.RuntimeId == runtimeId
                && string.Equals(plugin.Manifest.Id.ToString(), context.CallerPluginId, StringComparison.Ordinal));

    private static string? ReadString(JsonElement payload, string name)
        => payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static PluginRpcDispatchResult InvalidPayload()
        => PluginRpcDispatchResult.Fail(PluginRpcErrors.InvalidPayload, AppText.Get("PluginRpcInstallInvalidPayload"));
}
