import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import vm from 'node:vm';

const path = new URL('../../src/MoiCalendar.App/wwwroot/pwaUpdates.js', import.meta.url);
const source = existsSync(path) ? readFileSync(path, 'utf8') : '';
function events() {
    const handlers = new Map();
    return { handlers, addEventListener(type, fn) { handlers.set(type, fn); },
        removeEventListener(type) { handlers.delete(type); }, fire(type, e = {}) { return handlers.get(type)?.(e); } };
}
function setup({ waiting = true, controlled = true, blockedGate = false } = {}) {
    let clock = 5000, updates = 0, reloads = 0, modal = false, ready = true;
    const timers = new Map(), intervals = new Map(), sent = [], notices = [];
    let timerId = 0;
    const frame = { inert: false, isConnected: true };
    const root = { inert: false };
    const doc = { ...events(), visibilityState: 'visible', activeElement: { matches: () => false },
        getElementById() { return root; },
        querySelector(selector) {
            if (selector === '.app-frame') return ready ? frame : null;
            return modal ? {} : null;
        }, createElement() { return { setAttribute() {}, hidden: true, textContent: '', className: '', remove() {} }; },
        body: { append(node) { notices.push(node); } } };
    const worker = { ...events(), state: 'installed', scriptURL: 'https://calendar.test/service-worker.js', postMessage(message) { sent.push(message); } };
    const registration = { ...events(), waiting: waiting ? worker : null, installing: null, async update() { updates++; } };
    const sw = { ...events(), controller: controlled ? {} : null, async register(url, options) {
        assert.equal(url, 'service-worker.js'); assert.equal(options.updateViaCache, 'none'); return registration;
    } };
    const win = { ...events(), location: { href: 'https://calendar.test/', pathname: '/', reload() { reloads++; } } };
    let queuedGate, deferGate = false, gateRequests = 0, heldGates = 0;
    const grants = [];
    const navigator = { serviceWorker: sw, onLine: true, locks: { request(name, options, callback) {
        assert.equal(name, 'moicalendar-app-update'); assert.equal(options.mode, 'shared');
        if (options.ifAvailable) {
            gateRequests++;
            const grant = async () => { if (!blockedGate) heldGates++; try { return await callback(blockedGate ? null : {}); } finally { if (!blockedGate) heldGates--; } };
            if (deferGate) return new Promise(resolve => grants.push(() => resolve(grant())));
            return grant();
        }
        return new Promise(resolve => { queuedGate = () => resolve(callback({})); });
    } } };
    vm.runInNewContext(source, { window: win, document: doc, navigator, URL, console,
        performance: { now: () => clock },
        setInterval(fn, delay) { intervals.set(++timerId, { fn, delay }); return timerId; }, clearInterval(id) { intervals.delete(id); },
        setTimeout(fn, delay) { timers.set(++timerId, { fn, delay }); return timerId; }, clearTimeout(id) { timers.delete(id); } });
    const flush = async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); };
    const message = async (type, token = 'v2:1') => {
        let reply;
        sw.fire('message', { source: worker, data: { type, token }, ports: [{ postMessage(value) { reply = value; } }] });
        await flush(); return reply;
    };
    return { frame, root, doc, win, navigator, sw, worker, registration, sent, notices, timers, intervals,
        grantGate: () => queuedGate?.(),
        deferGates: () => deferGate = true,
        grantAllGates: () => { deferGate = false; for (const grant of grants.splice(0)) grant(); },
        gateRequests: () => gateRequests, heldGates: () => heldGates,
        flush, message, tick: n => clock += n, setModal: value => modal = value, setReady: value => ready = value,
        updates: () => updates, reloads: () => reloads,
        async start() { assert.ok(win.moicalendarUpdates, '缺少 PWA 自动更新协调器'); await win.moicalendarUpdates.start(); await flush(); } };
}

test('加载检查新版并请求等待中的 Worker 安全激活，控制器变化只刷新一次', async () => {
    const f = setup(); await f.start();
    assert.equal(f.updates(), 1);
    assert.equal(f.sent[0]?.type, 'MOI_REQUEST_UPDATE');
    const ack = await f.message('MOI_PREPARE_UPDATE');
    assert.equal(ack.ready, true); assert.equal(f.frame.inert, true);
    f.sw.controller = {}; f.sw.fire('controllerchange'); f.sw.fire('controllerchange');
    assert.equal(f.reloads(), 1);
});

test('首次安装控制器接管不产生刷新循环', async () => {
    const f = setup({ controlled: false, waiting: false }); await f.start();
    f.sw.controller = {}; f.sw.fire('controllerchange');
    assert.equal(f.reloads(), 0);
});

test('编辑弹窗与脏表单阻止更新，关闭后才允许激活', async () => {
    const f = setup(); f.setModal(true); await f.start();
    assert.equal(f.sent.length, 0);
    assert.equal((await f.message('MOI_PREPARE_UPDATE')).ready, false);
    f.setModal(false);
    const form = { isConnected: true };
    const input = { closest: () => form, matches: () => true };
    f.doc.fire('input', { target: input }); f.tick(3000);
    assert.equal((await f.message('MOI_PREPARE_UPDATE')).ready, false);
    form.isConnected = false;
    assert.equal((await f.message('MOI_PREPARE_UPDATE')).ready, true);
    assert.equal(f.frame.inert, true);
});

test('联网与前台才检测更新，离线和后台定时器不轮询', async () => {
    const f = setup({ waiting: false }); await f.start();
    const check = [...f.intervals.values()].find(x => x.delay === 60000).fn;
    f.navigator.onLine = false; await check(); assert.equal(f.updates(), 1);
    f.navigator.onLine = true; f.doc.visibilityState = 'hidden'; await check(); assert.equal(f.updates(), 1);
    f.doc.visibilityState = 'visible'; f.doc.fire('visibilitychange'); await f.flush(); assert.equal(f.updates(), 2);
    f.win.fire('online'); await f.flush(); assert.equal(f.updates(), 3);
});

test('跨窗口延期或握手超时解除冻结，输入内容不会被清理', async () => {
    const f = setup(); await f.start();
    await f.message('MOI_PREPARE_UPDATE');
    assert.equal(f.frame.inert, true);
    await f.message('MOI_UPDATE_DEFERRED');
    assert.equal(f.frame.inert, false); assert.equal(f.reloads(), 0);
    assert.ok(f.notices.some(x => x.textContent.includes('新版')));
    await f.message('MOI_PREPARE_UPDATE', 'v2:2');
    for (const { fn } of [...f.timers.values()]) fn();
    await f.flush();
    assert.equal(f.frame.inert, false);
});

test('启动未完成与正在输入不会被刷新；更新失败不打断本地应用', async () => {
    const f = setup(); f.setReady(false); await f.start();
    assert.equal((await f.message('MOI_PREPARE_UPDATE')).ready, false);
    f.setReady(true); f.doc.activeElement = { matches: () => true };
    assert.equal((await f.message('MOI_PREPARE_UPDATE')).ready, false);
    f.doc.activeElement = { matches: () => false };
    f.registration.update = async () => { throw Error('offline'); };
    await [...f.intervals.values()].find(x => x.delay === 60000).fn();
    assert.equal(f.reloads(), 0);
});

test('无关消息不冻结界面，卸载清理监听器和计时器', async () => {
    const f = setup(); await f.start();
    await f.message('REMINDER'); assert.equal(f.frame.inert, false);
    f.win.moicalendarUpdates.dispose();
    assert.equal(f.sw.handlers.size, 0); assert.equal(f.doc.handlers.size, 0);
    assert.equal(f.win.handlers.size, 0); assert.equal(f.intervals.size, 0);
});

test('提交阶段取消可逆冻结计时器，等待实际激活期间保持保护', async () => {
    const f = setup(); await f.start();
    await f.message('MOI_PREPARE_UPDATE');
    assert.equal((await f.message('MOI_COMMIT_UPDATE')).ready, true);
    for (const { fn } of [...f.timers.values()]) fn();
    assert.equal(f.frame.inert, true);
    f.sw.controller = {}; f.sw.fire('controllerchange'); assert.equal(f.reloads(), 1);
});

test('切换期间新开窗口保持不可操作，通行锁释放后重载新版', async () => {
    const f = setup({ blockedGate: true, waiting: false }); await f.start();
    assert.equal(f.root.inert, true); assert.equal(f.reloads(), 0);
    f.grantGate(); await f.flush(); assert.equal(f.reloads(), 1);
});

test('提交中的版本不再次检查更新，被后续版本替换时解除过期保护', async () => {
    const f = setup(); await f.start();
    await f.message('MOI_PREPARE_UPDATE'); await f.message('MOI_COMMIT_UPDATE');
    await [...f.intervals.values()].find(x => x.delay === 60000).fn();
    assert.equal(f.updates(), 1);
    f.worker.state = 'redundant'; f.worker.fire('statechange'); await f.flush();
    assert.equal(f.frame.inert, false);
});

test('初次注册暂时失败，恢复联网后可重试而不要求重开页面', async () => {
    const f = setup(); const register = f.sw.register;
    let attempts = 0;
    f.sw.register = async (...args) => { if (++attempts === 1) throw Error('temporary offline'); return register(...args); };
    await f.start(); f.win.fire('online'); await f.flush();
    assert.equal(attempts, 2); assert.equal(f.updates(), 1);
});

test('bfcache 恢复与延期消息并发时，共享启动锁申请合并且下次准备完全释放', async () => {
    const f = setup(); await f.start(); await f.message('MOI_PREPARE_UPDATE');
    f.deferGates();
    f.win.fire('pageshow', { persisted: true }); await f.message('MOI_UPDATE_DEFERRED');
    assert.equal(f.gateRequests(), 2, '初始租约加一份合并恢复申请');
    f.grantAllGates(); await f.flush(); assert.equal(f.heldGates(), 1);
    await f.message('MOI_PREPARE_UPDATE', 'v3:1'); assert.equal(f.heldGates(), 0);
});
