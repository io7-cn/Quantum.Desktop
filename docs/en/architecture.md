# Desktop architecture

[简体中文](../zh-CN/architecture.md)

## Components and startup

The host groups source by technical responsibility. `Shell` contains startup and the native window. `Components` separates routed pages, views, layouts, routing and hosting. `Networking` separates platform API calls and sessions from the device relay. `Plugins` contains catalog discovery, runtime, installation, assets, events, persistence, RPC, Web integration and remote view adapters. Platform entry points and metadata are under `Platforms`.

`Quantum.csproj` contains the MAUI entry point, Blazor UI, plugin model, loading and browser adapters. It builds the same plugin sources for plain `net10.0` tests and API reference generation. The public .NET ABI (the assembly-level compatibility boundary) is `Quantum.Plugin.Abstraction`. The SDK and host use strings for plugin IDs, semantic versions, version ranges and event topics. Validation and version comparisons use explicit helpers. Quantum Platform is separately deployed and exposes account, marketplace and mobile relay services via `/rpc`, outside the plugin ABI.

1. `NOFMauiAppBuilder` creates the application composition root. The host validates manifests, entries and migration files in `Modules`.
2. Hard `dependencies` require compatible providers and reject cycles and affected consumers. Compatible `integrations` affect ordering when possible. Plugins remain free to query the environment and call other plugins regardless of their integration declarations.
3. The host stages each plugin in a shadow copy. .NET plugins get an unloadable assembly load context (ALC) and a private dependency injection (DI) container. It runs generated NOF assembly initializers, then `IQuantumPlugin.ConfigureServices`, and discovers generated `[TransportOverQuantum]` server endpoints. The Web runtime validates the single-file JavaScript module and prepares an iframe.
4. The host applies pending migrations and starts static plugin entry points in dependency order. It publishes the new catalog atomically after successful startup. MAUI supplies the catalog to routes, menus and asset providers. Web modules activate in an iframe with an opaque origin after the catalog is published.

## Data and calls

Windows storage migration completes before MAUI initializes preferences or secure storage. It stages a complete copy with a SQLite backup, publishes the directory atomically and stops startup on failure. See the [storage decision](decisions/0001-windows-data.md) and [installation and recovery behavior](desktop-releases.md#local-data-and-uninstalling-on-windows).

Both runtimes ship append-only numbered SQLite migrations in `database.migrations`. The host verifies the applied SHA-256 history in `__quantum_plugin_migrations` and applies each plugin's pending scripts in a transaction before startup. Scripts leave transaction control to the host. A committed migration survives runtime rollback, so use backward-compatible database changes.

Web frames call the host through origin-checked `postMessage` and a `DotNetObjectReference` adapter. Remote procedure calls (RPCs) exchange JSON and NOF results. The host gives each invocation a fresh target DI scope and disposes it after serialization. Each RPC method exposes one case-sensitive capability name. The local catalog exposes capability schemas, dispatch policies and active providers. Request uses the declared All or Ordered policy. The report lists each contacted provider and is empty when no provider is available. The caller's invoker is tied to its runtime generation. The host drains pending calls and clears cached routes and JSON metadata during unload.

## Notify and localization

Both runtimes route Notify by the capability name declared by its publisher. The host serializes messages to JSON and uses the NOF in-memory event transport. Subscribers receive a raw payload in their own runtime and can read it or map it to a local type. The iframe acknowledges delivery and the host logs observer failures. Runtime disposal releases subscriptions and serializer caches. Plugins should dispose subscriptions during stop to prevent duplicates after restart.

The host sets the BCP-47 culture and page language and direction. Route titles fall back from exact culture to language to `title`. .NET plugins use resource files and `IStringLocalizer<T>`. The Web runtime passes locale explicitly on mount and remounts after a language change. Host and SDK example resources pair English defaults with Simplified Chinese translations.

## Runtime boundaries

Windows resolves plugin dependencies through `AssemblyDependencyResolver`. Mac Catalyst uses deterministic filename lookup and enables the Mono interpreter with `MtouchLink=None` to load external assemblies. Both share `System.*`, `Microsoft.*`, `NOF.*` and `Quantum.Plugin.Abstraction` with the host. Other dependencies resolve from the plugin. On unload, the host stops plugins in reverse dependency order, removes contributions, disposes DI and clears component type caches. Plugins must release references to their tasks, subscriptions and JavaScript objects to let the garbage collector reclaim the ALC.

Reload stages a full candidate snapshot, stops the old runtime, and starts the candidate in dependency order. A failure restores the old runtime. A successful switch publishes the new catalog and releases the old shadow copies. Disable first confirms affected hard dependents and moves their directories to `Modules/disabled`. Re-enable rechecks the full snapshot and rolls back on failure. Uninstall deletes the confirmed plugin directories.

`BlazorWebView.CreateFileProvider` serves plugin assets under `_content/{pluginId}/` from their original roots. Treat injected HTML and scripts as trusted code. Mobile displays remote plugin views; Desktop runs the plugin assemblies.
