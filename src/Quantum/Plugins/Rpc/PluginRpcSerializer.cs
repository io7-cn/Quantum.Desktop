using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Quantum.Plugins;

internal sealed class PluginRpcSerializer : IDisposable
{
    private readonly JsonSerializerOptions _options = QuantumJsonDefaults.CreatePluginOptions();
    internal JsonSerializerOptions Options => _options;
    private int _disposed;

    public JsonNode GetSchema(Type type, JsonSchemaExporterOptions exporterOptions)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _options.GetJsonSchemaAsNode(type, exporterOptions);
    }

    public JsonElement Serialize(object? value, Type inputType)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return JsonSerializer.SerializeToElement(value, inputType, _options);
    }

    public object? Deserialize(JsonElement payload, Type returnType)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return payload.Deserialize(returnType, _options);
    }

    public T? Deserialize<T>(JsonElement payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return payload.Deserialize<T>(_options);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        QuantumJsonDefaults.ClearPluginCaches(_options);
    }
}
