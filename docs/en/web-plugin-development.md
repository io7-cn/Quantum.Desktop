# TypeScript plugin development

[简体中文](../zh-CN/web-plugin-development.md)

TypeScript plugins run in an isolated iframe with an opaque origin. This runtime is experimental. Use the [.NET plugin guide](plugin-development.md) for the production development path. See the [working example](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/samples/Quantum.ExampleWebPlugin/package.json) and the code examples in this file.

## 1. Create and bundle the project

Install `@io7/quantum-plugin-sdk` and use a bundler such as esbuild. Keep `plugin.json` at the plugin root, and output one self-contained ECMAScript module under `wwwroot`. Bundle package dependencies into the entry. The iframe cannot resolve runtime package imports.

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

```bash
npm install @io7/quantum-plugin-sdk@0.3.0-rc.3
npm install --save-dev typescript esbuild
```

```json
{
  "scripts": {
    "build": "esbuild src/index.ts --bundle --format=esm --platform=browser --target=es2022 --outfile=wwwroot/dist/plugin.js"
  }
}
```

## 2. Define the manifest

Set the Web runtime entry relative to `wwwroot` with a `.js` or `.mjs` extension. Use safe relative paths. The Web runtime cannot inject `web.head` or `web.postBlazor` into the host page. `showInNavigation` defaults to true, and a hidden route can still be opened by path. Provide `title` and optional BCP-47 `titles` for localization.

The ID and version follow the [.NET manifest rules](plugin-development.md#2-define-the-manifest-and-rpc-contract). Use `versionRange` for dependencies and integrations. SDK types validate plugin IDs, semantic versions and ranges at runtime and keep their wire representation as strings.

For `database.migrations`, ship the same append-only numbered SQLite SQL files as a .NET plugin. The host verifies hashes and runs pending scripts before activation. The iframe accesses business data through host capabilities and .NET plugin RPC. See the [migration rules](plugin-development.md#6-ship-assets-web-contributions-and-migrations).

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

## 3. Activate, mount and clean up

`activate` runs once per runtime generation and may return cleanup, which runs before `deactivate`. `mount` runs for an active route; leaving aborts the view signal before cleanup and unmount. A locale change remounts with the updated culture, language and direction. Runtime stop aborts `context.signal` and removes the iframe. Manage services and state inside that lifecycle. The .NET registration hook configures a .NET dependency injection container and has no TypeScript counterpart.

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

## 4. Use host capabilities

The SDK offers logging, navigation, the plugin environment, topic events, assets and RPC. The iframe's Content Security Policy blocks direct network connections. `assets.readText` reads up to 2 MiB at a time, and `assets.url` provides host URLs for images, fonts and styles.

```ts
const environment = await context.environment.snapshot();
await context.navigation.navigate("/");
const text = await context.assets.readText("data/default.json", { signal });
const imageUrl = context.assets.url("images/icon.png");
```

## 5. Publish and subscribe to Notify

Web and .NET plugins share Notify. Publishers list the capability in `capabilities.provides` and declare its JSON Schema in `capabilities.notifications`. Subscribers use the capability name and narrow the `unknown` payload before reading its fields.

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

Dispose subscriptions during cleanup. The host waits for current observers, logs delivery errors, and releases subscriptions when the iframe runtime stops.

## 6. Request plugin capabilities

Use `context.capabilities.catalog()` to discover capabilities and `context.request()` to call one by its exact lowercase name. The declared policy controls which providers run. Pass the JSON payload and optional context. The host sets protected caller identity fields, dispatches through a scoped .NET handler and serializes a NOF result.

An unavailable capability returns an empty report. Handler and payload failures from individual providers appear in their outcomes. A destroyed runtime or a bridge error can reject the promise. Use string enum values and match the .NET nullability rules. `rpc_invalid_payload` reports the invalid JSON path and reason before the handler executes.

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

Web plugins can use `context.capabilities.catalog()`, `context.notify.publish()`, `context.notify.subscribe()` and `context.request()`. The host forwards these operations through the iframe bridge and disposes subscriptions when the frame runtime is released. See the [TypeScript SDK guide](https://github.com/io7-cn/Quantum.Sdk/blob/v0.3.0-rc.3/docs/en/web-plugin-development.md) for contracts and examples.

## 7. Build, install and verify

Build and put `plugin.json` and `wwwroot` under `Modules/<plugin-id>`, then rescan. The Web example calls `example.handshake` and offers a **Collect suggestions** button that displays replies from `example.suggestions` contributors. You can also [install a ZIP](plugin-development.md#8-distribute-zip-packages).

Verify loading and `postMessage` on Windows WebView2 and macOS WKWebView. The host commits the .NET snapshot before browser activation completes. A failed iframe activation reports the error and rejects further calls from the old runtime; it does not restore the committed .NET snapshot. Test initialization on both WebViews before shipping.

```bash
npm run build
```

## Navigation icons

Use a packaged relative ICO file under `wwwroot`, subject to the [shared icon limits](plugin-development.md#navigation-icons).

```json
{
  "path": "/plugins/example",
  "component": "Example.UI.Pages.Home",
  "icon": "◇",
  "iconFile": "icons/plugin.ico"
}
```
