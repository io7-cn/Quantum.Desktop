using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quantum.Components;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class ExtensionOutletTests
{
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
        await using var services = new ServiceCollection().AddLogging().AddLocalization()
            .AddSingleton<Quantum.Plugin.Abstraction.IQuantumExtensions>(scope)
            .AddSingleton<Quantum.ExamplePlugin.ExampleExtensions>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<Quantum.ExamplePlugin.ExampleSettings>();
            Assert.Contains("Visible sample message", root.ToHtmlString());
            Assert.Contains("quantum.plugin.example", root.ToHtmlString());
            Assert.DoesNotContain(": 42", root.ToHtmlString());
            valid.Update(JsonSerializer.SerializeToElement(new { message = "Updated sample message" }));
            Assert.Contains("Updated sample message", root.ToHtmlString());
            valid.Dispose();
            Assert.DoesNotContain("Updated sample message", root.ToHtmlString());
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

    private static JsonElement Value(string title) => JsonSerializer.SerializeToElement(new
    {
        title,
        component = typeof(ExtensionTestPanel).FullName
    });

    private sealed class BoundaryLogger : IErrorBoundaryLogger
    {
        public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
    }
}

public sealed class ExtensionTestPanel : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "Plugin content");
}
