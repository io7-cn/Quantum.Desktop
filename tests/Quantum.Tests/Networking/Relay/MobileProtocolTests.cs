using Quantum.Mobile.Internal;
using Xunit;

namespace Quantum.Tests.Networking.Relay;

public sealed class MobileProtocolTests
{
    [Fact]
    public void InternalProtocolIsIndependentOfPluginsAndRejectsExecutableWireActions()
    {
        Assert.DoesNotContain(typeof(MobileJson).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("NOF") || a.Name.Contains("Plugin"));
        var page = Quantum.Mobile.Internal.Mobile.Page("p", "Page",
            Quantum.Mobile.Internal.Mobile.Button("save", "Save").OnClick(
                Quantum.Mobile.Internal.Mobile.Rpc("plugin.notes.create")));
        Assert.Throws<ArgumentException>(() => Quantum.Mobile.Internal.MobileUiValidator.Validate(page, wire: true));
    }
}
