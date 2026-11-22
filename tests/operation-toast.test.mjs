import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(
    new URL('../src/Quantum/wwwroot/operationToast.js', import.meta.url),
    'utf8',
);

function fixture({ hovered = false, focused = false } = {}) {
    let now = 0;
    let nextId = 0;
    const timers = new Map();
    const listeners = new Map();
    const classes = new Set();
    const dismissed = [];
    const inside = {};
    const element = {
        matches: () => hovered,
        contains: (target) => target === inside,
        classList: { add: (name) => classes.add(name), remove: (name) => classes.delete(name) },
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: (name) => listeners.delete(name),
    };
    const context = vm.createContext({
        window: {},
        document: { activeElement: focused ? inside : null },
        performance: { now: () => now },
        setTimeout(callback, delay) {
            const id = ++nextId;
            timers.set(id, { callback, at: now + delay });
            return id;
        },
        clearTimeout: (id) => timers.delete(id),
    });
    vm.runInContext(source, context);
    const toast = context.window.quantum.operationToast;
    return {
        toast,
        element,
        timers,
        listeners,
        classes,
        dismissed,
        inside,
        show(noticeId = 'first', isError = false) {
            toast.show(
                'toast',
                element,
                {
                    invokeMethodAsync(method, id) {
                        dismissed.push([method, id]);
                        return Promise.resolve();
                    },
                },
                noticeId,
                isError,
            );
        },
        advance(milliseconds) {
            const end = now + milliseconds;
            while (true) {
                const entry = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0];
                if (!entry || entry[1].at > end) {
                    break;
                }
                timers.delete(entry[0]);
                now = entry[1].at;
                entry[1].callback();
            }
            now = end;
        },
    };
}

test('success fades after four seconds and errors after eight seconds', () => {
    for (const isError of [false, true]) {
        const f = fixture();
        f.show('result', isError);
        f.advance((isError ? 8000 : 4000) - 1);
        assert.equal(f.classes.has('fading'), false);
        f.advance(1);
        assert.equal(f.classes.has('fading'), true);
        assert.equal(f.dismissed.length, 0);
        f.advance(180);
        assert.deepEqual(f.dismissed, [['DismissAsync', 'result']]);
    }
});

test('hover and focus pause remaining time and resume only after both leave', () => {
    const f = fixture();
    f.show();
    f.advance(1500);
    f.listeners.get('pointerenter')();
    f.listeners.get('focusin')();
    f.advance(20000);
    f.listeners.get('pointerleave')();
    f.advance(20000);
    assert.equal(f.dismissed.length, 0);
    f.listeners.get('focusout')({ relatedTarget: null });
    f.advance(2499);
    assert.equal(f.classes.has('fading'), false);
    f.advance(181);
    assert.equal(f.dismissed.length, 1);
});

test('moving focus within a notice does not resume the timer', () => {
    const f = fixture();
    f.show();
    f.listeners.get('focusin')();
    f.listeners.get('focusout')({ relatedTarget: f.inside });
    f.advance(10000);
    assert.equal(f.dismissed.length, 0);
});

test('a notice initially under the pointer or focused starts paused', () => {
    for (const options of [{ hovered: true }, { focused: true }]) {
        const f = fixture(options);
        f.show();
        f.advance(10000);
        assert.equal(f.dismissed.length, 0);
        if (options.hovered) {
            f.listeners.get('pointerleave')();
        } else {
            f.listeners.get('focusout')({ relatedTarget: null });
        }
        f.advance(4180);
        assert.equal(f.dismissed.length, 1);
    }
});

test('replacement cancels an old fade and gives the new message a full lifetime', () => {
    const f = fixture();
    f.show('old');
    f.advance(4050);
    assert.equal(f.classes.has('fading'), true);
    f.show('new');
    assert.equal(f.classes.has('fading'), false);
    f.advance(3999);
    assert.equal(f.dismissed.length, 0);
    f.advance(181);
    assert.deepEqual(f.dismissed, [['DismissAsync', 'new']]);
});

test('hovering during fade restores the message until the pointer leaves', () => {
    const f = fixture();
    f.show();
    f.advance(4050);
    f.listeners.get('pointerenter')();
    assert.equal(f.classes.has('fading'), false);
    f.advance(10000);
    assert.equal(f.dismissed.length, 0);
    f.listeners.get('pointerleave')();
    f.advance(180);
    assert.equal(f.dismissed.length, 1);
});

test('manual dismissal and disposal remove timers and event listeners', () => {
    const f = fixture();
    f.show();
    f.toast.dispose('toast');
    f.advance(10000);
    assert.equal(f.dismissed.length, 0);
    assert.equal(f.timers.size, 0);
    assert.equal(f.listeners.size, 0);
    assert.equal(f.toast.registrations.size, 0);
});
