# Desktop build profiles

[简体中文](../zh-CN/build-profiles.md)

Local builds default to **Preview**, for both Debug and Release. Optimization does not select the production identity. A profile is compiled into the app and cannot be changed by an environment variable or launch argument. Unknown profiles and mismatched application identities fail the build or startup.

| Profile | Application identity | Windows persistent root |
| --- | --- | --- |
| Preview (default) | `io.quantum.desktop.preview` | `%LOCALAPPDATA%\io7-cn\io.quantum.desktop.preview` |
| Stable (explicit) | `io.quantum.desktop` | `%LOCALAPPDATA%\io7-cn\io.quantum.desktop` (unchanged) |

On Windows each root contains its own `Data` (database, plugins, bridge journal, logs), `Settings` (preferences and protected credentials) and `Cache` (plugin shadows, WebView2 and temporary files). Preview ignores `QUANTUM_MODULES_PATH` and bundled Debug fixtures, always loading personal plugins from its own `Data\Modules`. Install test ZIPs into Preview. Stable retains its existing plugin path override and Debug fixture behavior.

WebView2 is configured before native window creation with the profile's `Cache\WebView2`, and the initialized browser's actual directory is checked. Preview also uses its own process temporary directory and rejects linked storage paths. Native application identity and publisher must match the profile before storage is opened. Windows manifest identity and display name are generated from the build properties using the [MAUI manifest placeholders](https://github.com/dotnet/maui/blob/10.0.20/src/SingleProject/Resizetizer/src/GeneratePackageAppxManifest.cs).

On macOS Stable retains its existing MAUI Library/cache locations. Preview stores host data in `<Library>/Application Support/io7-cn/io.quantum.desktop.preview/Data` and cache in `<MAUI cache>/io7-cn/io.quantum.desktop.preview`. Preferences and Keychain use the distinct preview bundle identity. On Catalyst 17 or later, Preview selects its own persistent WebKit store identifier; older systems use a nonpersistent browser store, while host databases and plugins remain persistent. See [WebKit profile stores](https://developer.apple.com/documentation/webkit/wkwebsitedatastore) and the [.NET binding](https://learn.microsoft.com/en-us/dotnet/api/webkit.wkwebsitedatastore.create).

## Build and publish

Run Windows `dotnet` commands from an elevated PowerShell on development machines that require it.

```powershell
# Local test build, including Release optimization; no production identity.
dotnet build src/Quantum/Quantum.csproj -c Release -f net10.0-windows10.0.19041.0

# A production build requires an explicit selection.
dotnet build src/Quantum/Quantum.csproj -c Release -f net10.0-windows10.0.19041.0 -p:QuantumBuildProfile=Stable
```

The application uses separate `bin/<profile>/` and `obj/<profile>/` directories. This also separates restored assets, generated native manifests and static web asset metadata. When publishing, use a different `-o` directory for each profile. Tests can run with `-p:QuantumCoreOnly=true`; pass `-p:QuantumBuildProfile=Stable` to verify the production profile too. The official `.ci/Build-DesktopRelease.ps1` explicitly selects Stable, and keeps the production installer name, package ID and update feed.

Preview skips legacy data/plugin migration, Velopack startup hooks and uninstall registration. It does not ship the production Windows uninstall script or enable the macOS uninstall service, and its update backend cannot download or apply production updates. Update Preview by rebuilding into a separate output directory. Its window title identifies it as Quantum Preview; the startup log records the profile, identity and storage directories.

## Platform and existing data

Preview is offline by default. Host account, marketplace and mobile bridge HTTP requests receive an offline response without making a network request. It ignores the production `QUANTUM_PLATFORM_URL`. To connect a separate test platform, explicitly set `QUANTUM_PREVIEW_PLATFORM_URL` to its HTTP(S) address; malformed addresses, the public production host and the configured production origin are rejected rather than falling back. Stable retains its production platform behavior. DNS aliases or a test server connected to production databases remain the operator's responsibility.

Profiles isolate the host's stores and connections; they are not an operating system sandbox for arbitrary .NET plugins. A plugin that directly accesses external files or its own cloud service can still affect those resources.

The persistent Preview profile starts separately from production and from earlier one-off previews. It never automatically imports either data set. Existing production files and application identity remain in place. See [installation and recovery](desktop-releases.md) for Stable migration and uninstall behavior.
