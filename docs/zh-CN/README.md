# Quantum Desktop 桌面端

[English](../en/README.md)

Quantum Desktop 是面向 Windows 和 macOS 的本地优先插件宿主，采用 .NET 10、NOF 和 .NET MAUI Blazor Hybrid。.NET 插件在可卸载的独立运行环境中执行，实验性的 TypeScript 插件在沙箱浏览器框架中执行。移动伴随客户端显示插件页面，相关动作由桌面宿主执行。

| 目录 | 用途 |
| --- | --- |
| `src/Quantum/` | 桌面宿主、Blazor 界面和插件运行时 |
| `src/Quantum.UpdateAgent/` | macOS 更新助手 |
| `tests/` | 桌面测试与测试资源 |
| `docs/` | 桌面端和插件文档 |
| `.ci/` | 打包、签名与发布脚本 |

桌面解决方案从 NuGet 还原 .NET 插件契约，Web 测试资源从 npm 还原对应版本。宿主通过仓库内的平台客户端和设备协议连接独立部署的 Quantum Platform 服务。

## 开发与发布

使用 .NET 10 SDK 和对应系统的 MAUI 工作负载。在仓库根目录执行 `dotnet build Quantum.Desktop.slnx` 构建，执行 `dotnet test tests/Quantum.Tests/Quantum.Tests.csproj -p:QuantumCoreOnly=true` 运行普通 `net10.0` 测试。原生 Debug 构建会将插件测试资源暂存到应用输出目录。Windows Debug 使用自包含的 Windows App SDK。

`tests/Fixtures/Plugins/` 下的插件是基于 [SDK 示例](https://github.com/io7-cn/Quantum.Sdk/tree/v0.3.0-rc.2/samples) 的固定宿主测试资源。在 Web 测试资源项目目录运行 `npm ci` 和 `npm test`，即可将其纳入原生 Debug 构建。

日志写入应用数据目录下的 `Logs/quantum-YYYYMMDD-HHmmss-PID-SEGMENT.log`，每段的滚动阈值为 20 MiB。写入下一条会超过阈值时先切换文件，单条超限日志仍完整写入。向应用传入 `--console` 可在终端同步查看日志。使用 `dotnet run` 时，在启动命令后追加 `-- --console`。

参见[登录会话](login-sessions.md)、[架构](architecture.md)、[桌面发行](desktop-releases.md)、[.NET 插件指南](plugin-development.md)、[TypeScript 插件指南](web-plugin-development.md)及[贡献指南](CONTRIBUTING.md)。项目采用 [Apache License 2.0 许可证](../../LICENSE)。

## 启动命令

macOS

```bash
dotnet build src/Quantum/Quantum.csproj -t:Run -f net10.0-maccatalyst
```

Windows

```powershell
dotnet run --project src/Quantum/Quantum.csproj --framework net10.0-windows10.0.19041.0
```

版权归属及第三方许可说明见 [NOTICE](../../NOTICE)。

## 开发规范

遵循[简体中文开发规范](../../.standards/docs/zh-CN/README.md)。.standards 子模块记录经过审查的确切版本；上游变更只有在显式更新该提交后才生效。开发前初始化：

```sh
git submodule update --init --recursive
python .standards/.ci/check-adoption.py
```

同步维护两种语言。AGENTS.md 指向采用的英文入口，项目专属指令补充共享规范。
