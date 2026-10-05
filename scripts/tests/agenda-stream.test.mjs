import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';
import assert from 'node:assert/strict';

function setup() {
    const handlers = new Map(), frames = new Map(), calls = [], observers = [];
    let frameId = 0, rejectNext = false;
    const next = { disabled: false };
    const section = { dataset: { agendaMonth: '2026-10' }, getBoundingClientRect: () => ({ bottom: 400 }) };
    const root = {
        scrollTop: 0, scrollHeight: 1600,
        addEventListener: (name, callback) => handlers.set(name, callback),
        removeEventListener: name => handlers.delete(name),
        getBoundingClientRect: () => ({ top: 100 }),
        querySelectorAll: () => [section], querySelector: () => next
    };
    let finish;
    const reference = { invokeMethodAsync: (name, ...args) => {
        calls.push([name, ...args]);
        if (name === 'ReportVisibleMonth') return rejectNext ? Promise.reject(Error('failed')) : Promise.resolve();
        return new Promise(resolve => { finish = resolve; });
    } };
    class Observer {
        constructor(callback) { this.callback = callback; this.observed = []; this.disconnects = 0; observers.push(this); }
        observe(element) { this.observed.push(element); }
        disconnect() { this.disconnects++; }
    }
    const context = vm.createContext({ IntersectionObserver: Observer,
        requestAnimationFrame: fn => { const id = ++frameId; frames.set(id, fn); return id; },
        cancelAnimationFrame: id => frames.delete(id) });
    vm.runInContext(readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/agendaStream.js', import.meta.url), 'utf8').replaceAll('export function ', 'function '), context);
    const connect = (allow = true, key = 'October') => context.connect(root, reference, allow, key);
    const flush = () => { for (const [id, fn] of [...frames]) { frames.delete(id); fn(); } };
    const extendCalls = () => calls.filter(call => call[0] === 'ExtendAsync');
    return { root, next, section, reference, context, handlers, frames, calls, observers, connect, flush, extendCalls,
        finish: () => finish?.(), failReport: () => { rejectNext = true; } };
}

test('首次进入不向过去自动循环加载；普通重渲染不重复观察底部', () => {
    const s = setup(); s.connect(); s.flush(); s.connect();
    assert.equal(s.extendCalls().length, 0);
    assert.equal(s.observers[0].observed.length, 1);
});
test('顶部向上滚轮自动加载过去月份，不拦截原生滚动', () => {
    const s = setup(); s.connect();
    s.handlers.get('wheel')({ deltaY: -100 });
    assert.deepEqual(s.extendCalls(), [['ExtendAsync', -1]]);
});
test('只有接近顶部且向上滚动才读取更早月份', () => {
    const s = setup(); s.root.scrollTop = 500; s.connect();
    s.root.scrollTop = 450; s.handlers.get('scroll')();
    assert.equal(s.extendCalls().length, 0);
    s.root.scrollTop = 250; s.handlers.get('scroll')();
    assert.deepEqual(s.extendCalls(), [['ExtendAsync', -1]]);
});
test('加载中多次滚轮、触屏和观察器回调只发出一次请求', () => {
    const s = setup(); s.connect();
    for (let i = 0; i < 10; i++) s.handlers.get('wheel')({ deltaY: -50 });
    s.observers[0].callback([{ target: s.next, isIntersecting: true }]);
    assert.equal(s.extendCalls().length, 1);
});
test('触屏向下拉超过阈值才获取过去，多点触摸不触发', () => {
    const s = setup(); s.connect();
    s.handlers.get('touchstart')({ touches: [{ clientY: 100 }] });
    s.handlers.get('touchmove')({ touches: [{ clientY: 108 }] });
    assert.equal(s.extendCalls().length, 0);
    s.handlers.get('touchmove')({ touches: [{ clientY: 120 }] });
    assert.equal(s.extendCalls().length, 1);
    s.handlers.get('touchcancel')();
    assert.equal(s.handlers.size, 7);
});
test('追加月份不改变位置，前插月份补偿保留期间的新滚动位置且仅执行一次', () => {
    const s = setup(); s.connect(); s.root.scrollTop = 40;
    s.context.rememberPosition(s.root); s.root.scrollTop = 65; s.root.scrollHeight += 320;
    s.connect(); assert.equal(s.root.scrollTop, 385);
    s.connect(); assert.equal(s.root.scrollTop, 385);
    assert.equal(s.extendCalls().length, 0);
});
test('读取失败禁用自动重试；明确重试恢复后可继续加载', async () => {
    const s = setup(); s.connect(false); s.handlers.get('wheel')({ deltaY: -1 });
    assert.equal(s.extendCalls().length, 0);
    s.connect(true); s.handlers.get('wheel')({ deltaY: -1 });
    s.finish(); await new Promise(resolve => setImmediate(resolve));
    s.handlers.get('wheel')({ deltaY: -1 });
    assert.equal(s.extendCalls().length, 2);
});
test('切换月份上下文撤销旧补偿，不误加载更早月份', () => {
    const s = setup(); s.root.scrollTop = 500; s.connect();
    s.context.rememberPosition(s.root); s.root.scrollTop = 0; s.root.scrollHeight = 2000;
    s.connect(true, 'November');
    assert.equal(s.root.scrollTop, 0); assert.equal(s.extendCalls().length, 0);
});
test('键盘向上可获取过去月份，编辑控件的方向键不触发加载', () => {
    const s = setup(); s.connect();
    s.handlers.get('keydown')({ key: 'ArrowUp', target: { matches: () => true } });
    assert.equal(s.extendCalls().length, 0);
    s.handlers.get('keydown')({ key: 'PageUp', target: { matches: () => false } });
    assert.equal(s.extendCalls().length, 1);
});
test('卸载移除所有监听器与帧，旧观察器不再加载', () => {
    const s = setup(); s.connect(); const oldFrame = [...s.frames.values()][0];
    s.context.disconnect(s.root); oldFrame();
    s.observers[0].callback([{ target: s.next, isIntersecting: true }]);
    assert.equal(s.handlers.size, 0); assert.equal(s.frames.size, 0); assert.equal(s.calls.length, 0);
});
test('月份标题回调失败，下一次滚动可以再次报告', async () => {
    const s = setup(); s.failReport(); s.connect(); s.flush();
    await new Promise(resolve => setImmediate(resolve));
    s.handlers.get('scroll')(); s.flush();
    assert.equal(s.calls.filter(call => call[0] === 'ReportVisibleMonth').length, 2);
});
