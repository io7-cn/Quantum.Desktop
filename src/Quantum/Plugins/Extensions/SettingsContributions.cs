using System.Text.Json;

namespace Quantum.Plugins;

/// <summary>The settings consumer's version-1 JSON view contract, outside the generic registry.</summary>
internal static class SettingsContributions
{
    public static void Install(PluginExtensionRegistry registry)
    {
        var host = registry.CreateHostScope("quantum.settings");
        foreach (var id in new[] { "pages", "general", "accounts", "plugins" })
        {
            host.Define(id, 1);
        }

        Add("general", "GeneralSettings", 0);
        Add("plugins", "PluginManagement", 10);
        Add("account", "AccountSettings", 20);

        void Add(string id, string titleKey, int order)
        {
            host.Register("quantum.settings/pages", 1, id,
                JsonSerializer.SerializeToElement(new { builtin = id, titleKey }), order);
        }
    }
}
