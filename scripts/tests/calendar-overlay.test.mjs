import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarOverlay.js', import.meta.url), 'utf8');

function fixture({ preferred = null, connected = true, elements = [] } = {}) {
    const handlers = new Map();
    const microtasks = [];
    const document = { activeElement: null };
    const context = vm.createContext({ window: {}, document, queueMicrotask: fn => microtasks.push(fn), getComputedStyle: el => ({ visibility: el.visibility ?? 'visible' }) });
    vm.runInContext(source, context);
    const host = {
        isConnected: connected,
        querySelector: () => preferred,
        querySelectorAll: () => elements,
        addEventListener: (name, fn) => handlers.set(name, fn),
        removeEventListener: (name, fn) => { if (handlers.get(name) === fn) handlers.delete(name); }
    };
    const element = (options = {}) => ({
        tabIndex: 0,
        getClientRects: () => [1],
        focus() { document.activeElement = this; },
        ...options
    });
    return { api: context.window.moicalendarOverlay, document, host, element, handlers, flush: () => microtasks.splice(0).forEach(fn => fn()) };
}

test('an overlay without autofocus falls back without throwing', () => {
    const f = fixture();
    const close = f.element();
    f.host.querySelectorAll = () => [close];
    f.api.activate(f.host);
    assert.doesNotThrow(f.flush);
    assert.equal(f.document.activeElement, close);
});

test('hidden or disabled autofocus does not receive focus', () => {
    for (const options of [{ disabled: true }, { visibility: 'hidden' }, { getClientRects: () => [] }]) {
        const f = fixture();
        const preferred = f.element(options);
        const fallback = f.element();
        f.host.querySelector = () => preferred;
        f.host.querySelectorAll = () => [preferred, fallback];
        f.api.activate(f.host);
        f.flush();
        assert.equal(f.document.activeElement, fallback);
    }
});

test('Tab and Shift+Tab stay within the overlay', () => {
    const f = fixture();
    const first = f.element();
    const last = f.element();
    f.host.querySelectorAll = () => [first, last];
    f.api.activate(f.host);
    f.flush();
    let prevented = 0;
    f.handlers.get('keydown')({ key: 'Tab', shiftKey: true, preventDefault: () => prevented++ });
    assert.equal(f.document.activeElement, last);
    f.handlers.get('keydown')({ key: 'Tab', shiftKey: false, preventDefault: () => prevented++ });
    assert.equal(f.document.activeElement, first);
    assert.equal(prevented, 2);
});

test('detached overlays cannot steal focus and deactivate removes handlers', () => {
    const f = fixture({ connected: false });
    f.host.querySelectorAll = () => [f.element()];
    f.api.activate(f.host);
    f.flush();
    assert.equal(f.document.activeElement, null);
    f.api.deactivate(f.host);
    assert.equal(f.handlers.size, 0);
});
