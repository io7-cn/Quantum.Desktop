using NOF.Contract;
using Quantum.Plugin.Abstraction;

namespace Quantum.ExamplePlugin;

public sealed class CreateDependencyGreeting(ExamplePluginState state)
    : ExamplePluginRpcService.CreateDependencyGreeting
{
    public override Task<Result<string>> HandleAsync(
        Empty request,
        Context context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context[QuantumRpcContextKeys.CallerPluginId] is not string callerPluginId
            || string.IsNullOrWhiteSpace(callerPluginId))
        {
            return Task.FromResult<Result<string>>(Result.Fail(
                "rpc_caller_missing",
                "Quantum did not provide the calling plugin id."));
        }

        return Task.FromResult<Result<string>>(
            state.CreateDependencyGreeting(callerPluginId));
    }
}

public sealed class CreateWebHandshake(ExamplePluginState state)
    : ExamplePluginRpcService.CreateWebHandshake
{
    public override async Task<Result<ExamplePluginHandshake>> HandleAsync(
        Empty request,
        Context context,
        CancellationToken cancellationToken)
    {
        if (context[QuantumRpcContextKeys.CallerPluginId] is not string callerPluginId
            || string.IsNullOrWhiteSpace(callerPluginId))
        {
            return Result.Fail(
                "rpc_caller_missing",
                "Quantum did not provide the calling plugin id.");
        }

        return await state
            .CreateWebHandshakeAsync(callerPluginId, cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class ExampleSuggestionContributor : ExamplePluginRpcService.Suggest
{
    public override Task<QuantumRequestResult<string>> HandleAsync(
        ExampleSuggestionRequest request,
        Context context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Topic != "getting-started")
        {
            return Task.FromResult(QuantumRequestResult<string>.Pass());
        }

        return Task.FromResult(QuantumRequestResult<string>.AnswerAndStop(
            "Open the .NET example to see how many Web handshakes it has received."));
    }
}
