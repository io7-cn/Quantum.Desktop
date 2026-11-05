# 桌面架构

[English](../en/architecture.md)

架构决策：[0001 — 分离 Windows 用户数据与安装目录](decisions/0001-windows-data.md)。

Windows 存储迁移先于 MAUI 设置和安全存储初始化执行。Windows manifest 的发布者路径分量 `io7-cn` 将 MAUI 数据根目录放在 Velopack 默认的 `Quantum` 安装根目录之外。`Storage/DesktopDataMigration` 暂存完整副本，通过 SQLite 备份 API 取得一致快照，再以原子目录切换和完成标记提交；失败则停止启动。正式构建将个人插件安装到应用数据目录。Windows 卸载包装器在有超时的 Velopack 钩子之外收集默认保留的选择，仅在卸载成功后清除固定的应用自有目录。参见[安装与恢复行为](desktop-releases.md#windows-本地数据与卸载)。

## 组件结构

宿主源码按技术职责组织。`Components` 区分路由页面、视图、布局、路由和宿主适配。`Networking` 区分平台接口与会话、设备中转。

```text
Quantum
    |-- Shell
    |-- Components (Hosting, Layout, Navigation, Pages, Views)
    |-- Networking (Platform, Relay)
    |-- Plugins (Assets, Catalog, Events, Installation, Persistence, RemoteViews, Rpc, Runtime, Web)
    |-- Logging, Localization, Updates
    `-- Platforms
```

- `Quantum` 在一个项目内包含桌面组合根、Blazor UI，以及 `Quantum.Plugins` 下的插件模型、依赖规划、manifest、ALC、文件系统和 Notify 实现；这些内部职责以目录组织，不再拆成独立程序集。
- 同一个 `Quantum.csproj` 提供桌面 MAUI 目标和供测试、API 文档使用的普通 `net10.0` 核心目标；核心目标排除平台启动文件，但编译相同的插件实现源码。
- 已发布的 `Quantum.Plugin.Abstraction` NuGet 包定义 .NET 插件 ABI；程序集名、包名和命名空间均为单数形式 `Quantum.Plugin.Abstraction`，是插件兼容性边界。
- 清单、运行时环境和 Notify 消息使用字符串表示插件 ID、版本、版本范围和能力名。SDK 和宿主通过普通函数校验输入并比较版本。
- Quantum Platform 是独立部署的后端平台，平台级用户身份、权限、插件市场、密码哈希、文件存储、JWT 与 EF Core 持久化均由该宿主组合；其 Contract 通过 `/rpc` 的 JSON-RPC 2.0 暴露，不进入桌面插件 ABI。

## 启动顺序

1. `NOFMauiAppBuilder.Create()` 创建 NOF/MAUI composition root。
2. 扫描 `Modules/*/plugin.json`，执行 schema、runtime 入口与 SQL migration artifact 预校验。
3. 按 `dependencies` 删除强前置缺失、版本不满足和硬循环的候选及其下游。
4. 对剩余插件拓扑排序；兼容且已安装的 `integrations` 形成软排序偏好，但不形成加载门槛。
5. 把每个候选复制到本次进程专属的影子目录；.NET runtime 创建独立的 collectible `PluginLoadContext`，Web runtime 则生成独立 iframe descriptor。
6. .NET runtime 先执行 NOF 生成的全部 `IAssemblyInitializer`，再执行每个 `IQuantumPlugin.ConfigureServices`；
   两条注册链路均完成后建立私有 DI 容器，并发现 `[TransportOverQuantum]` `RpcServer` endpoint。Web runtime
   先验证 `wwwroot` 下的单文件 ESM 并生成 descriptor。
7. 为候选代建立私有环境快照和运行期 DI scope；Host 按依赖顺序先应用该插件尚未执行的 SQL migrations，再从入口程序集发现静态 .NET `IQuantumPlugin` bootstrap 并执行 `StartAsync`。bootstrap 不进入 DI，全部成功后再原子发布到宿主 `PluginCatalog`。
8. MAUI 创建 `BlazorWebView`；路由、菜单、静态文件提供器和 Web 注入订阅同一份动态 `PluginCatalog`。Web descriptor 发布后，宿主才在 opaque-origin iframe 内通过 Blob Module 激活入口。

加载失败按插件隔离；强前置失败的下游插件不会继续加载。弱联动缺失、版本不兼容或形成软循环时，插件仍然加载，
规划器只放弃无法满足的顺序偏好。`integrations` 不构成运行时调用权限；最终已加载状态通过 SDK 的
`IQuantumPluginEnvironment` 暴露，插件可以在没有 integration 声明时按运行环境选择独立模式或联动模式。

## 插件数据库 migration

`.NET` 和 Web runtime 共享 manifest 的 `database.migrations` 能力。Host 只消费插件包中的 SQLite SQL，不加载 EF、
Prisma 或 Drizzle migration runtime。文件用数字前缀确定顺序，每个发布包必须保留完整的追加式历史。Host 的
`__quantum_plugin_migrations` 表以 `plugin_id + migration_name` 为键保存 SHA-256、发布版本和时间；已应用前缀与当前
artifact 不一致时拒绝启动插件。

一个插件本次所有待应用脚本与历史写入处于同一个 SQLite 事务中，并发生在插件 runtime 生命周期启动之前。migration
提交后是 forward-only 的：后续插件生命周期或其他候选启动失败时，运行时代可以切回旧代码，但数据库不会执行降级。
因此发布者必须使用向后兼容的 expand/migrate/contract 方案。Host 管理事务边界，artifact 不能包含事务控制语句。

Web runtime 不把宿主对象直接暴露给 iframe。iframe 只能通过经来源校验的 `postMessage` 与宿主通信；父页面再通过
`DotNetObjectReference` 转发到 capability RPC。插件 RPC 只传递区分大小写的能力名、JSON Payload、Context 与 NOF Result；
每次调用在目标 .NET 插件创建独立 DI scope，并在生成 Handler 完成且结果序列化之后释放。销毁 iframe 会强制终止未正确
清理的定时器、事件和模块全局状态。

.NET Contract 使用 `[TransportOverQuantum]` 声明 NOF `IRpcService`，NOF 生成协议无关 Client 接口、`RpcServer` 映射和
Handler 基类，不生成具体 Quantum Client。Host 按方法上声明的能力名注册 RPC。Request 根据能力契约的 All 或 Ordered 策略分发。目录为每个能力列出分发策略、可用提供方及其 JSON Schema。强依赖保证至少一个所需能力的提供方先启动。

调用方只持有绑定自身 `pluginId + runtimeId` 的 `IQuantumRequester`，不会取得或缓存目标 provider、服务实例和 CLR DTO。Host
在调用方和目标 ALC 两侧分别序列化，运行代停止时先暂停 endpoint 并等待在途调用完成，再清除路由、JSON 类型缓存和
容器引用，因此旧 ALC 可以被 GC 回收。

## 插件 Notify

.NET 插件和 TypeScript iframe 按发布方声明的能力名收发通知。Host 先将消息序列化为 JSON，再通过 NOF 的内存事件传输分发给活跃订阅者。订阅方拿到原始载荷，可以直接读字段，也可以转换为自己的类型。

iframe 会确认每次投递。Host 记录观察者异常，并在释放运行代时移除订阅、清理序列化缓存。插件停止时应主动释放订阅，避免热切换回滚后重复注册。

## 本地化边界

宿主把用户选择归一化为受支持的 BCP-47 culture，并同时更新 `CurrentCulture`、`CurrentUICulture` 和页面
`lang/dir`。路由标题按 manifest 的 `titles` 完整 culture、语言、默认 `title` 顺序回退。.NET 插件与宿主共享
进程 culture，使用标准 `.resx` / `IStringLocalizer<T>`，其卫星程序集由插件 ALC 从 culture 子目录加载。
宿主页面以 `AppStrings.resx` 的 en-US 文本为基线，资源 Key 使用英文标识；简体中文译文存放在
`AppStrings.zh-CN.resx`。新增页面文案时应同时补齐两份资源。
SDK 中的 .NET 示例插件页面也采用英文中性资源、英文 Key 和 `zh-CN` 译文。

Web iframe 不继承 .NET 线程 culture，因此 TypeScript adapter 在每次 `mount` 的
`QuantumPluginViewContext.locale` 中显式传递等价的 culture、语言和文字方向；语言变化会重挂载当前 view。这是两个
runtime adapter 的有意接口差异：本地化语义相同，传递机制分别遵循 .NET culture 与隔离 iframe 消息边界。

## ALC 边界

每个插件拥有自己的 collectible ALC。Windows 使用 `AssemblyDependencyResolver`；Mac Catalyst 不提供该 API，因此按插件目录中的同名 DLL/本机库进行确定性解析。两种平台遵循相同边界：

1. `System.*`、`Microsoft.*`、`NOF.*` 和 `Quantum.Plugin.Abstraction` 委托给默认上下文。
2. 其余托管依赖优先由插件目录解析。
3. 本机库由插件自己的 resolver 解析。

共享 ABI 是类型一致性的必要条件。若插件私载一份 `IQuantumPlugin` 所在程序集，即使命名空间与类型名相同，宿主仍无法把它识别为同一接口。

卸载时先逆序执行 `StopAsync`，随后从目录快照撤销路由、静态资源和 Web 贡献，释放插件 DI 容器，清除 Blazor 的组件类型缓存，最后调用 `AssemblyLoadContext.Unload`。实际内存回收仍由 GC 决定；插件必须在 `StopAsync` 中停止后台任务、解绑宿主事件并释放 JS/.NET 引用，不能把自己的类型写入宿主或第三方库的进程级静态缓存。

## 禁用、卸载与热升级事务

运行时始终从影子副本执行，因此 Windows 上的 `Modules` 源 DLL 不会被入口程序集锁定。热升级或重新扫描按以下事务执行：

1. 从当前 `Modules` 重新读取所有 manifest，并创建完整的新影子快照、ALC 和私有 DI 容器。
2. 新快照无法完整装载时直接丢弃，旧运行时不受影响。
3. 旧生命周期按依赖逆序停止，因此目标插件的所有下游强依赖会先于目标停用；任一停止钩子失败则取消切换，并重新启动已停止的旧生命周期。
4. 新 runtime 针对候选环境快照按依赖顺序应用待执行 migration 并启动生命周期；此时宿主路由与 UI 仍指向旧目录。
5. 任一新启动钩子失败时丢弃候选代并重新启动旧快照；全部成功后才原子发布新目录。
6. 提交成功后释放旧容器、请求旧 ALC 卸载，并延迟清理仍被 GC 引用的影子目录。SQL migration 一旦提交不会随 runtime 回滚而降级。

单插件热升级会重建并重启完整插件快照，使强依赖和弱联动在新版本下重新求值；下游强依赖插件总是在目标之前停止、在目标之后恢复。禁用目标存在直接或传递强依赖时，运行时先返回完整影响清单，只有显式确认后才把目标和清单中的插件目录移动到保留目录 `Modules/disabled`；运行时切换失败会把所有目录移回。重新启用采用相反的目录事务，并重新验证完整快照。卸载同样先确认影响清单，但提交后会物理删除插件目录，不保留可供重新扫描恢复的源文件。

Mac Catalyst 默认 Release 为 AOT-only，外部 IL 程序集无法动态加载。`Quantum` 因此在 Catalyst 目标上强制 `UseInterpreter=true`，并以 `MtouchLink=None` 保留动态组件依赖的程序集与元数据；移除这些约束会导致原生运行时终止或裁剪后的无效程序。该选择会影响性能、包体和发行政策，面向 Mac App Store 前必须单独评审动态插件模式。Windows 目标继续使用常规 CoreCLR。

## WebView 静态文件

MAUI Blazor Hybrid 没有 ASP.NET Core 的 `UseStaticFiles`。Quantum 通过继承 `BlazorWebView.CreateFileProvider`，把宿主资源与插件物理目录组合为 `CompositeFileProvider`。插件资源的虚拟前缀固定为 `_content/{pluginId}`，避免将文件复制到宿主 `wwwroot`。

## 运行边界

- 只有桌面端加载 DLL 插件；移动端不执行插件程序集。
- 插件支持禁用、重新启用、物理卸载、重新扫描和事务式热升级；`Modules/disabled` 不参与普通发现，`disabled` 也是保留插件 id。
- 桌面宿主从本地 `Modules` 目录加载插件，并支持在整个应用窗口拖拽 ZIP 整合包执行预检确认后的文件安装事务。
- HTML/JS 注入属于受信任代码边界，只应加载来源可信且经过审核的插件。
- 桌面宿主支持 Windows 与 macOS。
