import assert from 'node:assert/strict';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const compiled = await build({
  entryPoints: [fileURLToPath(new URL('../src/extension-example.ts', import.meta.url))],
  bundle: true, write: false, format: 'esm', platform: 'node',
});
const { createExtensionExample, readMessage } = await import(
  `data:text/javascript;base64,${Buffer.from(compiled.outputFiles[0].contents).toString('base64')}`,
);

test('example consumers validate JSON and select localized messages', () => {
  for (const value of [null, true, 1, 'text', [], {}, { message: 42 }]) {
    assert.equal(readMessage(value, false), null);
  }
  assert.equal(readMessage({ message: 'Hello', messageZh: '你好' }, true), '你好');
  assert.equal(readMessage({ message: 'Hello' }, true), 'Hello');
  assert.equal(readMessage({ message: 42, messageZh: '无效消息' }, true), null);
});

class Element {
  children = [];
  attributes = {};
  handlers = new Map();
  textContent = '';
  disabled = false;
  replaceChildren(...children) { this.children = children; }
  appendChild(child) { this.children.push(child); }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, handler, options) {
    this.handlers.set(name, handler);
    options?.signal.addEventListener('abort', () => this.handlers.delete(name), { once: true });
  }
}

test('the Web example updates its own point and the .NET point and releases registrations', async t => {
  const previousDocument = globalThis.document;
  globalThis.document = { createElement: () => new Element() };
  t.after(() => { globalThis.document = previousDocument; });
  const entries = new Map();
  const observers = new Map();
  const definitions = new Set();
  const pluginId = 'quantum.plugin.example-web';
  let revision = 0;
  const notify = pointId => {
    const observer = observers.get(pointId);
    observer?.({ revision: ++revision, isAvailable: definitions.has(pointId), contributions:
      [...entries.values()].filter(entry => entry.pointId === pointId).map(entry => ({ pluginId, value: entry.value })) });
  };
  const context = {
    plugin: { id: pluginId },
    extensions: {
      async define(localId) {
        const pointId = `${pluginId}/${localId}`;
        definitions.add(pointId);
        return { async dispose() { definitions.delete(pointId); } };
      },
      async register(pointId, version, localId, value) {
        const entry = { pointId, version, value };
        entries.set(localId, entry);
        return {
          async update(value) { entry.value = value; notify(pointId); },
          async dispose() { entries.delete(localId); notify(pointId); },
        };
      },
      async observe(pointId, version, observer) {
        observers.set(pointId, observer);
        notify(pointId);
        return { async dispose() { observers.delete(pointId); } };
      },
    },
  };
  const example = await createExtensionExample(context);
  assert.equal(entries.get('web-message').pointId, 'quantum.plugin.example/example-data');
  assert.equal(entries.get('web-settings').pointId, 'quantum.settings/pages');
  const element = new Element();
  const controller = new AbortController();
  const unmount = await example.mount({ ...context, element, locale: { languageName: 'en' }, signal: controller.signal });
  const [, button, output] = element.children;
  assert.match(output.children[0].textContent, /\(0\)/);
  button.handlers.get('click')();
  await new Promise(resolve => setImmediate(resolve));
  assert.match(output.children[0].textContent, /\(1\)/);
  assert.equal(entries.get('message').value.message, entries.get('web-message').value.message);
  assert.equal(button.disabled, false);
  controller.abort();
  await unmount();
  assert.equal(observers.size, 0);
  assert.equal(button.handlers.size, 0);
  await example.dispose();
  assert.equal(entries.size, 0);
  assert.equal(definitions.size, 0);
});
