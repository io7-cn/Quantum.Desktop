using Quantum.Plugin.Abstraction;
using System.Text.Json;

namespace Quantum.ExamplePlugin;

/// <summary>Owns the example's registrations and demonstrates updating and withdrawing data.</summary>
public sealed class ExampleExtensions(IQuantumExtensions extensions)
{
    private readonly object _gate = new();
    private IDisposable? _point;
    private IDisposable? _page;
    private IQuantumContribution? _message;
    private int _sequence;

    public bool HasMessage
    {
        get
        {
            lock (_gate)
            {
                return _message is not null;
            }
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            _sequence = 0;
            _point = extensions.Define("example-data", 1);
            _message = extensions.Register("quantum.plugin.example/example-data", 1, "message", Message());
            using var page = JsonDocument.Parse("""
                {
                  "title": "Example plugin",
                  "titles": { "zh-CN": "示例插件" },
                  "component": "Quantum.ExamplePlugin.ExampleSettings"
                }
                """);
            _page = extensions.Register("quantum.settings/pages", 1, "example-settings", page.RootElement);
        }
    }

    public void UpdateMessage()
    {
        lock (_gate)
        {
            _sequence++;
            _message?.Update(Message());
        }
    }

    public void ToggleMessage()
    {
        lock (_gate)
        {
            if (_message is not null)
            {
                _message.Dispose();
                _message = null;
            }
            else if (_point is not null)
            {
                _message = extensions.Register("quantum.plugin.example/example-data", 1, "message", Message());
            }
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _message?.Dispose();
            _message = null;
            _page?.Dispose();
            _page = null;
            _point?.Dispose();
            _point = null;
        }
    }

    private JsonElement Message() => JsonSerializer.SerializeToElement(new Dictionary<string, string>
    {
        ["message"] = $"Message from the .NET example ({_sequence})",
        ["messageZh"] = $"来自 .NET 示例的消息（{_sequence}）"
    });
}
