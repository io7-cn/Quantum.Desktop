using Quantum.Storage;
using System.Reflection;

namespace Quantum;

/// <summary>Configures native storage before the UI, WebView, preferences or plugin host starts.</summary>
internal static class DesktopAppStorage
{
    public static DesktopStoragePaths Paths { get; } = CreatePaths();

    public static void ConfigureProcess()
    {
        var profile = DesktopBuildProfile.Current;
#if WINDOWS
        // AppInfo.Current constructs a dispatcher-aware object. At this point WinUI
        // has not started; use the generated native manifest metadata instead.
        var metadata = typeof(DesktopAppStorage).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        if (metadata["Microsoft.Maui.ApplicationModel.AppInfo.PackageName"] != profile.ApplicationId
            || metadata["Microsoft.Maui.ApplicationModel.AppInfo.PublisherName"] != profile.Publisher)
        {
            throw new InvalidOperationException("The native application identity does not match its build profile.");
        }
#elif MACCATALYST
        if (Foundation.NSBundle.MainBundle.BundleIdentifier != profile.ApplicationId)
        {
            throw new InvalidOperationException("The native bundle identity does not match its build profile.");
        }
#endif
        if (profile.IsPreview)
        {
            // A linked settings directory would defeat MAUI's identity-based preference isolation.
#if WINDOWS
            DesktopDataMigration.RejectLinks(Path.Combine(Path.GetDirectoryName(Paths.DataDirectory)!, "Settings"));
#endif
            Paths.Prepare();
            Environment.SetEnvironmentVariable("TEMP", Paths.TemporaryDirectory);
            Environment.SetEnvironmentVariable("TMP", Paths.TemporaryDirectory);
            Environment.SetEnvironmentVariable("TMPDIR", Paths.TemporaryDirectory + Path.DirectorySeparatorChar);
        }

#if WINDOWS
        // MAUI 10 can initialize WinUI WebView2 without the configured environment.
        // Apply the profile before either environment or control can be created.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Paths.BrowserDirectory);
        if (profile.IsPreview)
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", null);
            Environment.SetEnvironmentVariable("WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER", null);
        }
#endif
    }

    public static void VerifyNativeStorage()
    {
#if WINDOWS
        RequireSamePath(FileSystem.AppDataDirectory, Paths.DataDirectory);
        RequireSamePath(FileSystem.CacheDirectory, Paths.CacheDirectory);
#endif
    }

    public static void RequireSamePath(string actual, string expected)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Storage profile mismatch: '{actual}', expected '{expected}'.");
        }
    }

    private static DesktopStoragePaths CreatePaths()
    {
#if WINDOWS
        return DesktopStoragePaths.ForWindows(DesktopBuildProfile.Current,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
#else
        return DesktopStoragePaths.ForMac(DesktopBuildProfile.Current, FileSystem.AppDataDirectory, FileSystem.CacheDirectory);
#endif
    }
}
