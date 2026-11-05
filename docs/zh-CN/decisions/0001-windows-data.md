# 0001：分离 Windows 用户数据与安装目录

[English](../../en/decisions/0001-windows-data.md)

状态：在[问题 #8](https://github.com/io7-cn/Quantum.Desktop/issues/8) 中提议。负责人及验收：Desktop 维护者 @XmmShp。记录日期：2026-11-05；首次包含本修复的版本发布前复核。替代记录：无。

旧 MAUI 发布者路径将 `%LOCALAPPDATA%\Quantum\io.quantum.desktop` 放在 Velopack 默认安装根目录中，卸载会删除整个根目录；插件发现也优先使用安装目录中的 `Modules`。用户已报告卸载重装造成数据丢失。

使用稳定的 MAUI 发布者路径分量 `io7-cn`，保留包标识 `io.quantum.desktop`，使数据、设置和安全存储一起迁移，无须替换 MAUI 存储实现。正式构建将插件写入应用数据目录。一次性迁移暂存旧目录副本和 SQLite 一致备份，原子提交，保留原文件，冲突时停止启动。登录保护继续使用原有包标识。

另一种方案是在 Velopack 卸载时保留任意子目录，但固定版本更新器没有此能力；快速回调内的弹窗会超时，且不能否决根目录删除。采用应用管理的 PowerShell 卸载入口，默认保留，清除需额外确认，静默或卸载失败时保留，不删除外部插件覆盖目录。在启动和更新完成后注册；从未启动的安装没有新用户数据。

结果：新数据根目录成为持久兼容性约定；首次启动需要完整复制所需磁盘空间。旧 `current\Modules` 须在受影响旧更新器替换前备份。Windows PowerShell 必须可用。安装包验收、独立审查和现有必需 CI 门禁仍不可省略。参见[操作与回滚](../desktop-releases.md)及[验收](../desktop-data-validation.md)。
