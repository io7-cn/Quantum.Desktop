# .NET 插件开发

[English](../en/plugin-development.md)

当前版本建议使用 .NET 作为主要的插件开发方式。本篇介绍 .NET 插件；JavaScript/TypeScript 插件支持仍处于实验阶段，许多能力暂不可用，不建议作为主要开发方式，相关实验性功能见 [TypeScript 插件开发](web-plugin-development.md)。NOF `AutoInject` 元数据会注册到每代 .NET 插件独立的 DI 容器，使容器和程序集能在运行时一起释放。

**必须注意：凡是需要让 Quantum、其他插件或 AI 工具发现和调用的业务能力，都必须通过 NOF RPC 暴露。** Razor 页面可用、应用服务注册到 DI、或方法标记为 `public`，均不等于接口已经进入 Quantum RPC 目录。插件应提供带 `[TransportOverQuantum]` 的 `IRpcService` 合约、启用 NOF 源生成器生成 `RpcServer<T>` 的元数据，通过 NOF 原生 `services.AddRpcServer<TServer>()` 注册服务及生成的处理器。发布前必须验证 `quantum.capabilities.list` 能发现接口，并按完整 RPC 名称跨 JSON 边界实际调用；仅验证页面或直接调用应用服务不够。

## 1. 创建项目

使用 Razor SDK 创建类库，并引用稳定的插件 ABI；声明/实现插件 RPC 时同时引用 NOF Contract 与 Application：

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Quantum.Plugin.Abstraction" Version="0.3.0-rc.3" />
    <PackageReference Include="NOF.Abstraction" />
    <PackageReference Include="NOF.Application" />
    <PackageReference Include="NOF.Contract" />
  </ItemGroup>

  <ItemGroup>
    <Content Update="plugin.json" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="migrations\**\*.sql" CopyToOutputDirectory="PreserveNewest" />
    <Content Update="wwwroot\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

生产插件应引用发布后的 `Quantum.Plugin.Abstraction` 与所需 NOF 包，而不是 Quantum 宿主、Application 或
Infrastructure 项目。插件不得携带自己的 ABI 副本版本并尝试覆盖宿主版本。

## 2. 编写 manifest

```json
{
  "id": "quantum.plugin.example",
  "version": "0.1.0",
  "entryAssembly": "Quantum.ExamplePlugin.dll",
  "database": {
    "migrations": "./migrations"
  },
  "dependencies": [
    { "id": "quantum.plugin.core", "versionRange": "[0.1.0,)" }
  ],
  "integrations": [
    { "id": "quantum.plugin.theme", "versionRange": "[0.1.0,)" }
  ],
  "ui": {
    "routes": [
      {
        "path": "/plugins/example",
        "component": "Quantum.ExamplePlugin.Pages.Index",
        "title": "示例插件",
        "titles": { "en-US": "Example plugin" },
        "icon": "✦",
        "order": 100
      },
      {
        "path": "/plugins/example/detail",
        "component": "Quantum.ExamplePlugin.Pages.Detail",
        "showInNavigation": false
      }
    ],
    "globalComponents": [
      {
        "component": "Quantum.ExamplePlugin.Components.FloatingPanel",
        "order": 100
      }
    ]
  },
  "web": {
    "head": [
      "<link rel=\"stylesheet\" href=\"_content/quantum.plugin.example/site.css\">"
    ],
    "postBlazor": [
      "<script src=\"_content/quantum.plugin.example/main.js\"></script>"
    ]
  }
}
```

插件关系分为两类：

| 字段 | 语义 | 缺失或版本不足 | 排序 |
| --- | --- | --- | --- |
| `dependencies` | 强前置 | 当前插件加载失败，依赖它的插件继续级联失败 | 前置必须先加载；硬循环失败 |
| `integrations` | 弱联动 | 当前插件继续以独立模式加载 | 兼容目标尽量先加载；软循环按稳定顺序打破 |

约束：

- `id` 最长 128 个字符，使用小写 ASCII 字母、数字、点、下划线或连字符，首尾必须是字母或数字；
  `disabled` 是宿主保留值。SDK 接收普通字符串。
- `version` 严格使用 SemVer 2.0.0，必须写全 `major.minor.patch`。`dependencies` 和 `integrations` 使用
  `versionRange`，不再接受旧的 `minVersion` 字段。SDK 用字符串表示版本和范围，用 `SemanticVersion.Compare` 和 `VersionRange.Contains` 比较。
- 旧版 `entryAssembly` 继续受支持，等价于 `{ "runtime": { "kind": "dotnet", "entry": "..." } }`；DLL 入口只能是插件根目录下的文件名。
- .NET 路由的 `component` 必须是入口程序集内实现 `IComponent` 的完整类型名；Web 路由改用 `view`。
- `showInNavigation` 默认为 `true`；设为 `false` 的页面不会出现在主导航中，但仍可通过其路径或导航 API 打开。
- `globalComponents` 仅适用于 .NET 插件。宿主会在主布局中持续渲染这些组件，适合悬浮按钮、Drawer 和全局状态提示；
  `component` 必须是入口程序集内的 Blazor 组件完整类型名，`order` 控制多个全局组件的稳定渲染顺序。
- `title` 是路由标题的默认回退；`titles` 可用 BCP-47 culture name 提供本地化标题。宿主先匹配完整 culture，
  再匹配语言，最后回退到 `title`。
- `database.migrations` 对 .NET 和 Web 插件含义相同，指向插件根目录内的 SQL migration artifact。
- 同一目标不能同时出现在 `dependencies` 和 `integrations`，各类关系和路由不能重复；未知 manifest 字段会被拒绝，避免拼写错误静默失效。

`versionRange` 可以组合以下版本集合：

- 区间遵循数学边界规则，例如 `[1.0.0,1.2.0)` 表示 `1.0.0 <= version < 1.2.0`。
- 区间可以无界，例如 `(,1.2.0)`、`(1.3.0,)`；省略边界的一侧必须使用圆括号。`(,)` 表示全部版本，
  `*` 仅作为 `(,)` 的等价助记写法，不提供其他通配符语义。
- 花括号表示有限集合，例如 `{1.2.3}`、`{1.2.3,1.2.4}`；`{}` 非法。
- `|` 表示并集，例如 `{1.2.3} | [1.3.0,1.4.0) | (1.4.0,1.5.0)`。

所有边界与集合元素都必须是 SemVer 2.0.0。预发布版本直接按 SemVer precedence 参与比较，不做额外排除；
例如 `[1.2.3-alpha,1.2.3)` 包含 `1.2.3-beta`。构建元数据完全不参与比较，因此 `{1.2.3}` 也会匹配
`1.2.3+abc`。

完整的强依赖示例见
[`Quantum.ExampleDependentPlugin`](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/samples/Quantum.ExampleDependentPlugin/plugin.json)：它要求
`quantum.plugin.example >= 0.1.0`，并在启动后展示宿主实际解析到的前置插件版本以及一次跨插件方法调用结果。
插件间调用不传递 `IServiceProvider` 或服务实例。服务端声明 NOF RPC Contract，并让 Host 通过 Quantum transport 暴露
生成的 Handler：

```csharp
public sealed record CreateGreetingRequest;

[TransportOverQuantum]
public interface IExampleRpcService : IRpcService
{
    [RequestCapability("example.greet", QuantumDispatch.All)]
    Result<string> CreateGreeting(CreateGreetingRequest request);
}

public partial class ExampleRpcService : RpcServer<IExampleRpcService>;

public sealed class CreateGreeting(ExampleState state) : ExampleRpcService.CreateGreeting
{
    public override Task<Result<string>> HandleAsync(
        CreateGreetingRequest request,
        Context context,
        CancellationToken cancellationToken)
        => Task.FromResult<Result<string>>(state.CreateGreeting());
}
```

每个 RPC 方法通过 [RequestCapability] 声明一个稳定的能力名，并在 plugin.json 的 capabilities.provides 中列出。调用时使用准确的小写能力名，例如 example.greet。通过 Request 报告处理各提供方的结果。

调用方不需要引用实现插件 DLL，只从自己的 DI 获取 `IQuantumRequester`：

```csharp
var result = await services.GetRequiredService<IQuantumRequester>().RequestAsync<string>(
    "example.greet",
    new { },
    Context.Empty,
    cancellationToken);
```

未实现的有效能力名返回空的 `outcomes`。Payload、Context 和 Result 都经过 JSON 序列化边界；Host 每次调用创建
目标插件 scope，并在 Handler 完成且结果序列化后释放。`dependencies` 仍用于确保提供方先启动、后停止，但不作为 RPC
访问权限；没有依赖或 integration 声明的已加载插件也可以被调用。

开发时不要只添加合约文件：还需要声明 `partial class ... : RpcServer<I...>`，从生成的处理器基类派生实现，并在插件 DI 中调用 NOF 原生 `services.AddRpcServer<ExampleRpcService>()`；生成的程序集初始化器会注册处理器，不应重复手写 AddTransient。宿主依赖生成的 `ServiceType` 和 `HandlerMappings` 发现方法；缺失 `[TransportOverQuantum]`、禁用所需源生成器或漏注册处理器，都会导致目录不可发现或调用失败。为服务、方法和请求属性补充 `DescriptionAttribute`，让调用方能理解业务含义、金额单位、枚举值及副作用。

Host 还内置 `quantum.capabilities.list`，用于发现当前可用的 Request 和 Notify 能力，插件无需声明依赖即可读取：

```csharp
var catalog = await services.GetRequiredService<IQuantumRequester>()
    .DiscoverCapabilitiesAsync(cancellationToken);
```

目录 schema 2 的 capabilities 是平铺列表。每项包含能力名、当前提供方插件 ID 列表、请求和响应类型，以及可直接提供给 AI 工具的 JSON Schema。

从 Quantum 0.1.8 起，RPC 的 Schema 由实际序列化器的 `JsonSerializerOptions` 通过 .NET JSON Schema exporter 生成。
默认使用字符串枚举转换器并禁用整数枚举：请求和响应中枚举以名称字符串传输，例如 `"kind": "Expense"`，不再兼容
`"kind": 0` 或 `"kind": "0"`。枚举上的 `JsonStringEnumMemberName`、属性上的 `JsonPropertyName` / `JsonIgnore`
与序列化规则一致。可空类型、字典、集合和递归类型也由同一份序列化元数据导出。

无默认值的必需构造参数会被列入 `required` 并在载荷缺失时拒绝；带默认值的参数可省略，例如 `string Note = ""`
生成 `default: ""` 而不列入 `required`，反序列化时保留该默认值。可省略不等于可传 `null`：非空字符串显式传 `null`
仍会失败。RPC 请求本身不能为 `null`。

载荷错误返回 `rpc_invalid_payload`，`message` 包含 JSON 路径和转换原因，`extra` 提供 `rpc`、`path`、`reason`
三个字符串字段。例如旧数字枚举会指出 `$.kind`，嵌套数组错误会指出 `$.lines[0].kind`。错误在调用 Handler 前返回，
不会因反序列化失败触发业务写入。Web 插件与 .NET `IQuantumRequester` 使用相同的规则和诊断信息。

在服务接口、方法、唯一请求参数、返回值、DTO 类型和 DTO 属性上声明的全部 Attribute 都会以类型名、构造参数和命名参数
导出；`DescriptionAttribute` 还会提升为相应节点的 `description`。元数据读取使用 `CustomAttributeData`，不会为了发现接口而
实例化 Attribute。Web 插件可使用 `context.capabilities.catalog()` 获取同一份目录。

## 3. 注册服务和启动逻辑

插件装载时有两条独立的服务注册链路：宿主先执行程序集中的所有 NOF `IAssemblyInitializer`，再调用入口程序集内
每个 `IQuantumPlugin.ConfigureServices`。两条链路始终都会执行；显式 `ConfigureServices` 可以追加或覆盖生成注册，
并有一个什么都不做的默认实现。`IQuantumPlugin` 是由入口程序集静态发现的 bootstrap，不需要用 `AutoInject`
注册，也不会创建实例。业务状态、后台任务与可观察数据应放在普通服务中；bootstrap 直接使用宿主传入的插件运行期
scope 完成启动和停止编排：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Quantum.Plugin.Abstraction;

public interface IExampleState
{
    DateTimeOffset? StartedAt { get; }
}

public sealed class ExampleState
{
    public DateTimeOffset? StartedAt { get; private set; }

    internal void MarkStarted() => StartedAt = DateTimeOffset.Now;
}

[AutoInject(
    ServiceLifetime.Singleton,
    RegisterTypes = [typeof(IExampleState)])]
public sealed class ExampleStateView(ExampleState state) : IExampleState
{
    public DateTimeOffset? StartedAt => state.StartedAt;
}

public sealed class ExamplePlugin : IQuantumPlugin
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ExampleState>();
    }

    public static Task StartAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        services.GetRequiredService<ExampleState>().MarkStarted();
        var environment = services.GetRequiredService<IQuantumPluginEnvironment>();
        if (environment.IsPluginLoaded(PluginId.Of("quantum.plugin.theme")))
        {
            // 目标当前可用时启用适配逻辑；无需预先声明 integration。
        }

        return Task.CompletedTask;
    }

    public static Task StopAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
```

上例中的 `ExampleStateView` 由 NOF `AutoInject`/`IAssemblyInitializer` 注册，`ExampleState` 则由
`IQuantumPlugin.ConfigureServices` 注册，说明两条链路可以在同一插件内同时使用。

每代 .NET 插件拥有一个由宿主管理的 DI scope。宿主从入口程序集发现 `IQuantumPlugin` 实现类型，通过静态接口
分派调用配置与生命周期；bootstrap 本身不进入 DI。`StartAsync` 与 `StopAsync` 收到同一个 scoped
`IServiceProvider`，runtime 销毁时 scope 才会释放。这与 Blazor WebAssembly
中的 scope 很接近：在主插件 scope 内 `Singleton` 与 `Scoped` 通常各只有一个实例，但使用 `Scoped` 能阻止
代码意外从根 provider 解析 scoped 服务。需要跨 Web RPC 调用共享的状态仍应注册为 `Singleton`，因为每次 RPC
会建立自己的调用 scope。

`IQuantumPlugin.StartAsync` 针对候选环境快照按依赖顺序调用，全部成功后宿主才发布新目录；`StopAsync` 在卸载或热切换前按逆序调用。两个生命周期方法都必须实现；`ConfigureServices` 可以省略并使用默认空实现。即使无需清理，`StopAsync` 也应明确返回 `Task.CompletedTask`。

`IQuantumPluginEnvironment` 提供实时的 `LoadedPlugins` 和 `IsPluginLoaded`。`integrations` 只是供加载规划器参考的
声明性软排序提示，不是联动逻辑或跨插件调用的准入条件；插件可以直接根据运行环境决定是否启用适配逻辑。
`IQuantumPluginRuntimeContext` 可用于读取当前插件版本与本代只读影子目录。插件容器会同时执行 NOF 生成的
`AutoInject` 初始化器和 bootstrap 的 `ConfigureServices`，但动态插件不会加入宿主根 Application Part；依赖宿主级全局扫描的 Handler 或
Initialization Step 不属于可热卸载边界，应由稳定的宿主 Contract 显式桥接。

## 4. 发布和订阅 Notify

发布方在 `plugin.json` 的 `capabilities.provides` 和 `capabilities.notifications` 中声明能力名及 `messageSchema`。订阅方按能力名订阅，可以读取 `JsonElement`，也可以用 `TryDeserialize<T>()` 转成自己的 DTO。

```csharp
var notify = services.GetRequiredService<IQuantumNotifier>();
var subscription = notify.Subscribe("devices.status", (notification, token) =>
{
    if (notification.Payload.ValueKind == JsonValueKind.Object
        && notification.Payload.TryGetProperty("state", out var state)
        && state.ValueKind == JsonValueKind.String)
    {
        logger.LogInformation("Device state is {State}", state.GetString());
    }

    return Task.CompletedTask;
});

await notify.PublishAsync("devices.status", new { state = "ready" }, cancellationToken);
```

在插件停止时释放 `subscription`。Host 也会随运行代清理订阅。默认 `All` 向所有活跃订阅者投递。需要控制传播时使用 `Observe`，并在发布方清单中声明 `Ordered` 和允许停止的插件 `stopObservers`。观察者的异常由 Host 记录，其他观察者可继续接收。

### Request 与 Notify 能力

用 [RequestCapability] 声明处理器，并在契约中选择 All 或 Ordered。宿主通过 NOF 在独立 scope 中调用处理器，Ordered 写请求可要求唯一归属。Notify 使用同样的 All 和 Ordered 策略，通过 NOF 在本机内存投递。能力发现、归属和传播规则见 [SDK 能力指南](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/docs/zh-CN/dotnet-plugin-development.md)。

## 5. 提供页面

页面不需要 `@page`，路由以 manifest 为准：

```razor
@namespace Quantum.ExamplePlugin.Pages
<h1>Example</h1>
<p>Started at @State.StartedAt</p>

@code {
    private readonly IExampleState State;

    // 插件私有服务使用构造注入，由 Quantum 的插件组件激活器从私有容器解析。
    public Index(IExampleState state)
    {
        State = state;
    }
}
```

宿主校验类型后使用 `DynamicComponent` 渲染，并根据 manifest 的 `title`、`icon` 和 `order` 生成菜单。插件私有服务必须使用构造注入；Blazor 的 `@inject` 属性仍由宿主渲染器处理，只适合注入宿主公开的稳定服务。为避免 Router 缓存旧程序集，动态插件页面只使用 manifest 路由，不支持插件程序集内的 `@page` 自动发现。

### 本地化插件页面

.NET 插件使用标准 `IStringLocalizer<T>` 与 `.resx`。把中性资源和语言资源放在 marker type 对应路径下，例如
`Localization/PluginStrings.resx` 与 `Localization/PluginStrings.en-US.resx`，然后在组件中注入：

```razor
@using Microsoft.Extensions.Localization
@inject IStringLocalizer<PluginStrings> L

<h1>@L["页面标题"]</h1>
```

日期、数字和复数相关逻辑应读取 `CultureInfo.CurrentCulture`，不要固定 `zh-CN` 或 `en-US`。构建/分发插件时必须保留
`<culture>/<PluginAssembly>.resources.dll` 目录；宿主的可回收加载上下文会按当前 culture 探测这些卫星程序集。
宿主切换语言后会以新 culture 重新创建当前动态插件页面。

## 6. 提供静态资源和 Web 贡献

将资源放在插件输出目录的 `wwwroot` 下。宿主映射规则为：

```text
<plugin-root>/wwwroot/site.css
    -> _content/<plugin-id>/site.css
```

`head` 和 `postBlazor` 接受 HTML 片段。该能力等同于在宿主内执行代码，只应安装来源可信且经过审核的插件。

动态插件不会在宿主编译期作为 Razor 项目引用，因此 CSS isolation 生成的 project bundle 需要由插件项目复制到输出目录的
`wwwroot`，再通过 manifest 的 `web.head` 引用。例如：
`Calendar.razor.css` 构建为 `Quantum.CalendarPlugin.bundle.scp.css`，组件的 scope attribute 与 bundle
选择器仍由 Razor SDK 自动生成。

同一个日历示例也演示了宿主托管的共享持久化。发布包以 manifest 声明唯一、语言无关的 migration 能力：

```json
{
  "database": {
    "migrations": "./migrations"
  }
}
```

artifact 是插件根目录下的累积 SQL 历史，与运行时和开发 ORM 无关：

```text
migrations/
├── 001_init.sql
├── 002_add_index.sql
└── 003_add_status.sql
```

当前 Host 数据库是 SQLite，因此发布 SQL 必须使用 SQLite 方言。开发阶段可以自由使用 EF Core、Prisma、
Drizzle 或其他工具管理模型，但发布前必须把最终升级路径导出为这些 SQL 文件，随插件包一起交付。Host 不加载
ORM migration assembly，也不执行 Prisma/Drizzle 的运行时 migration metadata。

artifact 约束如下：

- 目录相对于插件根目录；`./migrations` 与 `migrations` 等价，不能使用绝对路径、反斜杠或 `..`。
- 目录不允许嵌套，至少包含一个非空 UTF-8 文件；文件名使用 `<数字>_<描述>.sql`，数字序号必须唯一并按数值排序。
- 每个新版本必须携带从第一条开始的完整历史，只能在末尾追加文件。已经发布的文件不得改名、删除或修改内容。
- Host 在插件 `StartAsync` 之前执行待应用文件，并把该插件的整批待应用 SQL 和历史记录放在同一个事务中；SQL 文件不要自行执行 `BEGIN`、`COMMIT` 或 `ROLLBACK`。
- Host 在 `__quantum_plugin_migrations` 中按插件记录文件名、SHA-256、发布版本和应用时间。checksum 漂移、历史缺失或中间插入会阻止新 runtime 启动。

migration 是 forward-only 的持久化提交。新 runtime 后续启动失败时 Host 可以恢复旧代码运行，但不会执行 `Down`
或撤销已经成功提交的 schema；升级 SQL 应采用 expand/migrate/contract，先添加兼容结构，等不再需要旧版本后再删除旧结构。

`Quantum.OfficialPlugins/Calendar/Application/CalendarApplicationService` 只依赖 `NOF.Domain.IRepository<CalendarEntry>` 与
`NOF.Application.IDbContext`；插件自身最多引用 `NOF.Infrastructure`，不引用 EF Core、SQLite provider 或宿主持久化项目。
Quantum 的本地优先数据库遵循 NOF 的默认硬删除行为。确有恢复或审计需求的插件才应显式启用软删除，并自行提供相应迁移。
运行期仍可通过纯 NOF 抽象贡献 EF 模型：

```csharp
internal sealed class CalendarDbContextModelCreatingContributor
    : IDbContextModelCreatingContributor
{
    public void Configure(IDbModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CalendarEntry>(entity =>
        {
            entity.ToTable("OfficialCalendarEntries");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(120).IsRequired();
        });
    }
}
```

插件初始化器将 contributor 注册为 `IDbContextModelCreatingContributor`。Quantum 宿主检测到该注册后，向该插件的
私有 DI 容器加入 NOF EF adapter；contributor 定义运行期对象映射，发布 SQL artifact 定义版本间 schema 演进。
页面通过构造注入的应用服务读写事项。所有插件连接同一个宿主数据库：

```text
<ApplicationData>/quantum.db
```

每个插件仍使用自己的 DbContext/DI 生命周期和只包含本插件实体的 EF 模型，以免宿主根容器持有可卸载程序集；它们共享的是
物理 SQLite 文件。表名位于全局命名空间，插件应使用稳定且包含插件标识的表名，避免与其他插件冲突。宿主关闭 SQLite
连接池和 EF provider cache，确保 DbContext、contributor 与 collectible ALC 能一起释放。

不要把数据库写入 `IQuantumPluginRuntimeContext.RootPath`；该路径属于当前运行代的只读影子副本，热升级或卸载后会被清理。

目录热切换时，宿主会移除旧 DOM 节点并应用新片段；已经执行的脚本副作用不会因删除 `<script>` 自动撤销。插件若注册了全局事件、定时器或 JS/.NET 引用，必须通过 `StopAsync` 调用自己的清理逻辑。

## 7. 调试

先构建插件，再让宿主扫描包含插件子目录且宿主有权访问的根路径：

```bash
dotnet build MyPlugin.csproj

QUANTUM_MODULES_PATH="$PWD/Modules" \
dotnet build src/Quantum/Quantum.csproj -t:Run -f net10.0-maccatalyst
```

示例目录：

```text
Modules/
└── quantum.plugin.example/
    ├── plugin.json
    ├── Quantum.ExamplePlugin.dll
    ├── ThirdParty.Dependency.dll
    ├── migrations/
    │   └── 001_init.sql
    └── wwwroot/
        ├── site.css
        └── main.js
```

正式构建默认使用应用数据中的 `Modules`，Windows 路径为 `%LOCALAPPDATA%\io7-cn\io.quantum.desktop\Data\Modules`；Debug 构建可使用随构建暂存的测试插件。`QUANTUM_MODULES_PATH` 指定的外部目录由用户管理，卸载应用时始终保留。

`disabled` 是宿主保留的插件 id，任何 .NET 或 Web 插件都不能使用该名称。宿主将
`Modules/disabled` 专门用于保存已禁用插件，普通扫描不会把它当成插件目录。

插件程序集依赖优先从自身影子目录解析；`System.*`、`Microsoft.*`、`NOF.*` 与 Quantum 插件 ABI 始终与宿主共享。
RPC Payload 与 Result 只要求 JSON 结构兼容，不依赖两个 ALC 中恰好同名的私有 CLR 类型。需要使用 NOF 生成的强类型
Client 接口时，可以把 Contract 与 DTO 放入双方共同引用的稳定 Contract 程序集；名称路由的 `IQuantumRequester` 不要求该引用。

调试热升级流程：

1. 在 `Modules/<plugin-id>` 中替换 DLL、manifest 和 `wwwroot`；正在运行的入口 DLL 来自影子目录，因此源文件可直接覆盖。
2. 推荐同步提升 manifest `version`，便于确认切换结果。
3. 在 Quantum 设置页点击“热升级”；也可以点击“重新扫描 Modules”重建整个插件快照。
4. 新快照的装载、SQL migration 或 `StartAsync` 失败时，宿主保留/恢复旧 runtime 并显示失败原因；已经成功提交的 forward-only migration 不回滚。

设置页的“禁用”会先列出完整的直接和传递强依赖清单并要求确认；确认后，下游插件按依赖逆序先于目标停止，相关物理目录统一移入 `Modules/disabled`。重新启用时目录移回 `Modules`，只有完整依赖、兼容性和运行时暂存检查通过后才提交，失败会回滚目录移动。“卸载”则是不可恢复操作：目标及级联强依赖停止后，其物理目录会被永久删除；已禁用插件也可以直接永久卸载。为了让 collectible ALC 真正被 GC 回收，插件必须在 `StopAsync` 后消除所有后台任务、事件、委托、JS interop 和共享静态缓存对插件对象或类型的引用。

启用应用沙箱的 Mac Catalyst 构建，不能直接扫描普通工作区目录。macOS 调试时请把插件复制到应用数据的 `Modules` 目录；环境变量指定的目录不可访问时，宿主会回退到应用数据目录。

## 8. 分发 ZIP 安装包

Quantum 在任意界面、窗口内任意位置都可以拖入 `.zip` 安装插件。所有 ZIP 都按整合包处理，每个插件占用一个独立文件夹；只含一个插件的 ZIP 是同一格式的特殊情况：

```text
community-pack.zip
├── quantum.plugin.notes/
│   ├── plugin.json
│   └── Quantum.NotesPlugin.dll
└── quantum.plugin.theme/
    ├── plugin.json
    └── wwwroot/
        └── dist/plugin.js
```

插件目录可以位于一层包装目录下，但插件目录之间不能嵌套。ZIP 内路径必须使用 `/`，不能包含绝对路径、`..`、反斜杠、盘符或符号链接。宿主当前限制单个 ZIP 不超过 512 MiB、解压后不超过 2 GiB、20,000 个条目和 256 个插件。

宿主解压到临时目录后执行以下预检，不会立即改动 `Modules`：

1. 使用与普通目录加载相同的规则验证 manifest、runtime 入口和 migration artifact。
2. ZIP 内同一插件有多个版本时按 SemVer 选择最高版本；与已安装插件重复时，只有严格更新的包内版本会进入安装清单，相同或更旧版本显示为“跳过”。同一最高版本对应多个目录会被视为歧义并拒绝；若同 id 插件位于 `Modules/disabled`，必须先重新启用或清理禁用版本，整包安装会被拒绝。
3. 将候选版本与现有 `Modules` 合成完整的新快照，校验所有强依赖、最低版本和依赖环，并实际暂存 .NET ALC / DI 或 Web runtime 入口以发现 ABI、路由及服务注册错误。预检阶段不调用插件生命周期，也不执行数据库 migration。
4. 任一插件失败会拒绝整个 ZIP。全部通过后，界面展示“新安装 / 升级 / 跳过”清单并要求确认。

确认后宿主才以文件事务更新 `Modules` 并热切换完整运行时快照。文件替换、runtime staging 或生命周期启动失败时会恢复旧插件目录和旧 runtime。SQL migration 仍遵循 forward-only 规则：一旦在确认后的启动阶段提交，不会随着后续生命周期失败反向执行。

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
