# TypeScript 插件开发

[English](../en/web-plugin-development.md)

> **实验性支持：** JavaScript/TypeScript 插件开发目前处于实验阶段，许多能力暂不可用，不建议作为主要开发方式。当前版本请优先使用 [.NET 插件开发指南](plugin-development.md)。

Quantum Web 插件不需要 DLL。宿主把每代插件复制到影子目录，在一个 `sandbox="allow-scripts"`、
opaque-origin 的独立 iframe 中执行入口模块；卸载或热更新时销毁整个 iframe。

## 1. 项目结构

```text
MyPlugin/
├── migrations/
│   └── 001_init.sql
├── package.json
├── plugin.json
├── src/
│   └── index.ts
└── wwwroot/
    └── dist/
        └── plugin.js
```

Web 插件与 .NET 插件使用相同的 id 规则；`disabled` 是宿主保留 id，不能用于插件 manifest。

安装 SDK 和 bundler：

```bash
npm install @io7/quantum-plugin-sdk@0.3.0-rc.3
npm install --save-dev typescript esbuild
```

入口必须是单文件 ESM bundle。iframe 不允许运行时导入 npm 包，因此所有依赖必须打进入口：

```json
{
  "scripts": {
    "build": "esbuild src/index.ts --bundle --format=esm --platform=browser --target=es2022 --outfile=wwwroot/dist/plugin.js"
  }
}
```

## 2. Manifest

```json
{
  "id": "quantum.plugin.notes",
  "version": "1.0.0",
  "runtime": {
    "kind": "web",
    "entry": "dist/plugin.js"
  },
  "database": {
    "migrations": "./migrations"
  },
  "integrations": [],
  "ui": {
    "routes": [
      {
        "path": "/plugins/notes",
        "view": "main",
        "title": "Notes",
        "titles": { "zh-CN": "笔记" },
        "icon": "N"
      },
      {
        "path": "/plugins/notes/detail",
        "view": "detail",
        "showInNavigation": false
      }
    ]
  }
}
```

`runtime.entry` 相对于 `wwwroot`，必须以 `.js` 或 `.mjs` 结尾，不能包含反斜杠、冒号、绝对路径或 `..`。
Web 插件不能使用 `web.head`/`web.postBlazor` 向宿主页面注入 HTML；样式和页面只能写入自己的 iframe。
路由的 `showInNavigation` 默认为 `true`；设为 `false` 后仍可通过路径或 `context.navigation.navigate()`
打开，但不会显示在宿主主导航中。
`title` 是默认回退，`titles` 使用 BCP-47 culture name 提供菜单和页面标题的本地化值。

manifest 的 `id` 与版本字段和 .NET 插件使用相同约束：id 最长 128 个字符，`version` 必须是写全
`major.minor.patch` 的 SemVer 2.0.0；`dependencies`/`integrations` 使用 `versionRange`，不再接受
`minVersion`。TypeScript SDK 提供 `PluginId.of()`、`SemanticVersion.parse()`、`SemanticVersion.components()`、
`SemanticVersion.compare()`、`VersionRange.parse()` 和 `VersionRange.contains()`；这些函数接收或返回普通字符串，JSON 传输仍保持字符串字段。完整的区间、无界区间、有限集合与并集语法见
[.NET 插件开发指南](plugin-development.md#2-编写-manifest)。

`database.migrations` 是 Host 能力，不是 JavaScript API。它与 .NET 插件使用完全相同的发布 artifact：把 Prisma、
Drizzle 或其他开发期 ORM 的最终升级路径导出为 SQLite SQL，按 `001_init.sql`、`002_add_index.sql` 的形式放入声明目录。
每个版本携带完整的追加式历史；Host 在 `activate` 之前用事务执行待应用文件，并校验已应用文件的 SHA-256。
Web iframe 不会获得数据库连接；需要读写业务数据时仍应通过 Host capability 或已加载 .NET 插件的 RPC 服务。

完整的命名、事务和 forward-only 升级规则见 [.NET 插件开发指南的持久化章节](plugin-development.md#6-提供静态资源和-web-贡献)。

## 3. 生命周期与页面

```ts
import { definePlugin } from "@io7/quantum-plugin-sdk";

export default definePlugin({
  async activate(context) {
    await context.log.info("started");
    return () => context.log.info("stopped");
  },

  async mount({ element, route, locale, signal }) {
    element.textContent = locale.languageName === "zh" ? "你好" : "Hello";
    signal.addEventListener("abort", () => element.replaceChildren(), { once: true });
    return () => element.replaceChildren();
  }
});
```

- `activate` 每代 runtime 调用一次；其返回的 cleanup 在 `deactivate` 前调用。
- `mount` 在路由展示时调用；切走路由时先 abort `signal`，再调用 cleanup 和 `unmount`。
- `locale` 包含当前 `cultureName`、`languageName` 和 `textDirection`。语言变化时宿主会 unmount 后使用新 locale 再次 mount；
  插件应在每次 mount 中选择文案并使用 `Intl`/`toLocaleString` 格式化日期与数字。
- `context.signal` 在整个 runtime 停止时 abort。
- 即使插件没有正确清理，宿主最终也会删除 iframe，浏览上下文中的 DOM、定时器和事件随之释放。

`IQuantumPlugin.ConfigureServices` 配置的是 .NET 插件运行代的 `IServiceCollection`，因此 Web 插件没有对应的
TypeScript API。Web runtime 不拥有 .NET DI 容器，继续通过 `activate`/cleanup 管理状态和生命周期，并通过
`context` 使用宿主能力。

## 4. 宿主能力

SDK 提供日志、导航、环境快照、Notify、资源读取和 Request：

```ts
const environment = await context.environment.snapshot();
await context.navigation.navigate("/");
const text = await context.assets.readText("data/default.json", { signal });
const imageUrl = context.assets.url("images/icon.png");
```

iframe 的 CSP 禁止直接网络连接。文本资源可通过 `assets.readText` 读取，单次上限 2 MiB；图片、字体和样式可以使用
`assets.url()` 返回的宿主 URL。

## 5. 发布和订阅 Notify

Web 与 .NET 插件共用 Notify。发布方在 `capabilities.provides` 列出能力名，并在 `capabilities.notifications` 声明 JSON Schema。订阅方按能力名注册，在读取字段前收窄 `unknown` 类型的载荷。

```ts
const subscription = await context.notify.subscribe("devices.status", notification => {
  const payload = notification.payload;
  if (payload !== null && typeof payload === "object"
      && "state" in payload && typeof payload.state === "string") {
    return context.log.info(payload.state);
  }
});

await context.notify.publish("devices.status", { state: "ready" });
await subscription.dispose();
```

在清理时释放订阅。Host 等待当前观察者完成，记录投递错误，并在 iframe 运行代停止时移除订阅。

## 6. 调用 Request 能力

```ts
const result = await context.request<MyResult>(
  "notes.find",
  { text: "quantum" },
  { traceId: "web-search" },
  { signal });

if (!result.isSuccess) {
  throw new Error(`${result.errorCode}: ${result.message}`);
}
console.log(result.value.outcomes);
```

服务端方法来自带 `[TransportOverQuantum]` 的 NOF `IRpcService`。

使用能力声明中的小写名称，例如 notes.find。宿主按能力声明的策略分发，调用方可读取各提供方的结果。

第二个参数是与服务端单一 request 类型对应的 JSON Payload，第三个参数是可选 Context 字典。调用方插件 id 和 runtime id
由 Host 写入保留 Context 项，插件不能伪造。未实现的有效名称返回空的 `outcomes`。提供方的载荷错误和业务失败记录在各自的 outcome 中，调用方可通过 AbortSignal 取消整次请求。

Quantum 0.1.8 起，枚举使用名称字符串，例如 `{ kind: "Expense" }`；旧数字形式和数字字符串均会被拒绝。
Schema 与宿主实际序列化配置一致。带构造参数默认值的字段可省略，例如 `note` 默认 `""`；显式 `null` 必须满足字段
可空规则。`rpc_invalid_payload` 的 `message` 会指出字段，`extra.path` / `extra.reason` 提供 JSON 路径和原因，
例如 `$.kind` 或 `$.lines[0].kind`。无效载荷在进入 Handler 前即被拒绝。

RPC 不要求 manifest 声明 `dependency` 或 `integration`。每次调用在目标插件创建独立 DI scope，等待 NOF 生成的 Handler
完成，在 Result 序列化后释放 scope；iframe 不接触 CLR TypeName、`IServiceProvider`、服务实例或反射重载。

## 7. 构建与调试

```bash
npm run build
```

把 `plugin.json` 和 `wwwroot` 复制到 `Modules/<plugin-id>` 后执行“重新扫描 Modules”。仓库内的
[Web 示例](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/samples/Quantum.ExampleWebPlugin/package.json) 展示了 iframe 页面、环境查询、导航，以及通过能力名向
[.NET 示例](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/samples/Quantum.ExamplePlugin/Quantum.ExamplePlugin.csproj) 发起 `example.handshake` 调用；打开 .NET 示例页可以看到 JS 发起的累计握手次数。Web 示例页的「收集建议」按钮调用 `example.suggestions`，显示 Contributor 的插件 ID 和答复。

也可以把 `plugin.json` 与 `wwwroot` 放在同一个插件目录中打成 ZIP，直接拖到 Quantum 窗口中的任意位置安装；单插件包与多插件整合包使用相同流程，包内插件目录不能互相嵌套。宿主会与已安装版本按 SemVer 择优，并在确认前用同一份新快照校验 Web 入口、强依赖和所有 .NET / Web 插件的兼容性。完整包结构、安全限制与事务语义见 [.NET 插件开发指南的分发 ZIP 安装包章节](plugin-development.md#8-分发-zip-安装包)。

当前自动化环境验证 manifest、运行时、市场包、TypeScript 类型和 bundle 语法。Windows WebView2 与 Mac Catalyst
WKWebView 仍应各做一次实机验证，尤其关注 sandbox iframe 加载和 `postMessage` 行为。

Web runtime 的 .NET 快照切换和 iframe 激活分属两个异步阶段：新 iframe 激活失败时宿主会报告错误并拒绝旧
runtime 的后续 RPC，但当前版本还不能把 .NET 侧已经提交的插件快照自动回滚。入口 bundle 应尽量把可失败的初始化放在
`activate`，并在发布前同时验证 WebView2 与 WKWebView。

## 静态导航图标

.NET 与 Web 插件的 `plugin.json` 都可在 `ui.routes` 中声明 `"iconFile": "icons/plugin.ico"`，路径相对于插件包的 `wwwroot`。仅接受标准 `.ico`，支持内含 PNG 或 DIB 的多尺寸图标，文件不超过 1 MiB、单帧不超过 256px。禁止绝对路径、URL、路径穿越和符号链接。文件必须随插件一起打包。

`icon` 继续作为文字回退。宿主加载时将 ICO 归一为透明的 64px PNG，桌面导航、移动端底栏与上拉图标菜单共用此图像；转换结果随插件加载缓存，浏览页面和短轮询不会重复读取文件。移动端按需协商图像元数据，旧客户端和旧平台继续使用文字图标。此能力属于宿主 manifest，不增加 .NET/TypeScript SDK 运行时 API。

```json
{
  "path": "/plugins/example",
  "component": "Example.UI.Pages.Home",
  "icon": "◇",
  "iconFile": "icons/plugin.ico"
}
```

Web 插件可以用 `context.capabilities.catalog()` 发现能力、用 `context.notify` 发布和订阅通知，并用 `context.request()` 收集贡献。宿主通过 iframe 桥接处理这些操作，并在释放运行时时清理订阅。声明和用法见 [TypeScript SDK 指南](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/docs/zh-CN/web-plugin-development.md)。
