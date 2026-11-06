# 扩展点与设置页

[English](../en/extensions.md)

Desktop 使用 SDK `0.3.0-rc.3` 候选版本通过代码定义、无业务类型依赖的 JSON 扩展协议。宿主注册目录位于 `Plugins/Extensions`，不解释业务载荷。插件通过 `IQuantumExtensions` 或 `context.extensions` 定义和观察自己的扩展点，无需增加 `plugin.json` 字段。定义和贡献属于运行时代次，只有已提交目录中的代次可见。载荷为独立 JSON 数据，修订号仅用于会话内排序。设置模块定义下列消费契约，其他消费者可以定义不同的 JSON 契约。

## 设置视图契约，版本 1

| 扩展点 | 内容 |
| --- | --- |
| `quantum.settings/pages` | 设置页签 |
| `quantum.settings/general` | 附加到通用页的分组 |
| `quantum.settings/accounts` | 附加到账户页的分组 |
| `quantum.settings/plugins` | 附加到插件管理页的分组 |

贡献为对象，包含 `title`（默认标题）、可选 `titles`（以 BCP-47 文化或语言代码为键），以及 `component`（贡献者入口程序集中的 .NET 组件全名）或 `view`（Web 挂载视图 ID）。使用哪个字段由运行时类型决定。无效内容被忽略，组件异常由局部错误边界处理。标题依次回退到精确文化、语言、默认标题；排序依次使用数值顺序、插件 ID、本地 ID。宿主通过同一机制注册三个内置页签，并保留 `quantum.settings` 命名空间。内置贡献的 `builtin` 和 `titleKey` 属于宿主内部字段，其他插件不能利用它们替换核心页面。

```csharp
using var page = JsonDocument.Parse("""
    { "title": "Example", "component": "Example.SettingsPanel" }
    """);
extensions.Register("quantum.settings/pages", 1, "settings", page.RootElement);
```

账户服务使用与其他分组相同的视图贡献。组件负责登录流程，通过插件服务管理状态和持久化。凭据禁止放入贡献。没有专门的账户提供方 API 或表单语言。插件也可以在自己的组件中观察另一个扩展点并展示贡献，从而开放自己的界面。

页签链接使用 `/settings?section=<插件ID/本地ID>`，兼容现有 `section=account`、`section=plugins` 和 `#account` 链接。选中贡献消失时返回通用页。.NET 组件通过现有激活器获取插件服务容器；Web 视图使用已有的沙箱 iframe 和 route/view 挂载契约。切换语言重新挂载。Web 运行时目前每个插件同时挂载一个视图，同一 Web 插件的多个贡献需要通过选择切换，不应同时挂载。

## 候选版本验证

SDK 与 Desktop 保持独立仓库边界。按 SDK 的包验证流程构建候选 NuGet 包，然后使用 `-p:RestoreAdditionalProjectSources=<候选包目录>` 恢复 Desktop。Desktop 精确固定候选版本，禁止以兄弟仓库源代码引用替代包依赖。候选包尚未公开发布；需要从公共源恢复的消费者合并前，应先按批准的发布流程发布对应包。

验证 SDK 契约、Desktop .NET 测试、`node --test tests/web-plugin-host.test.mjs` 和 Windows 原生构建。macOS iframe 渲染需要单独的平台验证。

## 手工界面复核

运行带 .NET 示例测试插件的原生 Debug 宿主。打开设置，确认三个内置页签旁出现示例插件页签，显示自定义扩展点的消息。检查键盘导航与窄窗口下页签换行。打开示例页签，再从插件管理重载或禁用插件，确认入口更新或消失且不保留旧内容。向同一页面添加来自同一 Web 插件的两个视图贡献，确认选择器可以切换单个 iframe。Windows 构建和 HTML 渲染测试不能替代这项原生界面复核。
