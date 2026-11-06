# Extension points and settings

[简体中文](../zh-CN/extensions.md)

Desktop implements the SDK’s code-defined, type-independent JSON extension protocol. The host registry lives under `Plugins/Extensions`; it does not interpret business payloads. Plugins define and observe their own points through `IQuantumExtensions` or `context.extensions`, with no additional `plugin.json` fields. Definitions and contributions are owned by runtime generations, and only committed catalog generations are visible. Payloads are detached JSON and revisions are session-local ordering tokens. The settings module defines its own contract below; other consumers can define different JSON contracts.

## Settings view contract, version 1

| Point | Content |
| --- | --- |
| `quantum.settings/pages` | Settings tabs |
| `quantum.settings/general` | Sections appended to General |
| `quantum.settings/accounts` | Sections appended to Accounts |
| `quantum.settings/plugins` | Sections appended to Plugin management |

Each contribution is an object with `title` (fallback text), optional `titles` (BCP-47 culture/language keys), and either `component` (full .NET component name in the contributor's entry assembly) or `view` (Web mount view ID). Runtime kind determines which field is used. Unknown or invalid content is skipped, and component failures have a local error boundary. Titles prefer exact culture, language, then fallback. Ordering is numeric order, contributor plugin ID, then local ID. The host registers its three built-in tabs through the same mechanism and reserves the `quantum.settings` namespace. Their `builtin` and `titleKey` fields are host-private and cannot be supplied by another owner to replace core pages.

```csharp
using var page = JsonDocument.Parse("""
    { "title": "Example", "component": "Example.SettingsPanel" }
    """);
extensions.Register("quantum.settings/pages", 1, "settings", page.RootElement);
```

Account services use the same view contributions as other sections. The component owns its login flow and uses plugin services for its state and persistence. Credentials must not be put into contributions. No separate account-provider API or form language is introduced. A plugin can make its own component extensible by observing another point and rendering its contributions.

Settings tab links use `/settings?section=<plugin-id/local-id>`; existing `section=account`, `section=plugins` and `#account` links remain supported. When the selected contribution disappears the UI returns to General. .NET components receive their plugin service container through the existing activator; Web views use the existing sandboxed iframe and route/view mount contract. Locale changes remount views. The Web runtime currently hosts one mounted view per plugin at a time; multiple contributions from the same Web plugin must be selected rather than simultaneously mounted.

## Integration verification

The SDK and Desktop remain independent repositories. Desktop pins its SDK package version. To test a locally built package, restore with `-p:RestoreAdditionalProjectSources=<package-directory>`; do not substitute sibling source references.

Validate SDK contracts, the Desktop .NET tests, `node --test tests/web-plugin-host.test.mjs`, and the native Windows build. macOS iframe rendering requires separate platform validation.

## Manual visual review

Run the native Debug host with the main and companion .NET fixtures. Open Settings → Example plugin: both plugins' messages should appear. Update, withdraw and restore the main message; the companion message remains. General settings includes the companion's section. Disable the companion plugin and verify both of its contributions disappear; re-enable it to restore them. With the SDK Web example installed, update its message from the Web example tab and verify the main example tab reflects the change.

Verify keyboard navigation and wrapped tabs at a narrow window width. Reload the main plugin while its tab is selected and check that only the current runtime's content remains. The SDK's `docs/en/extensions.md` describes the complete sample walkthrough and source files.
