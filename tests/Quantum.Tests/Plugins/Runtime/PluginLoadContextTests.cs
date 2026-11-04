using System.Reflection;
using System.Runtime.Loader;
using Quantum.Plugins;

namespace Quantum.Tests;

public sealed class PluginLoadContextTests
{
    [Fact]
    public void Constructor_CreatesCollectibleIsolatedContext()
    {
        var assemblyPath = typeof(PluginLoadContextTests).Assembly.Location;
        var firstContext = new PluginLoadContext(assemblyPath);
        var secondContext = new PluginLoadContext(assemblyPath);

        var firstAssembly = firstContext.LoadFromAssemblyPath(assemblyPath);
        var secondAssembly = secondContext.LoadFromAssemblyPath(assemblyPath);

        Assert.True(firstContext.IsCollectible);
        Assert.NotSame(firstContext, secondContext);
        Assert.NotSame(firstAssembly, secondAssembly);
        Assert.Same(firstContext, AssemblyLoadContext.GetLoadContext(firstAssembly));
        Assert.Same(secondContext, AssemblyLoadContext.GetLoadContext(secondAssembly));

        firstContext.Unload();
        secondContext.Unload();
    }

    [Fact]
    public void OptionalFrameworkPackageLoadsPrivatelyWhileSdkIdentityRemainsShared()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "PrivateOptional", "System.Quantum.Optional.dll");
        var context = new PluginLoadContext(path);
        try
        {
            var optional = context.LoadFromAssemblyName(new AssemblyName("System.Quantum.Optional"));
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(optional));
            Assert.Equal("optional", optional.GetType("Quantum.Tests.Fixtures.OptionalMarker")!.GetMethod("Read")!.Invoke(null, null));
            var sdk = typeof(Quantum.Plugin.Abstraction.PluginId).Assembly;
            Assert.Same(sdk, context.LoadFromAssemblyName(sdk.GetName()));
        }
        finally
        {
            context.Unload();
        }
    }
}
