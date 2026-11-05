using Microsoft.Win32;
using Quantum.Storage;
using System.Runtime.InteropServices;
using Velopack.Locators;

namespace Quantum.WinUI;

internal static class WindowsDesktopStorage
{
    public static bool Initialize()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var destination = Path.Combine(local, "io7-cn", "io.quantum.desktop");
        try
        {
            RegisterUninstaller();
            var installation = VelopackLocator.Current.RootAppDir;
            if (installation is not null
                && (string.Equals(Path.GetFullPath(installation), destination, StringComparison.OrdinalIgnoreCase)
                    || destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation))
                        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("Choose an installation location that does not contain the Quantum user data directory.");
            }

            DesktopDataMigration.Migrate(
                Path.Combine(local, "Quantum", "io.quantum.desktop"),
                destination,
                Path.Combine(AppContext.BaseDirectory, "Modules"));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            var chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var message = chinese
                ? $"无法准备 Quantum 本地数据，已停止启动以保护原有数据。请备份旧数据并解决冲突后重试。\n\n{destination}\n\n{exception.Message}"
                : $"Quantum could not prepare local data. Startup stopped to protect existing data. Back up legacy data and resolve the conflict before retrying.\n\n{destination}\n\n{exception.Message}";
            MessageBox(IntPtr.Zero, message, "Quantum", 0x10);
            return false;
        }
    }

    internal static void RegisterUninstaller()
    {
        var locator = VelopackLocator.Current;
        if (locator.CurrentlyInstalledVersion is null || locator.IsPortable || locator.AppId != "Quantum")
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Quantum", writable: true);
        if (key is null || key.GetValue("InstallLocation") is not string installedRoot
            || !string.Equals(Path.GetFullPath(installedRoot), Path.GetFullPath(locator.RootAppDir!), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The Quantum uninstall registration does not match this installation.");
        }

        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "Uninstall.ps1");
        if (!File.Exists(script))
        {
            throw new IOException("The Quantum uninstall script is missing.");
        }

        var command = $"\"{shell}\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"";
        key.SetValue("UninstallString", command);
        key.SetValue("QuietUninstallString", command + " -Silent");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
