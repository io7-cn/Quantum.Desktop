using Quantum.Plugins;
using System.Text.Json;

namespace Quantum.Components;

public sealed class PluginNavigationOrder
{
    public const string PreferenceKey = "Quantum.NavigationOrder";

    private readonly Action<string>? _savePreference;
    private string[] _keys;

    public PluginNavigationOrder(string? preference = null, Action<string>? savePreference = null)
    {
        _savePreference = savePreference;
        try
        {
            _keys = (JsonSerializer.Deserialize<string[]>(preference ?? "[]") ?? [])
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            _keys = [];
        }
    }

    public bool HasCustomOrder => _keys.Length > 0;

    public event EventHandler? Changed;

    public IReadOnlyList<PluginRouteRegistration> Apply(IReadOnlyList<PluginRouteRegistration> routes)
    {
        var positions = _keys.Select((key, index) => (key, index))
            .ToDictionary(entry => entry.key, entry => entry.index, StringComparer.Ordinal);
        return routes.OrderBy(route => positions.GetValueOrDefault(GetKey(route), int.MaxValue)).ToArray();
    }

    public bool Move(IReadOnlyList<PluginRouteRegistration> routes, string key, int targetIndex)
    {
        var ordered = Apply(routes).Select(GetKey).ToList();
        var sourceIndex = ordered.IndexOf(key);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= ordered.Count || sourceIndex == targetIndex)
        {
            return false;
        }

        ordered.RemoveAt(sourceIndex);
        ordered.Insert(targetIndex, key);
        var visible = ordered.ToHashSet(StringComparer.Ordinal);
        var remaining = new Queue<string>(ordered);
        // Keep absent entries in their saved slots, so disabling a plugin does not lose its position.
        var next = _keys.Select(saved => visible.Contains(saved) ? remaining.Dequeue() : saved).ToList();
        next.AddRange(remaining);
        Save(next.ToArray());
        return true;
    }

    public void Reset() => Save([]);

    public static string GetKey(PluginRouteRegistration route) => $"{route.PluginId}:{route.Definition.Path}";

    private void Save(string[] keys)
    {
        _savePreference?.Invoke(JsonSerializer.Serialize(keys));
        _keys = keys;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
