using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Quantum.Components;
using Quantum.Localization;
using Quantum.Plugins;
using System.Reflection;
using System.Text.Json;

namespace Quantum.Tests;

public sealed class ExtensionOutletTests
{
    [Fact]
    public async Task OldObservationCannotOverwriteASwitchedBackPoint()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope("example.settings", Guid.NewGuid());
        catalog.Replace([new LoadedPlugin(new PluginManifest("example.settings", "1.0.0", "Example.dll"),
            ".", typeof(ExtensionTestPanel).Assembly, [], scope.RuntimeId)]);
        using var entry = scope.Register("quantum.settings/general", 1, "entry", Value("Original"));
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddLocalization().AddSingleton(catalog)
            .AddSingleton<IComponentActivator>(activator).AddSingleton<IErrorBoundaryLogger, BoundaryLogger>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root = default;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            root = await renderer.RenderComponentAsync<ExtensionOutlet>(Parameters("quantum.settings/general"));
            // Queue a callback from the old observation while the renderer is busy switching points.
            Task.Run(() => entry.Update(Value("Queued old value"))).GetAwaiter().GetResult();
            await activator.Outlet!.SetParametersAsync(Parameters("quantum.settings/accounts"));
            entry.Update(Value("Current value"));
            await activator.Outlet.SetParametersAsync(Parameters("quantum.settings/general"));
        });
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.Contains("Current value", root.ToHtmlString());
            Assert.DoesNotContain("Queued old value", root.ToHtmlString());
        });

        static ParameterView Parameters(string pointId) => ParameterView.FromDictionary(new Dictionary<string, object?> { ["PointId"] = pointId });
    }

    [Fact]
    public async Task SelectedWebContributionStaysSelectedWhenItsContentChanges()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope("example.web", Guid.NewGuid());
        catalog.Replace([new LoadedPlugin(new PluginManifest("example.web", "1.0.0", PluginRuntimeDefinition.Web("main.js")),
            ".", null, [], scope.RuntimeId)]);
        using var first = scope.Register("quantum.settings/accounts", 1, "first", JsonSerializer.SerializeToElement(new { title = "First", view = "first" }));
        using var second = scope.Register("quantum.settings/accounts", 1, "second", JsonSerializer.SerializeToElement(new { title = "Second", view = "second" }));
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddLocalization()
            .AddSingleton(catalog).AddSingleton(new CultureService("en-US"))
            .AddSingleton<IJSRuntime, NoOpJavaScript>().AddSingleton<IComponentActivator>(activator)
            .AddSingleton<IErrorBoundaryLogger, BoundaryLogger>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ExtensionOutlet>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["PointId"] = "quantum.settings/accounts" }));
            activator.Outlet!.SelectView("example.web", "example.web/second");
            Assert.Contains(">Second</h2>", root.ToHtmlString());
            Assert.Contains("aria-pressed=\"true\"", root.ToHtmlString());
            Assert.Contains("aria-pressed=\"false\"", root.ToHtmlString());
            second.Update(JsonSerializer.SerializeToElement(new { title = "Updated second", view = "second" }));
            Assert.Contains(">Updated second</h2>", root.ToHtmlString());
            Assert.DoesNotContain(">First</h2>", root.ToHtmlString());
            second.Dispose();
            Assert.Contains(">First</h2>", root.ToHtmlString());
        });
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public ExtensionOutlet? Outlet { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is ExtensionOutlet outlet)
            {
                Outlet = outlet;
            }
            return component;
        }
    }

    private sealed class NoOpJavaScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    [Fact]
    public async Task ExampleSettingsValidatesPayloadsAndReflectsUpdates()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope("quantum.plugin.example", Guid.NewGuid());
        catalog.Replace([new LoadedPlugin(new PluginManifest("quantum.plugin.example", "1.0.0", "Example.dll"),
            ".", typeof(Quantum.ExamplePlugin.ExampleSettings).Assembly, [], scope.RuntimeId)]);
        using var point = scope.Define("example-data", 1);
        using var valid = scope.Register("quantum.plugin.example/example-data", 1, "valid",
            JsonSerializer.SerializeToElement(new { message = "Visible sample message" }));
        using var invalid = scope.Register("quantum.plugin.example/example-data", 1, "invalid",
            JsonSerializer.SerializeToElement(new { message = 42 }));
        await using var pluginServices = new ServiceCollection()
            .AddSingleton<Quantum.Plugin.Abstraction.IQuantumExtensions>(scope)
            .AddSingleton<Quantum.ExampleDependentPlugin.DependentPluginState>()
            .AddSingleton<Quantum.ExamplePlugin.ExampleExtensions>().BuildServiceProvider();
        var manager = DispatchProxy.Create<IPluginRuntimeManager, PluginServiceResolver>();
        ((PluginServiceResolver)manager).Services = pluginServices;
        await using var services = new ServiceCollection().AddLogging().AddLocalization()
            .AddSingleton(manager).AddSingleton<IComponentActivator, PluginComponentActivator>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Quantum.ExamplePlugin.ExampleSettings>();
            Assert.Contains("Visible sample message", root.ToHtmlString());
            Assert.DoesNotContain("@onclick", root.ToHtmlString());
            Assert.Contains("quantum.plugin.example", root.ToHtmlString());
            Assert.DoesNotContain(": 42", root.ToHtmlString());
            valid.Update(JsonSerializer.SerializeToElement(new { message = "Updated sample message" }));
            Assert.Contains("Updated sample message", root.ToHtmlString());
            valid.Dispose();
            Assert.DoesNotContain("Updated sample message", root.ToHtmlString());
            var companion = await renderer.RenderComponentAsync<Quantum.ExampleDependentPlugin.DependentSettings>();
            Assert.NotEmpty(companion.ToHtmlString());
        });
    }

    [Fact]
    public async Task SettingsContributionsRenderUpdateAndDisappearWithTheirRuntime()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope("example.settings", Guid.NewGuid());
        catalog.Replace([new LoadedPlugin(new PluginManifest("example.settings", "1.0.0", "Example.dll"),
            ".", typeof(ExtensionTestPanel).Assembly, [], scope.RuntimeId)]);
        using var registration = scope.Register("quantum.settings/accounts", 1, "account", Value("Account"));
        await using var services = new ServiceCollection().AddLogging().AddLocalization()
            .AddSingleton(catalog).AddSingleton<IErrorBoundaryLogger, BoundaryLogger>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ExtensionOutlet>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["PointId"] = "quantum.settings/accounts" }));
            Assert.Contains("Account", root.ToHtmlString());
            Assert.Contains("Plugin content", root.ToHtmlString());
            registration.Update(Value("Updated account"));
            Assert.Contains("Updated account", root.ToHtmlString());
            catalog.Replace([]);
            Assert.DoesNotContain("Plugin content", root.ToHtmlString());
        });
    }

    [Fact]
    public async Task InvalidViewContributionsDoNotBreakSiblingViews()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope("example.settings", Guid.NewGuid());
        catalog.Replace([new LoadedPlugin(new PluginManifest("example.settings", "1.0.0", "Example.dll"),
            ".", typeof(ExtensionTestPanel).Assembly, [], scope.RuntimeId)]);
        using var valid = scope.Register("quantum.settings/general", 1, "valid", Value("Valid"));
        using var invalid = scope.Register("quantum.settings/general", 1, "invalid",
            JsonSerializer.SerializeToElement(new { title = "Invalid", component = "" }));
        await using var services = new ServiceCollection().AddLogging().AddLocalization()
            .AddSingleton(catalog).AddSingleton<IErrorBoundaryLogger, BoundaryLogger>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ExtensionOutlet>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["PointId"] = "quantum.settings/general" }));
            Assert.Contains("Plugin content", root.ToHtmlString());
            Assert.DoesNotContain("Invalid", root.ToHtmlString());
        });
    }

    [Fact]
    public async Task FailingComponentKeepsSiblingContributionsVisible()
    {
        var catalog = new PluginCatalog([]);
        using var scope = catalog.Extensions.CreateScope("example.settings", Guid.NewGuid());
        catalog.Replace([new LoadedPlugin(new PluginManifest("example.settings", "1.0.0", "Example.dll"),
            ".", typeof(ExtensionTestPanel).Assembly, [], scope.RuntimeId)]);
        using var valid = scope.Register("quantum.settings/general", 1, "valid", Value("Valid"));
        using var broken = scope.Register("quantum.settings/general", 1, "broken",
            JsonSerializer.SerializeToElement(new { title = "Broken", component = typeof(FailingExtensionPanel).FullName }));
        await using var services = new ServiceCollection().AddLogging().AddLocalization().AddSingleton(catalog)
            .AddSingleton<IErrorBoundaryLogger, BoundaryLogger>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ExtensionOutlet>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["PointId"] = "quantum.settings/general" }));
            Assert.Contains("Plugin content", root.ToHtmlString());
            Assert.Contains("role=\"alert\"", root.ToHtmlString());
            Assert.DoesNotContain("Plugin rendering failed", root.ToHtmlString());
        });
    }

    private static JsonElement Value(string title) => JsonSerializer.SerializeToElement(new
    {
        title,
        component = typeof(ExtensionTestPanel).FullName
    });

    private sealed class BoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }

    public class PluginServiceResolver : DispatchProxy
    {
        public IServiceProvider Services { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(IPluginRuntimeManager.GetPluginServices)
                ? Services : throw new NotSupportedException(method?.Name);
    }
}

public sealed class ExtensionTestPanel : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "Plugin content");
}

public sealed class FailingExtensionPanel : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => throw new InvalidOperationException("Plugin rendering failed.");
}
