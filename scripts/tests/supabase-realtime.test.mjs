import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.Sync/wwwroot/supabaseRealtime.js', import.meta.url), 'utf8');

function harness() {
    const intervals = new Map();
    const listeners = new Map();
    const calls = [];
    let sequence = 0;
    const document = { hidden: false, addEventListener: (name, fn) => listeners.set(name, fn),
        removeEventListener: name => listeners.delete(name) };
    const window = { addEventListener: document.addEventListener, removeEventListener: document.removeEventListener };
    const navigator = { onLine: true };
    class Socket {
        static OPEN = 1;
        static CLOSING = 2;
        readyState = 0;
        close() { this.readyState = 3; }
        send() {}
    }
    const context = vm.createContext({ document, window, navigator, URL, WebSocket: Socket,
        setInterval: (fn, interval) => { const id = ++sequence; intervals.set(id, { fn, interval }); return id; },
        clearInterval: id => intervals.delete(id), setTimeout: () => ++sequence, clearTimeout() {} });
    vm.runInContext(source.replace('export function', 'function'), context);
    const subscription = context.createSupabaseRealtimeNotifier({ async invokeMethodAsync(name, value) {
        if (name === 'GetRealtimeAccessTokenAsync') return 'test-token';
        calls.push({ name, value });
    } }, 'wss://example.com/realtime/v1/websocket', 'public-test-key', 'test-account');
    return { intervals, listeners, calls, document, navigator, subscription };
}

test('foreground safety check runs every 60 seconds and emits only a wake-up', () => {
    const h = harness();
    const timer = [...h.intervals.values()].find(x => x.interval === 60000);
    assert.ok(timer);
    timer.fn();
    assert.ok(h.calls.some(x => x.name === 'OnRealtimeWakeUpAsync' && x.value === 'PeriodicCheck'));
});

test('safety check does not poll while hidden or offline', () => {
    const h = harness();
    const timer = [...h.intervals.values()][0];
    h.document.hidden = true;
    timer.fn();
    h.document.hidden = false;
    h.navigator.onLine = false;
    timer.fn();
    assert.equal(h.calls.filter(x => x.name === 'OnRealtimeWakeUpAsync').length, 0);
});

test('stop removes safety check and resume handlers', async () => {
    const h = harness();
    await h.subscription.stop();
    assert.equal(h.intervals.size, 0);
    assert.equal(h.listeners.size, 0);
});

test('return to foreground requests synchronization immediately', () => {
    const h = harness();
    h.listeners.get('visibilitychange')();
    assert.ok(h.calls.some(x => x.name === 'OnRealtimeWakeUpAsync' && x.value === 'ForegroundResume'));
});
