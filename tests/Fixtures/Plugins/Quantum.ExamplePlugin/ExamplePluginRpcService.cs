using NOF.Application;
using NOF.Contract;
using Quantum.Plugin.Abstraction;
using System.ComponentModel;

namespace Quantum.ExamplePlugin;

[TransportOverQuantum]
public interface IExamplePluginRpcService : IRpcService
{
    [RequestCapability("example.greet", QuantumDispatch.Ordered, UniqueOwner = true, RequireSingleProvider = true)]
    Result<string> CreateDependencyGreeting(Empty request);

    [RequestCapability("example.handshake", QuantumDispatch.Ordered, UniqueOwner = true, RequireSingleProvider = true)]
    Result<ExamplePluginHandshake> CreateWebHandshake(Empty request);

    [RequestCapability("example.suggestions", QuantumDispatch.Ordered, AllowStop = true)]
    [Description("Contributes a tip for getting started with the Quantum examples.")]
    QuantumRequestResult<string> Suggest(ExampleSuggestionRequest request);
}

public partial class ExamplePluginRpcService : RpcServer<IExamplePluginRpcService>;

public sealed record ExampleSuggestionRequest(
    [property: Description("Topic of the requested suggestions. The example responds to 'getting-started'.")]
    string Topic);
