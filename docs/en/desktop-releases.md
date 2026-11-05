# Desktop installation and updates

[简体中文](../zh-CN/desktop-releases.md)

## Install

Download a build for your operating system from [GitHub Releases](https://github.com/io7-cn/Quantum.Desktop/releases). Windows uses `Quantum-win-x64-Setup.exe`. On macOS, select the `.pkg` for Apple Silicon or Intel. The release page lists available builds. The installer includes the .NET runtime. Windows setup may need the network to install WebView2 or the Visual C++ runtime.

## Local data and uninstalling on Windows

Windows stores persistent state under `%LOCALAPPDATA%\io7-cn\io.quantum.desktop`, outside the default installation directory `%LOCALAPPDATA%\Quantum`. `Data` contains `quantum.db`, personal `Modules`, logs and the mobile bridge journal. `Settings` contains preferences and Windows-protected sign-in state. `Cache` contains disposable files. Release builds always use application data for personal plugins unless `QUANTUM_MODULES_PATH` is set. Debug builds may use the bundled test fixtures.

Before opening storage, the first launch copies the legacy `%LOCALAPPDATA%\Quantum\io.quantum.desktop` tree and any still-present installation `Modules` into the new directory. SQLite backup includes committed WAL data. Migration publishes a complete directory once, leaves originals intact, and does not replay them on later launches. An existing nonempty destination, conflicting plugins, inaccessible paths, links or an invalid database stop startup; back up both locations and resolve the reported conflict before retrying. Close older Quantum processes before migration. Already deleted data requires a separate backup to recover.

**Before upgrading an affected old release**, close Quantum and back up both the legacy data directory and `<installation>\current\Modules` outside the installation. The old updater can replace `current` before the new executable starts, so plugins removed during that transition cannot be migrated by the new executable. Restore that plugin backup into the new `Data\Modules` after the first successful launch. Do not uninstall the affected release as an upgrade step.

After a successful launch, Windows Settings uses the Quantum uninstall entry to ask whether to keep data. **Yes (default)** retains databases, plugins, preferences and sign-in state for reinstallation. **No** requires a second confirmation before permanently deleting the two app-owned data directories, after the application uninstall succeeds. **Cancel** exits without uninstalling. A failed uninstall retains data. Custom external plugin directories are never cleared. Silent uninstall uses `QuietUninstallString`, displays no prompt and always keeps data. Directly invoking Velopack's `Update.exe --uninstall` also preserves the new independent data directory but bypasses the choice dialog.

The entry is refreshed on normal startup and in the post-update hook; an installation that has never launched has no new user data and still uses the installer's default entry. The wrapper runs in Windows PowerShell outside the installation because Velopack terminates executables inside it. User interaction is kept outside Velopack's time-limited callbacks. A missing migration completion marker blocks the wrapper from deleting an installation that could still contain the only legacy copy.

Before shipping a fix for [issue #8](https://github.com/io7-cn/Quantum.Desktop/issues/8), follow the [installation acceptance procedure](desktop-data-validation.md).

## In-app updates

The app checks for updates in the background at startup. Settings shows the installed and target versions, progress and status. Save your work, then select the update and restart action. The updater prefers a delta package and falls back to the full package. An interrupted download can be retried without replacing the current installation. Keep settings, databases and personal plugins under the application data directory, which the update preserves.

## macOS update helper

`Quantum.UpdateAgent` is a separate CoreCLR executable signed and shipped inside the macOS `.app`. The main app uses Mac Catalyst, where the pinned Velopack 1.2.0 cannot initialize. Desktop launches the helper to check, download and report status. To apply the update, the helper passes the desktop process ID to Velopack's `UpdateMac`. Desktop exits after a successful handoff. `UpdateMac` waits for it, replaces the app and restarts it. [Velopack Mac Catalyst support](https://github.com/velopack/velopack/pull/1020)

## Troubleshooting

For source builds, update by rebuilding or use an installer to test the update feed. When migrating from a sandboxed macOS build to direct distribution, export and migrate user data and plugins from the old container. For an update failure, include the error, operating system and installed and target versions in an [issue](https://github.com/io7-cn/Quantum.Desktop/issues).

## Independent repository release workflow

[Release Desktop](../../.github/workflows/release-desktop.yml) builds this repository directly. Push a `desktop-vX.Y.Z` tag on verified `main` history to package and publish a stable release. Manual runs upload workflow artifacts only. Packages and checksums are validated before publication; published versions are immutable.

Windows packaging runs by default. To require Windows signing, configure `WINDOWS_CERTIFICATE_BASE64`, `WINDOWS_CERTIFICATE_PASSWORD` and the repository variable `REQUIRE_WINDOWS_SIGNING=true`. Enable `ENABLE_MACOS_RELEASE=true` only after configuring the application and installer certificates, their identities and password, and Apple notarization credentials listed in the workflow. Both macOS architectures must finish successfully before a macOS-enabled release is published.

Clients configured for the legacy release source need a one-time manual installation from this repository. Close the app and install the new distribution, preserving the application data directory. Subsequent versions use this repository's update feed. Moving the source repository does not redirect an installed client's previous update source.
