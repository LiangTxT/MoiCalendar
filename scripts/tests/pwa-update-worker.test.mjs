import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import vm from 'node:vm';

const path = new URL('../../src/MoiCalendar.App/wwwroot/pwaUpdateWorker.js', import.meta.url);
const source = existsSync(path) ? readFileSync(path, 'utf8') : '';
function setup({ replies = [true], lock = true, locksSupported = true, delayedActivation = false, lateClient = false, installingDuringCleanup = false } = {}) {
    const handlers = new Map(), messages = [], timers = new Map(), removed = [];
    let activations = 0, claims = 0, lockHeld = false, timer = 0, now = 1000;
    const scope = 'https://calendar.test/';
    const clients = replies.map((ready, i) => ({ id: `c${i}`, url: scope, type: 'window', postMessage(message, ports) {
        messages.push({ client: i, ...message });
        if (['MOI_PREPARE_UPDATE', 'MOI_COMMIT_UPDATE'].includes(message.type) && ready !== null) ports[0].postMessage({ token: message.token, ready });
    } }));
    class Channel {
        constructor() {
            this.port1 = { close() {}, onmessage: null };
            this.port2 = { close() {}, postMessage: data => this.port1.onmessage?.({ data }) };
        }
    }
    const self = { registration: { scope, waiting: {} }, location: { href: `${scope}service-worker.js` },
        assetsManifest: { version: 'v2' }, addEventListener: (name, fn) => handlers.set(name, fn),
        clients: { async matchAll() { return clients; }, async claim() { claims++; } },
        async skipWaiting() {
            activations++; assert.equal(lockHeld, true, '正在持有数据操作锁才允许激活');
            if (delayedActivation) return;
            let activated;
            handlers.get('activate')({ waitUntil: promise => activated = promise });
            await activated;
        } };
    const navigator = { locks: locksSupported ? { async request(name, options, action) {
        assert.ok(['moicalendar-local-data-operation', 'moicalendar-app-update', 'moicalendar-app-cache'].includes(name));
        if (name === 'moicalendar-app-cache') return action({});
        assert.equal(options.ifAvailable, true);
        if (name === 'moicalendar-app-update') return action({});
        if (lateClient && clients.length === replies.length) clients.push({ id: 'late', type: 'window', url: scope, postMessage() {} });
        lockHeld = lock;
        try { return await action(lock ? {} : null); } finally { lockHeld = false; }
    } } : undefined };
    vm.runInNewContext(source, { self, navigator, MessageChannel: Channel, URL, console, Date: { now: () => now },
        caches: { async keys() {
            if (installingDuringCleanup) self.registration.installing = {};
            return ['offline-cache-v1', 'offline-cache-v2', 'other-app'];
        }, async delete(name) { removed.push(name); } },
        crypto: { randomUUID: () => 'unique' },
        setTimeout(fn) { timers.set(++timer, fn); return timer; }, clearTimeout(id) { timers.delete(id); } });
    const send = (type = 'MOI_REQUEST_UPDATE', sender = clients[0]) => {
        assert.ok(handlers.has('message'), '缺少跨页面安全激活协议');
        let promise;
        handlers.get('message')({ source: sender, data: { type }, waitUntil(value) { promise = value; } });
        return promise;
    };
    const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); };
    return { send, flush, messages, clients, timers, self, navigator, removed,
        activations: () => activations, claims: () => claims, held: () => lockHeld,
        async activate() { let result; handlers.get('activate')({ waitUntil: value => result = value }); await result; },
        expire() { now += 20000; for (const fn of [...timers.values()]) fn(); } };
}

test('所有窗口确认安全后在数据锁内激活并接管，重复请求只激活一次', async () => {
    const f = setup({ replies: [true, true] });
    const first = f.send(), second = f.send(); await Promise.all([first, second]);
    assert.equal(f.activations(), 1); assert.equal(f.held(), false);
    assert.equal(f.messages.filter(x => x.type === 'MOI_PREPARE_UPDATE').length, 2);
});

test('skipWaiting 已提交但实际激活延迟时，不提前释放保存锁', async () => {
    const f = setup({ delayedActivation: true });
    const pending = f.send(); await f.flush();
    assert.equal(f.activations(), 1);
    f.expire(); await f.flush();
    assert.equal(f.held(), true, '不可逆提交后超时不允许恢复写入');
    assert.ok(f.messages.some(x => x.type === 'MOI_COMMIT_UPDATE'));
    await f.activate(); await pending;
    assert.equal(f.held(), false);
});

test('数据锁核对期间新开的窗口必须加入安全确认，否则延期', async () => {
    const f = setup({ lateClient: true }); await f.send();
    assert.equal(f.activations(), 0);
    assert.ok(f.messages.some(x => x.type === 'MOI_UPDATE_DEFERRED'));
});

test('任一窗口编辑中或正在持有保存锁都不激活，并恢复其他窗口', async () => {
    for (const options of [{ replies: [true, false] }, { lock: false }, { locksSupported: false }]) {
        const f = setup(options); await f.send();
        assert.equal(f.activations(), 0);
        assert.ok(f.messages.some(x => x.type === 'MOI_UPDATE_DEFERRED'));
        assert.equal(f.held(), false);
    }
});

test('旧窗口不支持协议或不回应时安全延期，不能强制刷新旧窗口', async () => {
    const f = setup({ replies: [true, null] });
    const pending = f.send(); await f.flush();
    for (const fn of [...f.timers.values()]) fn();
    await pending;
    assert.equal(f.activations(), 0);
    assert.ok(f.messages.some(x => x.type === 'MOI_UPDATE_DEFERRED'));
});

test('无关消息和 scope 外客户端不得触发更新', async () => {
    const f = setup(); await f.send('REMINDER');
    await f.send('MOI_REQUEST_UPDATE', { id: 'other', type: 'window', url: 'https://other.test/' });
    assert.equal(f.activations(), 0); assert.equal(f.messages.length, 0);
});

test('完整性预缓存仍先完成，更新不能删除 IndexedDB 或业务数据', () => {
    const sw = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/service-worker.published.js', import.meta.url), 'utf8');
    assert.ok(sw.includes("'./pwaUpdateWorker.js'"), '发布版 Worker 必须导入更新协议');
    assert.ok(sw.includes('integrity: asset.hash'));
    assert.doesNotMatch(source, /indexedDB|deleteDatabase|localStorage\.clear/);
});

test('仅全部页面重载确认后回收旧应用缓存，等待中的新版和其他缓存不删除', async () => {
    const f = setup({ replies: [true, true] });
    f.self.registration.active = {}; f.self.registration.waiting = null;
    await f.send('MOI_PAGE_READY', f.clients[0]); assert.deepEqual(f.removed, []);
    f.self.registration.installing = {};
    await f.send('MOI_PAGE_READY', f.clients[1]); assert.deepEqual(f.removed, []);
    f.self.registration.installing = null;
    await f.send('MOI_PAGE_READY', f.clients[1]); assert.deepEqual(f.removed, ['offline-cache-v1']);
});

test('回收过程中开始安装下一版本，不得删除其预缓存', async () => {
    const f = setup({ installingDuringCleanup: true });
    f.self.registration.active = {}; f.self.registration.waiting = null;
    await f.send('MOI_PAGE_READY'); assert.deepEqual(f.removed, []);
});

test('发布入口防止 HTTP 缓存旧代码，发布验证器要求完整更新资源', () => {
    const base = new URL('../../src/MoiCalendar.App/wwwroot/', import.meta.url);
    const config = JSON.parse(readFileSync(new URL('staticwebapp.config.json', base), 'utf8'));
    assert.equal(config.globalHeaders?.['Cache-Control'], 'no-cache');
    const index = readFileSync(new URL('index.html', base), 'utf8');
    assert.ok(index.includes('src="pwaUpdates.js"'));
    assert.ok(index.includes('window.moicalendarUpdates.start()'));
    const validator = readFileSync(new URL('../validate-production-publish.mjs', import.meta.url), 'utf8');
    for (const name of ['pwaUpdates.js', 'pwaUpdateWorker.js', 'css/pwa-updates.css', 'Cache-Control']) assert.ok(validator.includes(name), name);
});
