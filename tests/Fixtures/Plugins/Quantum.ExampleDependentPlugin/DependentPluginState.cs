using Microsoft.Extensions.DependencyInjection;
using Quantum.Plugin.Abstraction;
using System.Text.Json;

namespace Quantum.ExampleDependentPlugin;

[AutoInject(
    ServiceLifetime.Singleton,
    RegisterTypes = [typeof(DependentPluginState)])]
public sealed class DependentPluginState
{
    private IQuantumContribution? _message;
    private IQuantumContribution? _settings;

    public DateTimeOffset? StartedAt { get; private set; }

    public bool IsRunning { get; private set; }

    public string? Greeting { get; private set; }

    internal void Start(
        DateTimeOffset startedAt,
        string greeting,
        IQuantumExtensions extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(greeting);
        StartedAt = startedAt;
        Greeting = greeting;
        IsRunning = true;
        using var message = JsonDocument.Parse("""
            { "message": "Hello from the companion plugin", "messageZh": "来自依赖示例插件的消息" }
            """);
        _message = extensions.Register("quantum.plugin.example/example-data", 1, "companion-message", message.RootElement, 10);
        using var settings = JsonDocument.Parse("""
            {
              "title": "Companion plugin",
              "titles": { "zh-CN": "依赖示例插件" },
              "component": "Quantum.ExampleDependentPlugin.DependentSettings"
            }
            """);
        _settings = extensions.Register("quantum.settings/general", 1, "companion-settings", settings.RootElement);
    }

    internal void Stop()
    {
        IsRunning = false;
        _settings?.Dispose();
        _settings = null;
        _message?.Dispose();
        _message = null;
    }
}
