using Quantum.Components;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class PluginNavigationOrderTests
{
    [Fact]
    public void MovingAnEntryPersistsAndRestoresTheOrderWithoutChangingCatalogOrder()
    {
        var routes = new[] { Route("a"), Route("b"), Route("c") };
        string? saved = null;
        var order = new PluginNavigationOrder(savePreference: value => saved = value);

        Assert.True(order.Move(routes, PluginNavigationOrder.GetKey(routes[2]), 0));

        Assert.Equal([routes[2], routes[0], routes[1]], order.Apply(routes));
        Assert.Equal(["/a", "/b", "/c"], routes.Select(route => route.Definition.Path));
        var restored = new PluginNavigationOrder(saved);
        Assert.Equal(order.Apply(routes), restored.Apply(routes));
    }

    [Fact]
    public void AbsentEntriesKeepTheirSavedSlotsAndNewEntriesFollowSavedEntries()
    {
        var a = Route("a");
        var b = Route("b");
        var c = Route("c");
        var d = Route("d");
        var order = new PluginNavigationOrder();
        order.Move([a, b, c], PluginNavigationOrder.GetKey(c), 0);

        order.Move([a, c], PluginNavigationOrder.GetKey(a), 0);

        Assert.Equal([a, c, b, d], order.Apply([d, a, b, c]));
    }

    [Fact]
    public void PagesOfTheSamePluginCanBeOrderedIndependently()
    {
        var routes = new[] { Route("example", "/first"), Route("example", "/second") };
        var order = new PluginNavigationOrder();

        order.Move(routes, PluginNavigationOrder.GetKey(routes[1]), 0);

        Assert.Equal([routes[1], routes[0]], order.Apply(routes));
    }

    [Fact]
    public void ResetRestoresTheDefaultOrderAndPersistsIt()
    {
        var routes = new[] { Route("a"), Route("b") };
        string? saved = null;
        var order = new PluginNavigationOrder(savePreference: value => saved = value);
        order.Move(routes, PluginNavigationOrder.GetKey(routes[1]), 0);

        order.Reset();

        Assert.False(order.HasCustomOrder);
        Assert.Equal(routes, order.Apply(routes));
        Assert.Equal(routes, new PluginNavigationOrder(saved).Apply(routes));
    }

    [Theory]
    [InlineData("invalid JSON")]
    [InlineData("null")]
    [InlineData("[null, \"\", \"example:/first\", \"example:/first\"]")]
    public void MalformedAndDuplicatePreferencesDoNotBreakNavigation(string preference)
    {
        var routes = new[] { Route("example", "/first"), Route("other") };
        var order = new PluginNavigationOrder(preference);

        Assert.Equal(routes, order.Apply(routes));
    }

    [Fact]
    public void FailedPersistenceDoesNotChangeTheRunningOrderOrNotifyObservers()
    {
        var routes = new[] { Route("a"), Route("b") };
        var order = new PluginNavigationOrder(savePreference: _ => throw new IOException("unavailable"));
        var changes = 0;
        order.Changed += (_, _) => changes++;

        Assert.Throws<IOException>(() => order.Move(routes, PluginNavigationOrder.GetKey(routes[1]), 0));

        Assert.Equal(routes, order.Apply(routes));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void InvalidAndUnchangedMovesDoNotWritePreferences()
    {
        var routes = new[] { Route("a"), Route("b") };
        var order = new PluginNavigationOrder(savePreference: _ => throw new InvalidOperationException());

        Assert.False(order.Move(routes, "missing", 0));
        Assert.False(order.Move(routes, PluginNavigationOrder.GetKey(routes[0]), -1));
        Assert.False(order.Move(routes, PluginNavigationOrder.GetKey(routes[0]), 2));
        Assert.False(order.Move(routes, PluginNavigationOrder.GetKey(routes[0]), 0));
    }

    private static PluginRouteRegistration Route(string pluginId, string? path = null)
        => new(pluginId, new PluginRouteDefinition(path ?? $"/{pluginId}", "Example.Page"), typeof(PluginNavigationOrderTests));
}
