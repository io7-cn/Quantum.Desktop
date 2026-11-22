import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(
    new URL('../src/Quantum/wwwroot/navigationOrder.js', import.meta.url),
    'utf8',
);

function fixture(count = 3) {
    const events = () => ({
        listeners: new Map(),
        addEventListener(name, listener) {
            this.listeners.set(name, listener);
        },
        removeEventListener(name) {
            this.listeners.delete(name);
        },
    });
    const classes = () => ({
        values: new Set(),
        add(...names) {
            names.forEach((name) => this.values.add(name));
        },
        remove(...names) {
            names.forEach((name) => this.values.delete(name));
        },
    });
    let offset = 0;
    let sequence = 0;
    const frames = new Map();
    const previews = new Set();
    const calls = [];
    const window = Object.assign(events(), {
        innerHeight: 240,
        scrollBy(_x, y) {
            offset += y;
        },
    });
    const rows = Array.from({ length: count }, (_, index) => {
        const row = {
            dataset: { navigationKey: `entry-${index}` },
            classList: classes(),
            getBoundingClientRect: () => ({
                left: 100,
                right: 500,
                top: 40 + index * 48 - offset,
                height: 48,
                width: 400,
            }),
            cloneNode() {
                const preview = {
                    classList: classes(),
                    style: {},
                    removeAttribute() {},
                    setAttribute() {},
                    remove() {
                        previews.delete(preview);
                    },
                };
                return preview;
            },
        };
        row.handle = {
            capture: null,
            focus() {},
            setPointerCapture(id) {
                this.capture = id;
            },
            hasPointerCapture(id) {
                return this.capture === id;
            },
            releasePointerCapture() {
                this.capture = null;
            },
            closest(selector) {
                return selector === '.navigation-order-handle' ? this : row;
            },
        };
        return row;
    });
    const list = {
        getBoundingClientRect: () => ({
            left: 100,
            right: 500,
            top: 40 - offset,
            bottom: 40 + rows.length * 48 - offset,
        }),
    };
    const root = Object.assign(events(), {
        contains: (row) => rows.includes(row),
        querySelector: () => list,
        querySelectorAll: () => rows,
    });
    const context = vm.createContext({
        window,
        console,
        document: { body: { appendChild: (preview) => previews.add(preview) } },
        requestAnimationFrame(callback) {
            frames.set(++sequence, callback);
            return sequence;
        },
        cancelAnimationFrame(id) {
            frames.delete(id);
        },
    });
    vm.runInContext(source, context);
    const order = window.quantum.navigationOrder;
    order.initialize('settings', root, {
        invokeMethodAsync(...args) {
            calls.push(args);
            return Promise.resolve();
        },
    });
    function emit(target, name, overrides = {}) {
        const event = {
            pointerId: 1,
            pointerType: 'mouse',
            button: 0,
            buttons: 1,
            clientX: 120,
            clientY: 60,
            isPrimary: true,
            preventDefault() {
                this.prevented = true;
            },
            ...overrides,
        };
        target.listeners.get(name)?.(event);
        return event;
    }
    return {
        root,
        window,
        rows,
        order,
        calls,
        frames,
        previews,
        down(index, overrides = {}) {
            return emit(root, 'pointerdown', {
                target: rows[index].handle,
                clientY: 60 + index * 48 - offset,
                ...overrides,
            });
        },
        move(y, overrides = {}) {
            return emit(window, 'pointermove', { clientY: y, ...overrides });
        },
        up(y, overrides = {}) {
            return emit(window, 'pointerup', { clientY: y, ...overrides });
        },
        event(name, overrides = {}) {
            return emit(window, name, overrides);
        },
        frame() {
            const [id, callback] = frames.entries().next().value;
            frames.delete(id);
            callback();
        },
        get offset() {
            return offset;
        },
    };
}

test('pointer movement commits one drop without any HTML drag events', () => {
    const f = fixture();
    f.down(0);
    f.move(175);
    assert.equal(f.previews.size, 1);
    assert.ok(f.rows[2].classList.values.has('drop-after'));
    assert.equal(f.calls.length, 0);
    f.up(175);
    assert.deepEqual(f.calls, [['MoveByPointerAsync', 'entry-0', 'entry-2', true]]);
    assert.equal(f.previews.size, 0);
    assert.equal(f.frames.size, 0);
    assert.equal(f.rows[0].handle.capture, null);
});

test('upward drops identify the entry before the insertion point', () => {
    const f = fixture();
    f.down(2);
    f.move(45);
    assert.ok(f.rows[0].classList.values.has('drop-before'));
    f.up(45);
    assert.deepEqual(f.calls, [['MoveByPointerAsync', 'entry-2', 'entry-0', false]]);
});

test('clicks, small movements and other pointer IDs do not reorder', () => {
    const f = fixture();
    f.down(0);
    f.move(175, { pointerId: 2 });
    f.move(62);
    f.up(62);
    assert.equal(f.calls.length, 0);
    assert.equal(f.previews.size, 0);
    f.down(0, { button: 2 });
    f.move(175);
    f.up(175);
    assert.equal(f.calls.length, 0);
});

test('touch uses pointer capture and the same reorder path', () => {
    const f = fixture();
    f.down(0, { pointerType: 'touch', pointerId: 7 });
    const move = f.move(175, { pointerType: 'touch', pointerId: 7 });
    assert.equal(move.prevented, true);
    f.up(175, { pointerType: 'touch', pointerId: 7 });
    assert.equal(f.calls.length, 1);
});

test('Escape, cancellation, capture loss and blur remove previews without saving', () => {
    for (const [event, values] of [
        ['keydown', { key: 'Escape' }],
        ['pointercancel', {}],
        ['lostpointercapture', {}],
        ['blur', {}],
    ]) {
        const f = fixture();
        f.down(0);
        f.move(175);
        f.event(event, values);
        f.up(175);
        assert.equal(f.calls.length, 0, event);
        assert.equal(f.previews.size, 0, event);
        assert.equal(f.frames.size, 0, event);
    }
});

test('dropping outside the list or removing the source cancels the move', () => {
    const f = fixture();
    f.down(0);
    f.move(175);
    f.up(175, { clientX: 600 });
    assert.equal(f.calls.length, 0);
    f.down(0);
    f.move(175);
    f.rows.shift();
    f.up(175);
    assert.equal(f.calls.length, 0);
    assert.equal(f.previews.size, 0);
});

test('edge dragging scrolls the page and stopping cancels the animation', () => {
    const f = fixture(20);
    f.down(0);
    f.move(232);
    f.frame();
    assert.equal(f.offset, 12);
    f.event('pointercancel');
    assert.equal(f.frames.size, 0);
});

test('disposal cancels active dragging and removes all event handlers', () => {
    const f = fixture();
    f.down(0);
    f.move(175);
    f.order.dispose('settings');
    assert.equal(f.previews.size, 0);
    assert.equal(f.frames.size, 0);
    assert.equal(f.root.listeners.size, 0);
    assert.equal(f.window.listeners.size, 0);
    assert.equal(f.calls.length, 0);
});
