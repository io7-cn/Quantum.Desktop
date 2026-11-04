using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Quantum.Localization;
using Quantum.Plugins;

namespace Quantum.Components;

public partial class GlobalPluginInstaller : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _dropTargetId = Guid.NewGuid().ToString("N");
    private PluginInstallPreview? _pendingPreview;
    private string? _operationMessage;
    private bool _operationSucceeded;
    private bool _isProcessing;
    private bool _disposed;
    private int _fileInputKey;
    private ElementReference _dropOverlay;
    private InputFile? _packageInput;

    [Inject]
    internal IPluginRuntimeManager PluginRuntimeManager { get; set; } = null!;

    [Inject]
    internal IJSRuntime JSRuntime { get; set; } = null!;

    [Inject]
    private IStringLocalizer<AppStrings> L { get; set; } = null!;

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    public bool IsProcessing => _isProcessing;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_disposed)
        {
            await JSRuntime.InvokeVoidAsync(
                "quantum.pluginInstallerDropTarget.initialize",
                _dropTargetId,
                _dropOverlay);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _lifetime.Cancel();
            if (!_isProcessing)
            {
                await CancelPreviewAsync();
            }
        }
        finally
        {
            _lifetime.Dispose();
            try
            {
                await JSRuntime.InvokeVoidAsync("quantum.pluginInstallerDropTarget.dispose", _dropTargetId);
            }
            catch (JSDisconnectedException)
            {
                // The WebView has already released its listeners.
            }
        }
    }

    public async Task ChoosePackageAsync()
    {
        if (!_disposed && !_isProcessing && _packageInput is not null)
        {
            await JSRuntime.InvokeVoidAsync("quantum.pluginInstallerDropTarget.choosePackage", _packageInput.Element);
        }
    }

    public async Task PrepareInstallAsync(InputFileChangeEventArgs eventArgs)
    {
        if (_disposed || _isProcessing)
        {
            return;
        }

        _isProcessing = true;
        _operationMessage = null;
        CancellationToken cancellationToken = _lifetime.Token;
        try
        {
            await JSRuntime.InvokeVoidAsync("quantum.pluginInstallerDropTarget.hide", cancellationToken, _dropTargetId);
            await CancelPreviewAsync();
            cancellationToken.ThrowIfCancellationRequested();

            IBrowserFile file = eventArgs.File;
            await using Stream stream = file.OpenReadStream(PluginPackageLimits.MaximumArchiveBytes, cancellationToken);
            _pendingPreview = await PluginRuntimeManager.PrepareInstallAsync(stream, file.Name, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the component cancels its in-progress package scan.
        }
        catch (Exception exception) when (!_disposed)
        {
            _operationSucceeded = false;
            _operationMessage = L["CouldNotReadPluginZIPArg", exception.Message];
        }
        finally
        {
            // A scan can finish as the component closes. The active operation releases the preview it receives.
            if (_disposed)
            {
                await CancelPreviewAsync();
            }
            else
            {
                _fileInputKey++;
            }

            _isProcessing = false;
        }
    }

    private async Task CancelPreviewAsync()
    {
        string? previewId = _pendingPreview?.PreviewId;
        _pendingPreview = null;
        if (previewId is null)
        {
            return;
        }

        try
        {
            await PluginRuntimeManager.CancelInstallAsync(previewId);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Runtime shutdown also releases its pending preview.
        }
    }

    private async Task ConfirmInstallAsync()
    {
        PluginInstallPreview? preview = _pendingPreview;
        if (_disposed || _isProcessing || preview?.PreviewId is null || !preview.CanInstall)
        {
            return;
        }

        _isProcessing = true;
        _operationMessage = null;
        try
        {
            PluginOperationResult result = await PluginRuntimeManager.InstallAsync(preview.PreviewId);
            _operationSucceeded = result.Succeeded;
            _operationMessage = result.Message;
            _pendingPreview = null;
        }
        catch (Exception exception) when (!_disposed)
        {
            _operationSucceeded = false;
            _operationMessage = L["PluginInstallationFailedArg", exception.Message];
        }
        finally
        {
            _isProcessing = false;
            if (_disposed)
            {
                await CancelPreviewAsync();
            }
        }
    }

    private void DismissMessage()
    {
        _operationMessage = null;
    }

    private string GetInstallHeading(int count)
    {
        return count == 1
            ? L["ReadyToInstallPluginSingular", count]
            : L["ReadyToInstallPluginsPlural", count];
    }

    private string GetInstallVersionText(PluginInstallPreviewItem plugin)
    {
        return plugin.Action switch
        {
            PluginInstallAction.Install => $"v{plugin.PackageVersion}",
            PluginInstallAction.Upgrade => $"v{plugin.InstalledVersion} → v{plugin.PackageVersion}",
            _ => L[
                "KeepVArgPackageContainsVArg",
                plugin.InstalledVersion ?? string.Empty,
                plugin.PackageVersion]
        };
    }

    private string GetInstallActionText(PluginInstallAction action)
    {
        return action switch
        {
            PluginInstallAction.Install => L["InstallNewPlugin"],
            PluginInstallAction.Upgrade => L["Upgrade"],
            _ => L["Skip"]
        };
    }

    private static string GetInstallActionClass(PluginInstallAction action)
    {
        return action switch
        {
            PluginInstallAction.Install => "new",
            PluginInstallAction.Upgrade => "upgrade",
            _ => "skip"
        };
    }
}
