# .NET plugin development

[简体中文](../zh-CN/plugin-development.md)

Use .NET for production plugin development. The [TypeScript runtime](web-plugin-development.md) is experimental.

Expose any capability that Quantum, other plugins or AI tools must discover and call through NOF remote procedure calls (RPC). Define an `IRpcService` with `[TransportOverQuantum]`, generate the server metadata and register the server with `AddRpcServer<TServer>()`. Before release, verify that `quantum.capabilities.list` lists the method and that a caller can invoke its full name across the JSON boundary.

## 1. Create a project

Use a Razor class library targeting `net10.0`. Reference the published `Quantum.Plugin.Abstraction` package for the host's assembly-level compatibility boundary and the NOF packages you use. Set compatible versions in the project or central package file. Copy `plugin.json`, `wwwroot` and any numbered SQL migrations to the build output. Use the [SDK's example plugin](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.2/samples/Quantum.ExamplePlugin/Quantum.ExamplePlugin.csproj) as a working template. Keep the host and its internal assemblies out of plugin packages.

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Quantum.Plugin.Abstraction" Version="0.3.0-rc.2" />
    <PackageReference Include="NOF.Abstraction" />
    <PackageReference Include="NOF.Application" />
    <PackageReference Include="NOF.Contract" />
  </ItemGroup>

  <ItemGroup>
    <Content Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="migrations\**\*.sql" CopyToOutputDirectory="PreserveNewest" />
    <Content Update="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

## 2. Define the manifest and RPC contract

Declare the plugin ID, full semantic version, entry assembly and routes in `plugin.json`. Supply optional localized titles and set `showInNavigation` to false for routes that should open by path but stay out of the menu. IDs are limited to 128 characters. Use `versionRange` for dependencies and integrations. See the [dependent SDK example](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.2/samples/Quantum.ExampleDependentPlugin/plugin.json).

- `dependencies` require a compatible plugin and establish startup order. A missing provider, incompatible version or hard cycle blocks affected plugins.
- `integrations` prefer compatible providers to start first. Missing targets and soft cycles still allow startup. Use `IQuantumPluginEnvironment` to decide whether to enable integration code.

Version ranges accept bounded and unbounded intervals, finite sets, and unions. For example, `[1.0.0,1.2.0)` includes 1.0.0 and excludes 1.2.0. Use a parenthesis on the unbounded side, as in `(,1.2.0)` or `(1.3.0,)`. Use `(,)` or `*` for any version, `{1.2.3,1.2.4}` for a finite set, and `|` to combine ranges. Compare prereleases by semantic version precedence and ignore build metadata.

Declare a partial `RpcServer<I…>`, implement the generated handlers and register the server in the plugin container. Let NOF register generated handlers through its assembly initializer. Add descriptions to the service, methods and request fields so discovery explains meaning, units, enum values and side effects. Each RPC method declares one stable name with [RequestCapability]. List all provided names in capabilities.provides in plugin.json. Call by the exact lowercase capability name. Inspect the Request report for each contacted provider. Each invocation gets a target scope and exchanges JSON rather than sharing private CLR types. A stable contract assembly is useful if both sides use a generated typed client.

The schema 2 catalog returns a flat capabilities list with each name, the current providerPluginIds, and JSON schemas from the actual serializer. Enum values are strings. Required constructor parameters must be supplied; omitted optional parameters keep their defaults, and a nonnullable field rejects explicit null. Provider outcomes report `rpc_invalid_payload` with `rpc`, `path` and `reason` when payload validation fails. An unavailable capability returns an empty report. Both .NET and TypeScript callers see the same rules.

```json
{
  "id": "quantum.plugin.example",
  "version": "0.1.0",
  "entryAssembly": "Quantum.ExamplePlugin.dll",
  "database": {
    "migrations": "./migrations"
  },
  "dependencies": [
    { "id": "quantum.plugin.core", "versionRange": "[0.1.0,)" }
  ],
  "integrations": [
    { "id": "quantum.plugin.theme", "versionRange": "[0.1.0,)" }
  ],
  "ui": {
    "routes": [
      {
        "path": "/plugins/example",
        "component": "Quantum.ExamplePlugin.Pages.Index",
        "title": "示例插件",
        "titles": { "en-US": "Example plugin" },
        "icon": "✦",
        "order": 100
      },
      {
        "path": "/plugins/example/detail",
        "component": "Quantum.ExamplePlugin.Pages.Detail",
        "showInNavigation": false
      }
    ],
    "globalComponents": [
      {
        "component": "Quantum.ExamplePlugin.Components.FloatingPanel",
        "order": 100
      }
    ]
  },
  "web": {
    "head": [
      "<link rel=\"stylesheet\" href=\"_content/quantum.plugin.example/site.css\">"
    ],
    "postBlazor": [
      "<script src=\"_content/quantum.plugin.example/main.js\"></script>"
    ]
  }
}
```

```csharp
public sealed record CreateGreetingRequest;

[TransportOverQuantum]
public interface IExampleRpcService : IRpcService
{
    [RequestCapability("example.greet", QuantumDispatch.All)]
    Result<string> CreateGreeting(CreateGreetingRequest request);
}

public partial class ExampleRpcService : RpcServer<IExampleRpcService>;

public sealed class CreateGreeting(ExampleState state) : ExampleRpcService.CreateGreeting
{
    public override Task<Result<string>> HandleAsync(
        CreateGreetingRequest request,
        Context context,
        CancellationToken cancellationToken)
        => Task.FromResult<Result<string>>(state.CreateGreeting());
}
```

```csharp
var result = await services.GetRequiredService<IQuantumRequester>().RequestAsync<string>(
    "example.greet",
    new { },
    Context.Empty,
    cancellationToken);
```

```csharp
var catalog = await services.GetRequiredService<IQuantumRequester>()
    .DiscoverCapabilitiesAsync(cancellationToken);
```

## 3. Register services and implement lifecycle

The host first runs NOF assembly initializers, then the entry assembly's static `IQuantumPlugin.ConfigureServices` hooks in a private dependency injection (DI) container. The bootstrap type does not require registration. Implement `StartAsync` and `StopAsync`; they use the same runtime scope. The host starts providers before dependents and stops them in reverse order. Use a plugin singleton for state shared across independent RPC scopes. Keep global host discovery and static references outside unloadable plugin code. Use `IQuantumPluginRuntimeContext` for the runtime version and read-only shadow path.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Quantum.Plugin.Abstraction;

public interface IExampleState
{
    DateTimeOffset? StartedAt { get; }
}

public sealed class ExampleState
{
    public DateTimeOffset? StartedAt { get; private set; }

    internal void MarkStarted() => StartedAt = DateTimeOffset.Now;
}

[AutoInject(
    ServiceLifetime.Singleton,
    RegisterTypes = [typeof(IExampleState)])]
public sealed class ExampleStateView(ExampleState state) : IExampleState
{
    public DateTimeOffset? StartedAt => state.StartedAt;
}

public sealed class ExamplePlugin : IQuantumPlugin
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ExampleState>();
    }

    public static Task StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        services.GetRequiredService<ExampleState>().MarkStarted();
        var environment = services.GetRequiredService<IQuantumPluginEnvironment>();
        if (environment.IsPluginLoaded(PluginId.Of("quantum.plugin.theme")))
        {
            // Enable the integration when its provider is loaded.
        }

        return Task.CompletedTask;
    }

    public static Task StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
```

## 4. Publish and subscribe to Notify

Declare the capability name in `capabilities.provides` and its `messageSchema` in `capabilities.notifications`. Subscribers use the capability name and receive a `JsonElement`. They can inspect fields directly or call `TryDeserialize<T>()` with their own DTO.

```csharp
var notify = services.GetRequiredService<IQuantumNotifier>();
var subscription = notify.Subscribe("devices.status", (notification, token) =>
{
    if (notification.Payload.ValueKind == JsonValueKind.Object
        && notification.Payload.TryGetProperty("state", out var state)
        && state.ValueKind == JsonValueKind.String)
    {
        logger.LogInformation("Device state is {State}", state.GetString());
    }

    return Task.CompletedTask;
});

await notify.PublishAsync("devices.status", new { state = "ready" }, cancellationToken);
```

Dispose the subscription when the plugin stops. The host also releases it with the runtime. By default, `All` delivers to every active subscriber. To control propagation, use `Observe` and declare `Ordered` and the eligible `stopObservers` in the publisher manifest. The host logs observer failures and continues delivery.

### Request and Notify capabilities

Declare Request handlers with [RequestCapability] and choose All or Ordered in the contract. The host calls each handler through NOF in its own scope. An Ordered write may require a unique owner. Notify uses NOF in-memory event delivery and supports the same All and Ordered policies. The [SDK capability guide](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.2/docs/en/dotnet-plugin-development.md) describes discovery, ownership and propagation.

## 5. Provide pages and translations

Use Razor components with manifest routes. Use standard `.resx` resources and `IStringLocalizer<T>` for localized components, and ship the culture satellite assemblies alongside the plugin. Route labels use an exact culture title, then its language, then the default `title`.

```razor
@namespace Quantum.ExamplePlugin.Pages
<h1>Example</h1>
<p>Started at @State.StartedAt</p>

@code {
    private readonly IExampleState State;

    // Inject plugin services from the private container for this component.
    public Index(IExampleState state)
    {
        State = state;
    }
}
```

```razor
@using Microsoft.Extensions.Localization
@inject IStringLocalizer<PluginStrings> L

<h1>@L["PageTitle"]</h1>
```

## 6. Ship assets, Web contributions and migrations

Put static assets under `wwwroot`. They appear at `_content/{pluginId}/…`. Use `web.head` or `web.postBlazor` for trusted host-page contributions. Copy isolated Razor CSS bundles into `wwwroot` and reference them explicitly. Release any timers, events and JavaScript/.NET references in `StopAsync`; removing a script element does not undo executed code.

Both runtimes can declare `database.migrations` and ship the complete numbered SQLite SQL history. The host runs pending migrations transactionally before startup and verifies hashes for applied files. Keep migrations append-only, omit transaction-control statements and design for compatibility with the previous runtime. Committed migrations survive a rollback. Use unique, stable table names in the shared database. A .NET plugin can register an `IDbContextModelCreatingContributor` for its own entity mappings. Keep its context in the private runtime and database files out of the shadow directory.

```text
<plugin-root>/wwwroot/site.css
    -> _content/<plugin-id>/site.css
```

```json
{
  "database": {
    "migrations": "./migrations"
  }
}
```

```text
migrations/
├── 001_init.sql
├── 002_add_index.sql
└── 003_add_status.sql
```

```csharp
internal sealed class CalendarDbContextModelCreatingContributor
    : IDbContextModelCreatingContributor
{
    public void Configure(IDbModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CalendarEntry>(entity =>
        {
            entity.ToTable("OfficialCalendarEntries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(120).IsRequired();
        });
    }
}
```

```text
<ApplicationData>/quantum.db
```

## 7. Debug

Build the plugin and place each plugin in its own folder under `Modules`. Set `QUANTUM_MODULES_PATH` to an accessible override when needed. An inaccessible override falls back to the application data directory. For sandboxed macOS builds use the application data directory. Supply the manifest, entry, private dependencies, migrations and assets. `disabled` is reserved.

Replace source files and increase the manifest version, then use Hot Upgrade or Rescan Modules in Settings. The host uses shadow copies, so the source assemblies are replaceable. A failed load or startup restores the previous runtime; committed SQL remains applied. Disable or uninstall shows the affected hard dependents before confirmation. Disable moves files to `Modules/disabled`; re-enable revalidates the entire candidate. Uninstall deletes the confirmed directories. Release tasks, events and type references when stopping so the assembly load context can be reclaimed.

```bash
dotnet build MyPlugin.csproj

QUANTUM_MODULES_PATH="$PWD/Modules" \
dotnet build src/Quantum/Quantum.csproj -t:Run -f net10.0-maccatalyst
```

```text
Modules/
└── quantum.plugin.example/
    ├── plugin.json
    ├── Quantum.ExamplePlugin.dll
    ├── ThirdParty.Dependency.dll
    ├── migrations/
    │   └── 001_init.sql
    └── wwwroot/
        ├── site.css
        └── main.js
```

## 8. Distribute ZIP packages

Drop a ZIP into the app and review the install plan. Give each plugin a separate directory, optionally under one wrapper directory. Reject nested plugin directories, absolute or traversing paths, backslashes, drive letters and symbolic links. Limits are 512 MiB compressed, 2 GiB expanded, 20,000 entries and 256 plugins.

The host selects the highest semantic version in the ZIP and skips installed versions that are newer or equal. Ambiguous ties or a matching disabled plugin reject the package. Preflight stages the combined candidate to check dependencies, entries and compatibility without running lifecycle hooks or migrations. Confirmation starts the file and runtime switch. Failure restores old files and runtime, subject to the committed migration rule above.

```text
community-pack.zip
├── quantum.plugin.notes/
│   ├── plugin.json
│   └── Quantum.NotesPlugin.dll
└── quantum.plugin.theme/
    ├── plugin.json
    └── wwwroot/
        └── dist/plugin.js
```

## Navigation icons

Use `ui.routes[].iconFile` with a relative path under `wwwroot`. The app accepts standard multi-frame ICO files up to 1 MiB, with frames up to 256 pixels. Ship the file with the plugin. Absolute paths, URLs, traversal and symbolic links are rejected.

```json
{
  "path": "/plugins/example",
  "component": "Example.UI.Pages.Home",
  "icon": "◇",
  "iconFile": "icons/plugin.ico"
}
```
