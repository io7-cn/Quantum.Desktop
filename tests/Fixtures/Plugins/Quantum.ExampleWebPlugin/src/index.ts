import { definePlugin } from "@io7/quantum-plugin-sdk";
import { createExtensionExample } from './extension-example';

let extensionExample: Awaited<ReturnType<typeof createExtensionExample>> | undefined;

interface ExamplePluginHandshake {
  message: string;
  sequence: number;
  dotNetStartedAt: string;
  webPluginAvailable: boolean;
}

export default definePlugin({
  async activate(context) {
    await context.log.info("TypeScript example activated");
    extensionExample = await createExtensionExample(context);
    const notification = "example.web.status";
    const subscription = await context.notify.subscribe(notification, event => {
      const payload = event.payload;
      if (payload !== null && typeof payload === "object" && "state" in payload && typeof payload.state === "string") {
        return context.log.info(`${event.publisher.id} published ${payload.state} to ${event.capabilityName}`);
      }
    }, { signal: context.signal });
    await context.notify.publish(notification, { state: "activated" }, { signal: context.signal });
    return async () => {
      await extensionExample?.dispose();
      extensionExample = undefined;
      await subscription.dispose();
      await context.log.info("TypeScript example deactivated");
    };
  },

  async mount(context) {
    if (context.route.view === "settings") {
      return extensionExample?.mount(context);
    }
    const text = createMessages(context.locale.languageName);
    context.element.innerHTML = `
      <style>
        body { color: #29243d; background: #fff; font-family: Inter, system-ui, sans-serif; }
        .page { padding: 42px; }
        .tag { color: #6653e8; font-size: 12px; font-weight: 800; letter-spacing: .12em; }
        h1 { margin: 10px 0; font-size: 38px; }
        p { color: #716b7e; line-height: 1.7; }
        .interop { margin: 24px 0; padding: 18px; border-radius: 14px; background: #f4f1ff; }
        .interop strong { display: block; color: #342974; margin-bottom: 6px; }
        .actions { display: flex; gap: 10px; flex-wrap: wrap; }
        button { border: 0; border-radius: 10px; padding: 10px 14px; color: #fff; background: #6553e7; cursor: pointer; }
        button.secondary { color: #554a80; background: #ece8fb; }
        button:disabled { cursor: wait; opacity: .65; }
      </style>
      <section class="page">
        <div class="tag">${text.tag}</div>
        <h1>${text.heading}</h1>
        <p>${text.currentView}: ${escapeHtml(context.route.view)}</p>
        <p data-environment-status>${text.readingEnvironment}</p>
        <div class="interop">
          <strong>${text.interopHeading}</strong>
          <p data-handshake-status>${text.waitingForEnvironment}</p>
        </div>
        <div class="interop">
          <strong>${text.collectHeading}</strong>
          <p role="status" data-collect-status></p>
          <ul data-collect-results></ul>
          <button type="button" data-action="collect">${text.collectButton}</button>
        </div>
        <div class="actions">
          <button type="button" data-action="handshake">${text.handshakeAgain}</button>
          <button type="button" class="secondary" data-action="dotnet">${text.openDotNet}</button>
          <button type="button" class="secondary" data-action="settings">${text.openSettings}</button>
          <button type="button" class="secondary" data-action="home">${text.backHome}</button>
        </div>
      </section>
    `;

    const status = context.element.querySelector<HTMLElement>("[data-handshake-status]");
    const environmentStatus = context.element.querySelector<HTMLElement>("[data-environment-status]");
    const handshakeButton = context.element.querySelector<HTMLButtonElement>("[data-action=handshake]");
    let dotNetPluginAvailable: boolean | null = null;

    const requestHandshake = async () => {
      if (dotNetPluginAvailable === false) {
        if (status) {
          status.textContent = text.dotNetUnavailable;
        }
        return;
      }

      if (handshakeButton) {
        handshakeButton.disabled = true;
      }
      try {
        if (status) {
          status.textContent = text.connecting;
        }
        const result = await context.request<ExamplePluginHandshake>(
          "example.handshake",
          {},
          {},
          { signal: context.signal });
        if (!result.isSuccess) {
          throw new Error(`${result.errorCode}: ${result.message}`);
        }
        const [outcome] = result.value.outcomes;
        if (result.value.outcomes.length !== 1 || outcome?.status !== "handled" || !outcome.value) {
          throw new Error(outcome?.message ?? "No provider returned a handshake.");
        }
        const handshake = outcome.value;
        if (status) {
          status.textContent = handshake.message;
        }
      } catch (error) {
        if (!context.signal.aborted && status) {
          status.textContent = text.handshakeFailed(
            error instanceof Error ? error.message : String(error));
        }
      } finally {
        if (handshakeButton && !context.signal.aborted) {
          handshakeButton.disabled = false;
        }
      }
    };

    const collectButton = context.element.querySelector<HTMLButtonElement>("[data-action=collect]");
    const collectStatus = context.element.querySelector<HTMLElement>("[data-collect-status]");
    const collectResults = context.element.querySelector<HTMLUListElement>("[data-collect-results]");

    collectButton?.addEventListener("click", async () => {
      if (!collectStatus || !collectResults) {
        return;
      }

      collectButton.disabled = true;
      collectStatus.textContent = text.collecting;
      collectResults.replaceChildren();
      try {
        const result = await context.request<string>(
          "example.suggestions", { topic: "getting-started" }, {}, { signal: context.signal });
        if (!result.isSuccess) {
          throw new Error(result.message);
        }
        if (context.signal.aborted) {
          return;
        }

        collectStatus.textContent = result.value.outcomes.length === 0 ? text.noContributors : "";
        for (const contribution of result.value.outcomes) {
          const row = document.createElement("li");
          const detail = contribution.status === "handled"
            ? contribution.value
            : text.contributionStatus[contribution.status];
          row.textContent = contribution.providerPluginId + " · " + detail;
          collectResults.append(row);
        }
      } catch (error) {
        if (!context.signal.aborted) {
          collectStatus.textContent = text.collectFailed(errorMessage(error));
        }
      } finally {
        if (!context.signal.aborted) {
          collectButton.disabled = false;
        }
      }
    }, { signal: context.signal });

    const initializeInterop = async () => {
      try {
        const environment = await context.environment.snapshot();
        dotNetPluginAvailable = environment.loadedPlugins.some(
          plugin => plugin.id === "quantum.plugin.example");
        if (!context.signal.aborted && environmentStatus) {
          environmentStatus.textContent = text.environmentLoaded(
            environment.loadedPlugins.length,
            dotNetPluginAvailable);
        }
      } catch (error) {
        if (!context.signal.aborted && environmentStatus) {
          environmentStatus.textContent = text.environmentFailed(errorMessage(error));
        }
      }

      await requestHandshake();
    };

    handshakeButton?.addEventListener("click", () => void requestHandshake(), { signal: context.signal });
    context.element.querySelector("[data-action=dotnet]")?.addEventListener(
      "click",
      () => void context.navigation.navigate("/plugins/example"),
      { signal: context.signal });
    context.element.querySelector("[data-action=home]")?.addEventListener(
      "click",
      () => void context.navigation.navigate("/"),
      { signal: context.signal });
    context.element.querySelector("[data-action=settings]")?.addEventListener("click", () => {
      void context.navigation.navigate(
        `/settings?section=${encodeURIComponent(`${context.plugin.id}/web-settings`)}`,
      ).catch(error => {
        if (environmentStatus && !context.signal.aborted) {
          environmentStatus.textContent = errorMessage(error);
        }
      });
    }, { signal: context.signal });

    // Mount must not be held open by cross-runtime work. Rendering succeeds immediately,
    // while every interop stage reports its own outcome and is cancelled on unmount.
    void initializeInterop();

    return () => context.element.replaceChildren();
  }
});

function escapeHtml(value: string): string {
  const element = document.createElement("span");
  element.textContent = value;
  return element.innerHTML;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

interface ExampleMessages {
  readonly tag: string;
  readonly heading: string;
  readonly interopHeading: string;
  readonly currentView: string;
  readonly readingEnvironment: string;
  readonly waitingForEnvironment: string;
  readonly handshakeAgain: string;
  readonly collectHeading: string;
  readonly collectButton: string;
  readonly collecting: string;
  readonly noContributors: string;
  readonly contributionStatus: Readonly<Record<"pass" | "failed" | "timedOut", string>>;
  collectFailed(message: string): string;
  readonly openDotNet: string;
  readonly openSettings: string;
  readonly backHome: string;
  readonly dotNetUnavailable: string;
  readonly connecting: string;
  handshakeFailed(message: string): string;
  environmentLoaded(count: number, available: boolean): string;
  environmentFailed(message: string): string;
}

function createMessages(languageName: string): ExampleMessages {
  if (languageName.toLowerCase() === "zh") {
    return {
      tag: "WEB 示例",
      heading: "Web 插件示例",
      interopHeading: "与示例插件连接",
      currentView: "当前视图",
      readingEnvironment: "正在检查已安装插件…",
      waitingForEnvironment: "等待连接…",
      handshakeAgain: "重新连接",
      collectHeading: "入门建议",
      collectButton: "收集建议",
      collecting: "正在收集建议…",
      noContributors: "当前没有可用的建议提供方。",
      contributionStatus: { pass: "跳过", failed: "答复失败", timedOut: "答复超时" },
      collectFailed: message => "收集失败：" + message,
      openDotNet: "打开 .NET 示例",
      openSettings: "打开插件设置",
      backHome: "返回概览",
      dotNetUnavailable: "示例插件未安装。",
      connecting: "正在连接示例插件…",
      handshakeFailed: message => `连接失败：${message}`,
      environmentLoaded: (count, available) =>
        `已安装 ${count} 个插件；示例插件${available ? "可用" : "未安装"}。`,
      environmentFailed: message => `检查插件失败：${message}`
    };
  }

  return {
    tag: "WEB EXAMPLE",
    heading: "Web plugin example",
    interopHeading: "Connect to the example plugin",
    currentView: "Current view",
    readingEnvironment: "Checking installed plugins…",
    waitingForEnvironment: "Waiting to connect…",
    handshakeAgain: "Connect again",
    collectHeading: "Getting started suggestions",
    collectButton: "Collect suggestions",
    collecting: "Collecting suggestions…",
    noContributors: "No suggestion contributors are available.",
    contributionStatus: { pass: "Skipped", failed: "Failed", timedOut: "Timed out" },
    collectFailed: message => "Collection failed: " + message,
    openDotNet: "Open .NET example",
    openSettings: "Open plugin settings",
    backHome: "Back to overview",
    dotNetUnavailable: "The example plugin is not installed.",
    connecting: "Connecting to the example plugin…",
    handshakeFailed: message => `Connection failed: ${message}`,
      environmentLoaded: (count, available) =>
        `${count} ${count === 1 ? "plugin" : "plugins"} installed; example plugin ${available ? "available" : "not installed"}.`,
    environmentFailed: message => `Could not check plugins: ${message}`
  };
}
