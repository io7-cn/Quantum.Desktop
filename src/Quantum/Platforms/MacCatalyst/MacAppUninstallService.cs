using Foundation;
using Microsoft.Extensions.Logging;
using Quantum.Localization;
using Quantum.Plugins;
using Quantum.Storage;
using Quantum.Updates;

namespace Quantum;

internal sealed class MacAppUninstallService(
    PluginRuntimeManager plugins,
    Quantum.Mobile.MobileDesktopBridge mobileBridge,
    AppUpdateService updates,
    ILogger<MacAppUninstallService> logger) : IAppUninstallService
{
    public bool IsAvailable => File.Exists(Path.Combine(
        NSBundle.MainBundle.BundlePath, "Contents", "Helpers", "Quantum.UpdateAgent", "Quantum.UpdateAgent"));
    public bool IsBusy { get; private set; }

    public Task RequestAsync() => MainThread.InvokeOnMainThreadAsync(RequestOnMainThreadAsync);

    private async Task RequestOnMainThreadAsync()
    {
        if (!IsAvailable || IsBusy)
        {
            return;
        }

        IsBusy = true;
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        try
        {
            if (page is null)
            {
                return;
            }

            var keep = AppText.Get("UninstallKeepData");
            var clear = AppText.Get("UninstallClearData");
            var choice = await page.DisplayActionSheetAsync(
                AppText.Get("UninstallQuantum"), AppText.Get("Cancel"), null, keep, clear);
            if (choice != keep && choice != clear)
            {
                return;
            }

            var removeData = choice == clear;
            if (removeData && !await page.DisplayAlertAsync(
                AppText.Get("UninstallQuantum"), AppText.Get("UninstallClearDataPrompt"),
                clear, AppText.Get("Cancel")))
            {
                return;
            }

            if (updates.IsBusy)
            {
                throw new IOException(AppText.Get("UninstallUpdateBusy"));
            }

            var bundle = NSBundle.MainBundle;
            if (bundle.BundleIdentifier != "io.quantum.desktop"
                || !bundle.BundlePath.EndsWith(".app", StringComparison.Ordinal))
            {
                throw new IOException("The application bundle does not match Quantum Desktop.");
            }

            DesktopDataMigration.RejectLinks(bundle.BundlePath);
            if (removeData)
            {
                MacLocalData.ValidateCleanup(MacLocalData.GetOwnedPaths(
                    FileSystem.AppDataDirectory, FileSystem.CacheDirectory));
            }

            using var url = NSUrl.FromFilename(bundle.BundlePath);
            if (!NSFileManager.DefaultManager.TrashItem(url, out var trashedUrl, out var error))
            {
                throw new IOException(error?.LocalizedDescription ?? "Could not move Quantum to Trash.");
            }

            trashedUrl?.Dispose();
            await mobileBridge.ShutdownAsync();
            await plugins.DisposeAsync();
            if (removeData)
            {
                await Task.Run(() => MacLocalData.Clear(FileSystem.AppDataDirectory, FileSystem.CacheDirectory));
                NSUserDefaults.StandardUserDefaults.RemovePersistentDomain("io.quantum.desktop");
                SecureStorage.Default.RemoveAll();
            }

            Application.Current!.Quit();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Quantum application uninstall did not complete.");
            if (page is not null)
            {
                await page.DisplayAlertAsync(AppText.Get("UninstallQuantum"),
                    AppText.Get("UninstallFailedArg", exception.Message), AppText.Get("Close"));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
