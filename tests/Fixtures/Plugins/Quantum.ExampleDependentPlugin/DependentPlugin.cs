using Microsoft.Extensions.DependencyInjection;
using NOF.Contract;
using Quantum.Plugin.Abstraction;

namespace Quantum.ExampleDependentPlugin;

/// <summary>
/// Demonstrates that a required capability is available before this plugin starts.
/// </summary>
public sealed class DependentPlugin : IQuantumPlugin
{
    internal const string GreetingCapabilityName = "example.greet";

    public static async Task StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var greetingResult = await services
            .GetRequiredService<IQuantumRequester>()
            .RequestSingleAsync<string>(
                GreetingCapabilityName,
                new Empty(),
                Context.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        if (!greetingResult.IsSuccess)
        {
            throw new InvalidOperationException(
                $"RPC '{GreetingCapabilityName}' failed: {greetingResult.ErrorCode} {greetingResult.Message}");
        }

        services.GetRequiredService<DependentPluginState>().Start(
            DateTimeOffset.Now,
            greetingResult.Value);
    }

    public static Task StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        services.GetRequiredService<DependentPluginState>().Stop();
        return Task.CompletedTask;
    }
}
