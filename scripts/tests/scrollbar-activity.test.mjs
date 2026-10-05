import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/scrollbar.js', import.meta.url), 'utf8');
function fixture(reduced = false) {
    const frames = new Map(), timers = new Map(), handlers = new Map(), lifecycle = new Map();
    let id = 0, writes = 0;
    const document = {
        addEventListener(type, callback, options) { handlers.set(type, { callback, options }); },
        removeEventListener(type) { handlers.delete(type); }
    };
    const window = {
        matchMedia: () => ({ matches: reduced }),
        addEventListener(type, callback) { lifecycle.set(type, callback); },
        removeEventListener(type) { lifecycle.delete(type); }
    };
    const context = vm.createContext({ window, document,
        requestAnimationFrame(callback) { frames.set(++id, callback); return id; },
        cancelAnimationFrame(key) { frames.delete(key); },
        setTimeout(callback, delay) { timers.set(++id, { callback, delay }); return id; },
        clearTimeout(key) { timers.delete(key); }
    });
    const element = (eligible = true) => {
        const classes = new Set();
        return { isConnected: true, matches: () => eligible,
            classList: { contains: key => classes.has(key),
                add(key) { writes++; classes.add(key); }, remove(key) { classes.delete(key); } } };
    };
    vm.runInContext(source, context);
    return { window, handlers, lifecycle, frames, timers, element, context,
        writes: () => writes,
        scroll(target) { handlers.get('scroll').callback({ target }); },
        frame() { const callbacks = [...frames.values()]; frames.clear(); callbacks.forEach(callback => callback()); },
        expire() { const callbacks = [...timers.values()]; timers.clear(); callbacks.forEach(timer => timer.callback()); }
    };
}
test('委托捕获监听为 passive，动态滚动区域无需重新初始化', () => {
    const f = fixture();
    assert.equal(f.handlers.get('scroll').options.passive, true);
    assert.equal(f.handlers.get('scroll').options.capture, true);
    const e = f.element(); f.scroll(e); f.frame();
    assert.equal(e.classList.contains('is-scrolling'), true);
});
test('连续滚动只保留一帧及一个650ms计时器，不重复写class', () => {
    const f = fixture(), e = f.element();
    for (let i = 0; i < 100; i++) f.scroll(e);
    assert.equal(f.frames.size, 1); assert.equal(f.timers.size, 1);
    assert.equal([...f.timers.values()][0].delay, 650);
    f.frame(); f.scroll(e); f.frame(); assert.equal(f.writes(), 1);
    f.expire(); assert.equal(e.classList.contains('is-scrolling'), false);
});
test('嵌套区域只标记实际滚动目标，隐藏及未标记区域不参与', () => {
    const f = fixture(), e = f.element(), parent = f.element();
    f.scroll(e); f.scroll(f.element(false)); f.scroll({}); f.frame();
    assert.equal(e.classList.contains('is-scrolling'), true);
    assert.equal(parent.classList.contains('is-scrolling'), false);
    assert.equal(f.timers.size, 1);
});
test('各区域独立防抖，移除的节点不会在下一帧被写入', () => {
    const f = fixture(), a = f.element(), b = f.element();
    f.scroll(a); f.scroll(b); b.isConnected = false; f.frame();
    assert.equal(f.writes(), 1); assert.equal(f.timers.size, 2);
    f.expire(); assert.equal(f.timers.size, 0);
});
test('卸载清理计时器和待执行帧，重复载入不遗留监听或class', () => {
    const f = fixture(), a = f.element(); f.scroll(a); f.frame(); f.scroll(a);
    vm.runInContext(source, f.context);
    assert.equal(f.frames.size, 0); assert.equal(f.timers.size, 0);
    assert.equal(a.classList.contains('is-scrolling'), false);
    assert.equal(f.handlers.size, 1); assert.equal(f.lifecycle.size, 2);
    f.window.moicalendarScrollbars.dispose(); assert.equal(f.handlers.size, 0);
    assert.equal(f.lifecycle.size, 0);
});
test('页面挂起清理，bfcache恢复可继续显示滚动状态', () => {
    const f = fixture(), e = f.element(); f.scroll(e); f.frame();
    f.lifecycle.get('pagehide')(); assert.equal(f.timers.size, 0);
    assert.equal(e.classList.contains('is-scrolling'), false);
    f.lifecycle.get('pageshow')(); f.scroll(e); f.frame();
    assert.equal(e.classList.contains('is-scrolling'), true);
});
test('减少动态效果时缩短停留时间', () => {
    const f = fixture(true); f.scroll(f.element());
    assert.equal([...f.timers.values()][0].delay, 100);
});
