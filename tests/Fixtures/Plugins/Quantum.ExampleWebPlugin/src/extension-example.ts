import type {
  ExtensionJson,
  QuantumExtensionRegistration,
  QuantumPluginContext,
  QuantumPluginViewContext,
} from '@io7/quantum-plugin-sdk';

export async function createExtensionExample(context: QuantumPluginContext) {
  const registrations: QuantumExtensionRegistration[] = [];
  const dispose = async () => {
    for (const registration of [...registrations].reverse()) {
      await registration.dispose();
    }
    registrations.length = 0;
  };
  let sequence = 0;
  const message = () => ({
    message: `Message from the Web example (${sequence})`,
    messageZh: `来自 Web 示例的消息（${sequence}）`,
  });
  try {
    registrations.push(await context.extensions.define('example-data', 1));
    const ownMessage = await context.extensions.register(
      `${context.plugin.id}/example-data`, 1, 'message', message(),
    );
    registrations.push(ownMessage);
    const sharedMessage = await context.extensions.register(
      'quantum.plugin.example/example-data', 1, 'web-message', message(), 20,
    );
    registrations.push(sharedMessage);
    registrations.push(await context.extensions.register('quantum.settings/pages', 1, 'web-settings', {
      title: 'Web example', titles: { 'zh-CN': 'Web 示例' }, view: 'settings',
    }));

    return {
      dispose,
      async mount(view: QuantumPluginViewContext) {
        const chinese = view.locale.languageName === 'zh';
        const intro = document.createElement('p');
        intro.textContent = chinese
          ? '更新消息后，也可以在“示例插件”设置页看到变化。'
          : 'Update the message, then open the Example plugin settings tab to see the same change.';
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = chinese ? '更新消息' : 'Update message';
        const output = document.createElement('ul');
        output.setAttribute('aria-live', 'polite');
        const error = document.createElement('p');
        error.setAttribute('role', 'status');
        view.element.replaceChildren(intro, button, output, error);
        const observation = await view.extensions.observe(`${context.plugin.id}/example-data`, 1, snapshot => {
          output.replaceChildren();
          for (const contribution of snapshot.contributions) {
            const text = readMessage(contribution.value, chinese);
            if (text !== null) {
              const item = document.createElement('li');
              item.textContent = `${contribution.pluginId}: ${text}`;
              output.appendChild(item);
            }
          }
        });
        button.addEventListener('click', () => {
          button.disabled = true;
          error.textContent = '';
          sequence++;
          void (async () => {
            try {
              await ownMessage.update(message());
              await sharedMessage.update(message());
            } catch {
              if (!view.signal.aborted) {
                error.textContent = chinese ? '无法更新消息。' : 'Could not update the message.';
              }
            } finally {
              if (!view.signal.aborted) {
                button.disabled = false;
              }
            }
          })();
        }, { signal: view.signal });
        return () => observation.dispose();
      },
    };
  } catch (error) {
    await dispose();
    throw error;
  }
}

export function readMessage(value: ExtensionJson, chinese: boolean): string | null {
  if (value === null || typeof value !== 'object' || !('message' in value)
    || typeof value.message !== 'string') {
    return null;
  }
  if (chinese && 'messageZh' in value && typeof value.messageZh === 'string') {
    return value.messageZh;
  }
  return value.message;
}
