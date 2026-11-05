# Desktop installation and updates

[简体中文](../zh-CN/desktop-releases.md)

## Install

Download a build for your operating system from [GitHub Releases](https://github.com/io7-cn/Quantum.Desktop/releases). Windows uses `Quantum-win-x64-Setup.exe`. On macOS, select the `.pkg` for Apple Silicon or Intel. The release page lists available builds. The installer includes the .NET runtime. Windows setup may need the network to install WebView2 or the Visual C++ runtime.

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
