using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Quantum.Components;
using Quantum.Localization;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class NavigationOrderSettingsTests
{
    [Theory]
    [InlineData(0, 2, true, "B,C,A")]
    [InlineData(2, 0, false, "C,A,B")]
    [InlineData(0, 1, false, "A,B,C")]
    public async Task PointerDropUsesCurrentEntryKeysAndCorrectInsertionPosition(int source, int target, bool after, string expected)
    {
        var routes = new[] { Route("A"), Route("B"), Route("C") };
        var order = new PluginNavigationOrder();
        var activator = new CapturingActivator();
        await using var services = Services(Catalog(routes), order, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<NavigationOrderSettings>();
            await activator.Settings!.MoveByPointerAsync(PluginNavigationOrder.GetKey(routes[source]), PluginNavigationOrder.GetKey(routes[target]), after);
            Assert.Equal(expected, string.Join(",", order.Apply(routes).Select(route => route.Definition.Path.TrimStart('/'))));
        });
    }

    [Fact]
    public async Task DropIsIgnoredWhenAnEntryDisappearsOrTheComponentIsDisposed()
    {
        var routes = new[] { Route("A"), Route("B"), Route("C") };
        var catalog = Catalog(routes);
        var writes = 0;
        var order = new PluginNavigationOrder(savePreference: _ => writes++);
        var activator = new CapturingActivator();
        await using var services = Services(catalog, order, activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<NavigationOrderSettings>();
            catalog.Replace(Catalog([routes[0], routes[2]]).Plugins);
            await activator.Settings!.MoveByPointerAsync(PluginNavigationOrder.GetKey(routes[0]), PluginNavigationOrder.GetKey(routes[1]), true);
            Assert.Equal(0, writes);
            await activator.Settings.DisposeAsync();
            await activator.Settings.MoveByPointerAsync(PluginNavigationOrder.GetKey(routes[2]), PluginNavigationOrder.GetKey(routes[0]), false);
            Assert.Equal(0, writes);
        });
    }

    private static ServiceProvider Services(PluginCatalog catalog, PluginNavigationOrder order, CapturingActivator activator)
        => new ServiceCollection().AddLogging().AddLocalization().AddSingleton(catalog).AddSingleton(order)
            .AddSingleton(new CultureService("en-US")).AddSingleton<IJSRuntime, NoOpJavaScript>()
            .AddSingleton<IComponentActivator>(activator).BuildServiceProvider();

    private static PluginRouteRegistration Route(string name)
        => new("example", new PluginRouteDefinition($"/{name}", "Example.Page", title: name), typeof(NavigationOrderSettingsTests));

    private static PluginCatalog Catalog(IReadOnlyList<PluginRouteRegistration> routes)
        => new([new LoadedPlugin(new PluginManifest("example", "1.0.0", "Example.dll"), ".", typeof(NavigationOrderSettingsTests).Assembly, routes)]);

    private sealed class CapturingActivator : IComponentActivator
    {
        public NavigationOrderSettings? Settings { get; private set; }

        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is NavigationOrderSettings settings)
            {
                Settings = settings;
            }

            return component;
        }
    }

    private sealed class NoOpJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
            => ValueTask.FromResult(default(T)!);
    }
}
