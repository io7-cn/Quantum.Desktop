using Microsoft.AspNetCore.Components.WebView.Maui;
using Quantum.Components;
using Quantum.Plugins;

namespace Quantum;

public sealed class MainPage : ContentPage
{
    public MainPage(PluginStaticAssetFileProvider pluginAssets)
    {
        Title = string.Empty;
        BackgroundColor = Color.FromArgb("#FFFFFF");

        var blazorWebView = new PluginBlazorWebView(pluginAssets)
        {
            HostPage = "wwwroot/index.html"
        };
        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(Routes)
        });

#if WINDOWS
        blazorWebView.BlazorWebViewInitializing += (_, args) =>
            args.UserDataFolder = DesktopAppStorage.Paths.BrowserDirectory;
        blazorWebView.BlazorWebViewInitialized += (_, args) =>
            DesktopAppStorage.RequireSamePath(args.WebView.CoreWebView2.Environment.UserDataFolder,
                DesktopAppStorage.Paths.BrowserDirectory);
#elif MACCATALYST
        if (Quantum.Storage.DesktopBuildProfile.Current.IsPreview)
        {
            blazorWebView.BlazorWebViewInitializing += (_, args) =>
            {
                if (Foundation.NSBundle.MainBundle.BundleIdentifier != Quantum.Storage.DesktopBuildProfile.Current.ApplicationId)
                {
                    throw new InvalidOperationException("The preview WebKit store requires the preview bundle identity.");
                }

                args.Configuration ??= new WebKit.WKWebViewConfiguration();
                args.Configuration.WebsiteDataStore = OperatingSystem.IsMacCatalystVersionAtLeast(17)
                    ? WebKit.WKWebsiteDataStore.Create(new Foundation.NSUuid("a460cb56-69f0-4edf-b44d-c8bdfd79b49f"))
                    : WebKit.WKWebsiteDataStore.NonPersistentDataStore;
            };
        }
#endif

        Content = blazorWebView;
    }
}
