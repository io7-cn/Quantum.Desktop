# 桌面安装与更新

[English](../en/desktop-releases.md)

## 下载安装

从 [GitHub Releases](https://github.com/io7-cn/Quantum.Desktop/releases) 下载适合自己系统的安装包。
Windows 使用 `Quantum-win-x64-Setup.exe`；macOS 根据 Apple Silicon 或 Intel 处理器选择对应的 `.pkg`。
可用平台与版本以发布页列出的文件为准。

安装包包含应用所需的 .NET 运行时，无需另外安装 .NET SDK 或 MAUI workload。
Windows 安装器可能需要联网安装 WebView2 或 VC++ Runtime。

## Windows 本地数据与卸载

Windows 将持久化状态保存在 `%LOCALAPPDATA%\io7-cn\io.quantum.desktop`，独立于默认安装目录 `%LOCALAPPDATA%\Quantum`。`Data` 包含数据库、个人插件和日志；`Settings` 包含设置和受保护的登录状态。正式构建默认使用应用数据中的插件目录，`QUANTUM_MODULES_PATH` 可指定外部目录；Debug 构建可使用随构建暂存的测试插件。

首次启动前请关闭旧版 Quantum。应用会一次性复制旧 `%LOCALAPPDATA%\Quantum\io.quantum.desktop` 状态及安装目录中仍存在的 `Modules`，保留原文件及 SQLite 预写日志（WAL）中已提交的记录。冲突、路径不可访问、目录链接或数据库无效都会停止启动；请先备份两处数据、解决报出的问题，再重试。回滚时须保留并备份新数据根目录，因为写入新记录后，旧副本会过期。已删除的数据需要通过另外保存的备份恢复。

**从受影响旧版本升级前**，请关闭 Quantum，将旧数据目录和 `<安装目录>\current\Modules` 备份到安装目录之外。旧更新器可能在新程序启动前替换 `current`，此过程中已删除的插件无法由新程序迁移。首次成功启动后，将插件备份恢复到新的 `Data\Modules`。不要先卸载受影响旧版本再进行升级。

启动后，从 Windows 设置卸载 Quantum 会询问是否保留数据。**是（默认）**保留数据供重装使用；**否**需要再次确认，仅在卸载成功后清除新旧两个应用自有数据根目录；**取消**退出卸载。静默卸载或卸载失败时保留数据，外部插件目录始终保留。该入口使用 Windows PowerShell，在启动和更新后刷新；迁移完成标记缺失时会阻止卸载。从未启动的安装使用安装器默认入口；直接调用 `Update.exe --uninstall` 会跳过选择，但保留独立数据根目录。

## 应用内更新

- 应用启动后会在后台检查更新，有新版本时显示提示。
- 在设置页查看当前版本、目标版本、下载进度和更新状态。保存工作后，点击“一键更新并重启”。
- 更新优先使用增量下载；增量不可用时回退到完整包。
- 断网或下载失败时，可以重试；当前安装不会被未完成的下载替换。
- 更新保留应用数据目录中的设置、数据库与插件。请勿将自己的插件或数据存入应用安装目录。

## macOS 更新助手的维护说明

`Quantum.UpdateAgent` 是随 macOS `.app` 签名发布的独立 CoreCLR 可执行文件。主程序以 Mac Catalyst 运行，当前固定的 Velopack 1.2.0 无法在该运行时初始化。主程序启动助手来检查和下载更新，并读取进度与结果。应用更新时，助手把桌面主进程的 PID 交给 Velopack 的 `UpdateMac`。主程序收到交接成功的响应后退出，由 `UpdateMac` 等待该进程退出、替换应用并重启。[Velopack 的 Mac Catalyst 支持变更](https://github.com/velopack/velopack/pull/1020)

## 常见问题

从源码或开发目录启动的客户端不支持应用内更新，请通过本地构建更新，或改用正式安装包。

macOS 从沙箱版本迁移到直接安装版本时，旧容器中的数据不会自动搬迁。请先从旧应用导出插件和数据，
再迁移至新安装的应用。

如果更新失败，保留错误信息，并附上操作系统、当前版本及目标版本，提交到
[Issues](https://github.com/io7-cn/Quantum.Desktop/issues)。

## 独立仓库发行工作流

[Release Desktop](../../.github/workflows/release-desktop.yml) 直接构建本仓库。在已验证的 `main` 历史上推送 `desktop-vX.Y.Z` 标签，打包并发布稳定版本。手动运行只上传工作流产物。发布前验证安装包及校验和，已发布版本不可变更。

默认构建 Windows 安装包。若要求 Windows 签名，配置 `WINDOWS_CERTIFICATE_BASE64`、`WINDOWS_CERTIFICATE_PASSWORD` 及仓库变量 `REQUIRE_WINDOWS_SIGNING=true`。配置工作流中列出的应用与安装器证书、签名身份、证书密码和 Apple 公证凭据后，才可启用 `ENABLE_MACOS_RELEASE=true`。启用 macOS 时，两种架构均成功后才能发布。

仍使用旧发行源的客户端需要从本仓库手动安装一次。请先关闭应用，安装新分发版本并保留应用数据目录，后续版本使用本仓库的更新源。源代码仓库的迁移不会自动重定向已安装客户端的更新源。
