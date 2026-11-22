using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NOF.Hosting;
using NOF.Hosting.Maui;
using Quantum.Components;
using Quantum.Logging;
using Quantum.Localization;
using Quantum.Marketplace;
using Quantum.Mobile.Internal;
using Quantum.Plugins;
using Quantum.Storage;
using Quantum.Updates;
using Quantum.WebPlugins;

namespace Quantum;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        DesktopAppStorage.VerifyNativeStorage();
#if MACCATALYST
        if (!DesktopBuildProfile.Current.IsPreview)
        {
            DesktopDataMigration.MigrateModules(
                DesktopAppStorage.Paths.ModulesDirectory,
                Path.Combine(AppContext.BaseDirectory, "Modules"),
                Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Contents", "Resources", "Modules"));
        }
#endif
        var loggingOptions = QuantumLoggingOptions.Parse(
            Environment.GetCommandLineArgs(),
            DesktopAppStorage.Paths.DataDirectory);
        var consoleOutputRequested = loggingOptions.WriteToConsole;
#if WINDOWS
        if (consoleOutputRequested && !WindowsConsole.TryEnable())
        {
            loggingOptions = loggingOptions with { WriteToConsole = false };
        }
#endif
        var builder = NOFMauiAppBuilder.Create();
        QuantumLogging.Configure(builder.Logging, loggingOptions);
        var (runtimeOptions, locationFailures) = ResolvePluginRuntimeOptions();

        builder.AddApplicationPart(typeof(MauiProgram).Assembly);

        builder.MauiAppBuilder
            .UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        if (DesktopBuildProfile.Current.IsPreview)
        {
            builder.Services.AddSingleton<IAppUpdateBackend, PreviewUpdateBackend>();
        }
        else
        {
#if MACCATALYST
            builder.Services.AddSingleton<IAppUpdateBackend, MacAppUpdateBackend>();
            builder.Services.AddSingleton<IAppUninstallService, MacAppUninstallService>();
#else
            builder.Services.AddSingleton<IAppUpdateBackend, VelopackUpdateBackend>();
#endif
        }
        builder.Services.AddSingleton<Quantum.Updates.AppUpdateService>();
        builder.Services.AddLocalization();
        var requestedCulture = Preferences.Default.Get(
            CultureService.PreferenceKey,
            System.Globalization.CultureInfo.CurrentUICulture.Name);
        builder.Services.AddSingleton(new CultureService(
            requestedCulture,
            cultureName => Preferences.Default.Set(CultureService.PreferenceKey, cultureName)));
        builder.Services.AddSingleton(new PluginNavigationOrder(
            Preferences.Default.Get(PluginNavigationOrder.PreferenceKey, "[]"),
            order => Preferences.Default.Set(PluginNavigationOrder.PreferenceKey, order)));
        builder.Services.AddSingleton(
            typeof(IStringLocalizer<>),
            typeof(CultureAwareStringLocalizer<>));
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSingleton<PluginCatalog>(services => new PluginCatalog(
            [],
            locationFailures,
            services.GetRequiredService<ILogger<PluginCatalog>>()));
        builder.Services.AddSingleton<IQuantumPluginEnvironment>(services =>
            services.GetRequiredService<PluginCatalog>());
        builder.Services.AddSingleton<PluginRpcRouter>();
        builder.Services.AddQuantumPluginEventBus();
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton<IPluginReferenceRelease, BlazorPluginReferenceRelease>();
        builder.Services.AddSingleton<PluginRuntimeManager>(services => new PluginRuntimeManager(
            services.GetRequiredService<PluginCatalog>(),
            runtimeOptions,
            services.GetRequiredService<IPluginReferenceRelease>(),
            logger: services.GetRequiredService<ILogger<PluginRuntimeManager>>()));
        builder.Services.AddSingleton<IPluginRuntimeManager>(services =>
            services.GetRequiredService<PluginRuntimeManager>());
        var marketplaceOptions = QuantumMarketplaceOptions.FromEnvironment();
        builder.Services.AddSingleton(marketplaceOptions);
        var platformClient = marketplaceOptions.IsOffline
            ? new HttpClient(new OfflinePlatformHttpHandler()) : new HttpClient();
        platformClient.Timeout = TimeSpan.FromMinutes(5);
        builder.Services.AddSingleton(platformClient);
        var sessionStorageKey = "quantum.marketplace.session." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(marketplaceOptions.BaseAddress.AbsoluteUri)));
        builder.Services.AddSingleton(new QuantumMarketplaceSession(
            async () => await SecureStorage.Default.GetAsync(sessionStorageKey) ??
                (marketplaceOptions.BaseAddress.AbsoluteUri == PlatformAddress.DefaultUrl
                    ? await SecureStorage.Default.GetAsync("quantum.marketplace.access-token") : null),
            async token =>
            {
                await SecureStorage.Default.SetAsync(sessionStorageKey, token);
                SecureStorage.Default.Remove("quantum.marketplace.access-token");
            },
            () =>
            {
                SecureStorage.Default.Remove(sessionStorageKey);
                SecureStorage.Default.Remove("quantum.marketplace.access-token");
                return Task.CompletedTask;
            }));
        builder.Services.AddSingleton<IQuantumMarketplaceClient, QuantumMarketplaceClient>();
        builder.Services.AddSingleton<MarketplacePluginInstaller>();
        builder.Services.AddSingleton<PluginInstallationRpc>();
        builder.Services.AddSingleton<ConfirmPluginInstallation>(_ => (callerId, source) =>
            MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = Application.Current?.Windows.FirstOrDefault()?.Page;
                return page is not null && await page.DisplayAlertAsync(
                    AppText.Get("PluginRpcInstallTitle"),
                    AppText.Get("PluginRpcInstallPrompt", callerId, source),
                    AppText.Get("ConfirmInstall"), AppText.Get("Cancel"));
            }));
        builder.Services.AddSingleton<Quantum.Mobile.MobileActionGateway>();
        string DesktopRegistrationKey(string user) => "quantum.mobile.desktop." + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(marketplaceOptions.BaseAddress.AbsoluteUri + "\n" + user)));
        builder.Services.AddSingleton(services => new Quantum.Mobile.MobileDesktopBridge(
            services.GetRequiredService<HttpClient>(),
            services.GetRequiredService<QuantumMarketplaceSession>(), marketplaceOptions,
            services.GetRequiredService<Quantum.Mobile.MobileActionGateway>(),
            Path.Combine(DesktopAppStorage.Paths.DataDirectory, "MobileBridgeJournal"),
            user => SecureStorage.Default.GetAsync(DesktopRegistrationKey(user)),
            (user, value) => SecureStorage.Default.SetAsync(DesktopRegistrationKey(user), value),
            user => SecureStorage.Default.Remove(DesktopRegistrationKey(user))));
        builder.Services.AddSingleton<PluginStaticAssetFileProvider>();
        builder.Services.AddScoped<WebPluginInteropBridge>();
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<IComponentActivator, PluginComponentActivator>();
        builder.Services.AddInitializationStep(new PluginLifecycleInitializationStep());

        var nofApp = builder.BuildAsync().GetAwaiter().GetResult();
        nofApp.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Quantum.Startup")
            .LogInformation(
                "Logging initialized. Process log file starts at {LogFilePath}; "
                + "console requested: {ConsoleOutputRequested}; console enabled: {ConsoleOutputEnabled}.",
                loggingOptions.GetFilePath(segmentIndex: 0),
                consoleOutputRequested,
                loggingOptions.WriteToConsole);
        nofApp.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Quantum.Startup")
            .LogInformation("Build profile: {Profile}; identity: {Identity}; data: {Data}; cache: {Cache}; platform offline: {Offline}.",
                DesktopBuildProfile.Current.Name, AppInfo.Current.PackageName,
                DesktopAppStorage.Paths.DataDirectory, DesktopAppStorage.Paths.CacheDirectory, marketplaceOptions.IsOffline);
        return nofApp.MauiApp;
    }

    private static (PluginRuntimeOptions Options, IReadOnlyList<PluginLoadFailure> Failures)
        ResolvePluginRuntimeOptions()
    {
        var configuredPath = Environment.GetEnvironmentVariable("QUANTUM_MODULES_PATH");
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Modules");
        var applicationDataPath = DesktopAppStorage.Paths.ModulesDirectory;
        var databasePath = DesktopAppStorage.Paths.DatabasePath;
#if DEBUG
        const bool IsDebug = true;
#else
        const bool IsDebug = false;
#endif
        var preferredPath = DesktopAppStorage.Paths.ResolveModulesDirectory(
            DesktopBuildProfile.Current, configuredPath, bundledPath, IsDebug);

        try
        {
            Directory.CreateDirectory(preferredPath);
            return (
                new PluginRuntimeOptions(
                    preferredPath,
                    DesktopAppStorage.Paths.ShadowDirectory,
                    databasePath),
                []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (string.Equals(preferredPath, applicationDataPath, StringComparison.Ordinal))
            {
                return (
                    new PluginRuntimeOptions(
                        applicationDataPath,
                        DesktopAppStorage.Paths.ShadowDirectory,
                        databasePath),
                    [new PluginLoadFailure(
                        null,
                        $"Plugin directory '{preferredPath}' is not accessible.",
                        exception)]);
            }

            Directory.CreateDirectory(applicationDataPath);
            return (
                    new PluginRuntimeOptions(
                        applicationDataPath,
                        DesktopAppStorage.Paths.ShadowDirectory,
                        databasePath),
                [new PluginLoadFailure(
                    null,
                    $"Plugin directory '{preferredPath}' is not accessible; using application data instead.",
                    exception)]);
        }
    }

}
