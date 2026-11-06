# 0001: Separate Windows user data from installation

[简体中文](../../zh-CN/decisions/0001-windows-data.md)

Status: Proposed in [issue #8](https://github.com/io7-cn/Quantum.Desktop/issues/8). Owner and acceptance: Desktop maintainer @XmmShp. Recorded: 2026-11-05; review before the first release containing this fix. Supersedes: none.

The old MAUI publisher path places `%LOCALAPPDATA%\Quantum\io.quantum.desktop` below Velopack's default installation root. Uninstall removes that root. Plugin discovery also prefers installation-local `Modules`. Users have reported data loss after uninstall/reinstall.

Use the stable MAUI publisher component `io7-cn` and retain the package identity `io.quantum.desktop`. This moves data, preferences and protected storage together without replacing MAUI's storage implementations. Release plugin writes use application data. Stage a one-time legacy copy with a consistent SQLite backup, publish atomically, preserve originals and fail startup on conflicts. Keep sign-in protection tied to the existing package identity.

An alternative of preserving arbitrary children during Velopack uninstall is unavailable in the pinned updater. A prompt inside its fast callback can time out and cannot veto root deletion. Use an application-owned PowerShell uninstall entry instead: default to keeping data, confirm deletion separately, preserve data on silent runs or updater failure, and never delete external plugin overrides. Register on startup and after updates; an installation never launched has no new user data.

Consequences: the new root becomes a persistent compatibility contract; the first launch requires disk space for a full copy. Legacy `current\Modules` must be backed up before the affected old updater replaces it. Windows PowerShell must be available. See [operations and rollback](../desktop-releases.md).

On macOS, the existing MAUI Library path is already outside the bundle and remains compatible. Migrate bundled plugins into that location with conflict checks and recovery from interrupted directory replacement. Use the native file manager's Trash operation for the in-app uninstall choice; stop data writers before optional cleanup and preserve shared Library roots. Finder deletion cannot invoke the app's confirmation flow and therefore keeps data. The required `test` gate includes macOS storage tests and both Catalyst architectures.
