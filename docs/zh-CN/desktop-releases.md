# 桌面安装与更新

[English](../en/desktop-releases.md)

## 下载安装

从 [GitHub Releases](https://github.com/io7-cn/Quantum.Desktop/releases) 下载适合自己系统的安装包。
Windows 使用 `Quantum-win-x64-Setup.exe`；macOS 根据 Apple Silicon 或 Intel 处理器选择对应的 `.pkg`。
可用平台与版本以发布页列出的文件为准。

安装包包含应用所需的 .NET 运行时，无需另外安装 .NET SDK 或 MAUI workload。
Windows 安装器可能需要联网安装 WebView2 或 VC++ Runtime。

## Windows 本地数据与卸载

Windows 将持久化状态保存在 `%LOCALAPPDATA%\io7-cn\io.quantum.desktop`，独立于默认安装目录 `%LOCALAPPDATA%\Quantum`。`Data` 包含 `quantum.db`、个人插件 `Modules`、日志和移动桥接日志；`Settings` 包含设置和由 Windows 保护的登录状态；`Cache` 为可清理的缓存。正式构建默认将个人插件保存在应用数据目录；`QUANTUM_MODULES_PATH` 可指定外部目录，Debug 构建仍可使用随构建暂存的测试插件。

首次启动在打开存储前，将旧 `%LOCALAPPDATA%\Quantum\io.quantum.desktop` 目录及安装目录中仍存在的 `Modules` 复制到新位置。SQLite 备份包含已提交的 WAL 数据。迁移一次性提交完整目录，保留旧文件，后续启动不重复导入。新目录已有内容、插件冲突、路径不可访问、目录链接或数据库无效都会停止启动；请先备份两处数据、解决报出的冲突，再重试。迁移前请关闭旧版 Quantum。已删除的数据需要通过另外保存的备份恢复。

**从受影响旧版本升级前**，请关闭 Quantum，将旧数据目录和 `<安装目录>\current\Modules` 备份到安装目录之外。旧更新器可能在新程序启动前替换 `current`，此过程中已删除的插件无法由新程序迁移。首次成功启动后，将插件备份恢复到新的 `Data\Modules`。不要先卸载受影响旧版本再进行升级。

成功启动后，从 Windows 设置进入 Quantum 卸载入口，会提示是否保留数据。**是（默认）**保留数据库、插件、设置和登录状态，重装后继续使用；**否**需要再次确认，在应用卸载成功后才永久删除上述两个应用自有数据目录；**取消**退出卸载。卸载失败时保留数据。自定义的外部插件目录始终保留。静默卸载使用 `QuietUninstallString`，不弹窗且始终保留数据。直接调用 Velopack 的 `Update.exe --uninstall` 也会保留新的独立数据目录，但会跳过数据选择提示。

正常启动和更新完成钩子会刷新卸载入口；从未启动的安装没有新用户数据，仍使用安装器的默认入口。卸载包装脚本在安装目录之外的 Windows PowerShell 进程中运行，因为 Velopack 会结束安装目录内的可执行程序。用户交互不放入有超时限制的 Velopack 回调。迁移完成标记缺失时，包装脚本会阻止卸载，避免删除可能仍是唯一副本的旧数据。

发布 [问题 #8](https://github.com/io7-cn/Quantum.Desktop/issues/8) 的修复前，须执行[安装包验收步骤](desktop-data-validation.md)。

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
