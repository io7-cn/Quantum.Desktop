# Quantum Desktop

[简体中文](../zh-CN/README.md)

Quantum Desktop is a local-first plugin host for Windows and macOS, built with .NET 10, NOF and .NET MAUI Blazor Hybrid. It loads .NET plugins in isolated, unloadable runtime contexts and runs experimental TypeScript plugins in sandboxed browser frames. The mobile companion displays plugin views while Desktop executes their actions.

| Directory | Purpose |
| --- | --- |
| `src/Quantum/` | Desktop host, Blazor UI and plugin runtime |
| `src/Quantum.UpdateAgent/` | macOS update helper |
| `tests/` | Desktop tests and test fixtures |
| `docs/` | Desktop and plugin documentation |
| `.ci/` | Packaging, signing and release scripts |

The desktop solution restores the .NET plugin contract from NuGet. The Web fixture restores the matching npm package. The host uses its platform client and device protocol to reach the separately deployed Quantum Platform service.

## Development and release

Use the .NET 10 SDK and the MAUI workload for your operating system. Run `dotnet build Quantum.Desktop.slnx` from the repository root. Run `dotnet test tests/Quantum.Tests/Quantum.Tests.csproj -p:QuantumCoreOnly=true` for the plain `net10.0` tests. Native Debug builds stage the plugin fixtures in the app output. Windows Debug uses the self-contained Windows App SDK.

The plugins under `tests/Fixtures/Plugins/` are fixed host test fixtures derived from the [SDK examples](https://github.com/io7-cn/Quantum.Sdk/tree/v0.3.0-rc.3/samples). Build the optional Web fixture with `npm ci` and `npm test` in its project directory to include it in a native Debug build.

Logs go to `Logs/quantum-YYYYMMDD-HHmmss-PID-SEGMENT.log` under application data, with a 20 MiB threshold per segment. The file writer rolls before a new entry would exceed the threshold and keeps a single oversized entry intact. Pass `--console` to also see logs in the terminal. With `dotnet run`, append `-- --console` to the launch command.

See [Login sessions](login-sessions.md), [Architecture](architecture.md), [Runtime extensions](extensions.md), [Desktop releases](desktop-releases.md), the [.NET plugin guide](plugin-development.md), the [TypeScript plugin guide](web-plugin-development.md) and [Contributing](CONTRIBUTING.md). Licensed under [Apache License 2.0](../../LICENSE).

## Launch commands

macOS

```bash
dotnet build src/Quantum/Quantum.csproj -t:Run -f net10.0-maccatalyst
```

Windows

```powershell
dotnet run --project src/Quantum/Quantum.csproj --framework net10.0-windows10.0.19041.0
```

Attribution and third-party license notices are provided in [NOTICE](../../NOTICE).

## Development standards

Follow the [English development standards](../../.standards/docs/en/README.md). The .standards submodule records the exact reviewed revision; upstream changes apply only after an explicit update of that recorded commit. Initialize it before development:

```sh
git submodule update --init --recursive
python .standards/.ci/check-adoption.py
```

Keep both language editions aligned. AGENTS.md links to the adopted English entry point; project-specific instructions supplement the shared requirements.
