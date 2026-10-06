import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarPaging.js', import.meta.url), 'utf8');
function setup({ reduce = false } = {}) {
    let clock = 1000, nextTimer = 0;
    const timers = new Map(), calls = [], motions = [];
    const events = () => ({ handlers: new Map(),
        addEventListener(name, handler) { this.handlers.set(name, handler); },
        removeEventListener(name) { this.handlers.delete(name); },
        fire(name, data) { return this.handlers.get(name)?.(data); } });
    const classes = () => { const set = new Set(); return { add: n => set.add(n), remove: n => set.delete(n), contains: n => set.has(n) }; };
    const frame = { style: {}, animate: (frames, options) => {
        motions.push({ frames, options }); return { finished: Promise.resolve(), cancel() {} };
    } };
    const timeline = { scrollTop: 640 };
    let removed = 0;
    const ghost = { style: {}, classList: classes(), setAttribute() {}, querySelectorAll: () => [],
        querySelector: selector => selector === '.week-grid-frame' ? { style: {} } : { scrollTop: 0 },
        remove() { removed++; }, animate: frame.animate };
    const horizontal = { scrollLeft: 0, cloneNode: () => ghost };
    const surface = { ...events(), style: {}, classList: classes(), clientWidth: 1000,
        querySelector: selector => selector === '.week-grid-frame' ? frame : selector === '.week-timed-scroll' ? timeline : horizontal,
        append() {}, setPointerCapture() {}, hasPointerCapture: () => false };
    const window = { ...events(), matchMedia: () => ({ matches: reduce }), moicalendarInteraction: { cancelActiveInteraction() { calls.push('cancel-js'); } } };
    vm.runInNewContext(source, { window, console, performance: { now: () => clock },
        requestAnimationFrame: callback => { callback(); return 0; }, cancelAnimationFrame() {},
        setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; },
        clearTimeout: id => timers.delete(id) });
    const ui = window.moicalendarPaging;
    const dotNet = { async invokeMethodAsync(name, direction) {
        calls.push({ name, direction });
        if (name === 'NavigateTimeGridPeriod') { timeline.scrollTop = 0; ui.rendered(surface, `new:${calls.length}`, true); }
    } };
    ui.attach(surface, dotNet);
    ui.rendered(surface, 'week:2026-10-05', true);
    const target = (header = false) => ({ closest: selector => selector === '.week-day-headings' ? header ? {} : null : {} });
    const event = (extra = {}) => ({ button: 0, isPrimary: true, pointerId: 1, pointerType: 'mouse',
        clientX: 600, clientY: 100, target: target(true), preventDefault() { this.prevented = true; }, stopImmediatePropagation() { this.stopped = true; }, ...extra });
    const flush = async () => {
        const scheduled = [...timers.entries()]; timers.clear();
        for (const [, callback] of scheduled) callback();
        for (let i = 0; i < 12; i++) await Promise.resolve();
    };
    return { ui, surface, window, timeline, ghost, dotNet, motions, calls, target, event, flush,
        tick: n => clock += n, removed: () => removed,
        navigation: () => calls.filter(x => x.name === 'NavigateTimeGridPeriod') };
}

test('顶部左拖下一周，右拖上一周；保留时间轴位置', async () => {
    for (const [dx, direction] of [[-250, 1], [250, -1]]) {
        const f = setup();
        f.surface.fire('pointerdown', f.event());
        f.window.fire('pointermove', f.event({ clientX: 600 + dx }));
        f.tick(500);
        await f.window.fire('pointerup', f.event({ clientX: 600 + dx }));
        assert.equal(f.navigation()[0].direction, direction);
        assert.equal(f.timeline.scrollTop, 640);
        assert.equal(f.removed(), 1);
        assert.equal(f.surface.classList.contains('is-period-paging'), false);
    }
});

test('足量横滑立即提交，不等待滚轮结束计时器', async () => {
    const f = setup();
    f.surface.fire('wheel', f.event({ deltaX: 80, deltaY: 0 }));
    assert.equal(f.navigation().length, 1);
    await f.flush();
});

test('等待日程加载时，旧页面保持松手位置，不跳回原点', async () => {
    const f = setup();
    let finish;
    f.dotNet.invokeMethodAsync = name => name === 'NavigateTimeGridPeriod'
        ? new Promise(resolve => { finish = resolve; }) : Promise.resolve();
    f.surface.fire('pointerdown', f.event());
    f.window.fire('pointermove', f.event({ clientX: 350 }));
    f.tick(500);
    const pending = f.window.fire('pointerup', f.event({ clientX: 350 }));
    for (let i = 0; i < 8 && !finish; i++) await Promise.resolve();
    assert.equal(f.ghost.style.transform, 'translate3d(-250px,0,0)');
    f.ui.rendered(f.surface, 'next', true);
    finish();
    await pending;
});
test('短拖动回弹，取消手势不翻页', async () => {
    for (const type of ['pointerup', 'pointercancel']) {
        const f = setup();
        f.surface.fire('pointerdown', f.event());
        f.window.fire('pointermove', f.event({ clientX: 570 }));
        f.tick(500);
        await f.window.fire(type, f.event({ clientX: 570, type }));
        assert.equal(f.navigation().length, 0);
    }
});
test('网格鼠标拖动、纵向滚轮与缩放不被翻页拦截', async () => {
    const f = setup();
    const gridTarget = f.target(false);
    f.surface.fire('pointerdown', f.event({ target: gridTarget }));
    f.window.fire('pointermove', f.event({ clientX: 100 }));
    await f.window.fire('pointerup', f.event({ clientX: 100 }));
    for (const params of [{ deltaX: 0, deltaY: 120 }, { deltaX: 120, deltaY: 0, ctrlKey: true }]) {
        const event = f.event({ target: gridTarget, ...params });
        f.surface.fire('wheel', event);
        assert.equal(event.prevented, undefined);
    }
    assert.equal(f.navigation().length, 0);
});
test('顶部纵向滚轮翻页，触控板细小增量可以累计', async () => {
    for (const header of [true, false]) {
        const f = setup();
        for (let i = 0; i < 10; i++) {
            f.surface.fire('wheel', f.event({ target: f.target(header), deltaX: header ? 0 : 5, deltaY: header ? 5 : 0 }));
            f.tick(10);
        }
        await f.flush();
        assert.equal(f.navigation().length, 1);
        assert.equal(f.navigation()[0].direction, 1);
        for (let i = 0; i < 8; i++) { f.tick(30); f.surface.fire('wheel', f.event({ deltaX: 20, deltaY: 0 })); }
        await f.flush();
        assert.equal(f.navigation().length, 1, '惯性尾部不能连翻');
        f.tick(250);
        f.surface.fire('wheel', f.event({ deltaX: -100, deltaY: 0 }));
        await f.flush();
        assert.equal(f.navigation()[1].direction, -1);
    }
});
test('触摸横滑可翻页；纵向滑动和长按日程保留原操作', async () => {
    for (const [dy, delay, expected] of [[0, 100, 1], [300, 100, 0], [0, 400, 0]]) {
        const f = setup();
        const event = extra => f.event({ target: f.target(false), pointerType: 'touch', ...extra });
        f.surface.fire('pointerdown', event()); f.tick(delay);
        f.window.fire('pointermove', event({ clientX: 350, clientY: 100 + dy }));
        await f.window.fire('pointerup', event({ clientX: 350, clientY: 100 + dy }));
        assert.equal(f.navigation().length, expected);
    }
});
test('拖动后的 click 被拦截，普通日期点击不受影响', async () => {
    const f = setup();
    const click = f.event(); f.surface.fire('click', click); assert.equal(click.prevented, undefined);
    f.surface.fire('pointerdown', f.event());
    f.window.fire('pointermove', f.event({ clientX: 300 }));
    await f.window.fire('pointerup', f.event({ clientX: 300 }));
    const ghostClick = f.event(); f.surface.fire('click', ghostClick); assert.equal(ghostClick.stopped, true);
});
test('关闭动效仍可翻页，卸载清理监听器', async () => {
    const f = setup({ reduce: true });
    f.surface.fire('wheel', f.event({ deltaY: 100, deltaX: 0 }));
    await f.flush();
    assert.equal(f.navigation().length, 1);
    assert.equal(f.motions.length, 0);
    f.ui.dispose(f.surface);
    assert.equal(f.surface.handlers.size, 0);
    assert.equal(f.window.handlers.size, 0);
});
test('浮层打开和正在调整日程时不允许翻页', async () => {
    const f = setup();
    f.ui.rendered(f.surface, 'same', false);
    f.surface.fire('wheel', f.event({ deltaX: 100, deltaY: 0 }));
    await f.flush(); assert.equal(f.navigation().length, 0);
    f.ui.rendered(f.surface, 'same', true);
    f.surface.classList.add('is-browser-interacting');
    f.surface.fire('pointerdown', f.event());
    await f.window.fire('pointerup', f.event({ clientX: 300 }));
    assert.equal(f.navigation().length, 0);
});
