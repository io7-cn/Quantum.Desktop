# 桌面构建配置与数据隔离

[English](../en/build-profiles.md)

普通本地构建默认使用 **Preview 测试版**，Debug 与 Release 都一样。优化方式不会决定是否使用正式数据。配置会编译进应用，环境变量和启动参数无法切换；配置名无效或应用身份不匹配时，构建或启动会停止。

| 配置 | 应用身份 | Windows 持久化根目录 |
| --- | --- | --- |
| Preview（默认） | `io.quantum.desktop.preview` | `%LOCALAPPDATA%\io7-cn\io.quantum.desktop.preview` |
| Stable（显式指定） | `io.quantum.desktop` | `%LOCALAPPDATA%\io7-cn\io.quantum.desktop`（保持原位置） |

Windows 两个根目录各有自己的 `Data`（数据库、插件、桥接日志、运行日志）、`Settings`（偏好设置与受保护的凭据）、`Cache`（插件影子副本、WebView2 与临时文件）。测试版忽略 `QUANTUM_MODULES_PATH` 和 Debug 暂存的示例插件，只使用自己的 `Data\Modules`；请向测试版安装测试 ZIP。正式版保留原来的插件路径覆盖和 Debug 示例加载方式。

WebView2 在创建原生窗口前就指定为对应配置的 `Cache\WebView2`，初始化后还会核对实际目录。测试版另设进程临时目录，并拒绝带链接的存储路径。打开存储前会核对原生应用身份及发布者。Windows 清单中的身份和显示名通过 [MAUI 清单占位符](https://github.com/dotnet/maui/blob/10.0.20/src/SingleProject/Resizetizer/src/GeneratePackageAppxManifest.cs) 根据构建属性生成。

macOS 正式版继续使用原有 MAUI Library/缓存位置。测试版的宿主数据保存在 `<Library>/Application Support/io7-cn/io.quantum.desktop.preview/Data`，缓存位于 `<MAUI 缓存>/io7-cn/io.quantum.desktop.preview`；偏好设置和 Keychain 使用独立的测试版 bundle 身份。Catalyst 17 及以上为测试版选择独立的持久化 WebKit 存储标识；较旧系统使用不落盘的浏览器存储，但宿主数据库、插件仍然持久化。参见 [WebKit 配置存储](https://developer.apple.com/documentation/webkit/wkwebsitedatastore)及 [.NET 绑定](https://learn.microsoft.com/en-us/dotnet/api/webkit.wkwebsitedatastore.create)。

## 构建与发布

在要求提权的 Windows 开发机上，请从管理员 PowerShell 运行 `dotnet`。

```powershell
# 本地测试版；即使使用 Release 优化，也不会采用正式身份。
dotnet build src/Quantum/Quantum.csproj -c Release -f net10.0-windows10.0.19041.0

# 正式版必须显式选择。
dotnet build src/Quantum/Quantum.csproj -c Release -f net10.0-windows10.0.19041.0 -p:QuantumBuildProfile=Stable
```

应用分别使用 `bin/<配置>/` 与 `obj/<配置>/`，还原结果、生成的原生清单和静态资源元数据也分开。发布时应为每种配置使用不同的 `-o` 输出目录。测试可以使用 `-p:QuantumCoreOnly=true`，再加 `-p:QuantumBuildProfile=Stable` 可验证正式配置。正式发布脚本 `.ci/Build-DesktopRelease.ps1` 已显式选择 Stable，并保留正式安装包名称、包 ID 和更新源。

测试版跳过旧数据/插件迁移、Velopack 启动钩子和卸载注册；不携带 Windows 正式卸载脚本，不启用 macOS 正式卸载服务，更新后端也不能下载或应用正式更新。测试版通过重新构建到独立输出目录更新。窗口标题显示 Quantum Preview，启动日志记录配置、身份和存储目录。

## 平台连接与已有数据

测试版默认离线，宿主账号、插件市场和移动桥接的 HTTP 请求直接返回离线响应，不发起网络访问；它忽略正式版的 `QUANTUM_PLATFORM_URL`。若有独立测试平台，请显式设置 `QUANTUM_PREVIEW_PLATFORM_URL` 为其 HTTP(S) 地址。地址无效、指向公开正式平台主机或已配置的正式平台来源时会拒绝启动，不会回退到正式平台。正式版保持原有连接方式。DNS 别名，或测试服务器实际连接正式数据库的情况，仍需部署者自行保证分离。

这里隔离的是宿主存储和连接，并非任意 .NET 插件的操作系统沙箱。插件若自行读取外部文件或连接自己的云服务，仍可能影响那些外部资源。

长期 Preview 配置与正式数据、此前的一次性测试数据均分开，不自动导入任一数据集；正式数据及应用身份保留原位置。Stable 的迁移与卸载行为见[安装及恢复说明](desktop-releases.md)。
