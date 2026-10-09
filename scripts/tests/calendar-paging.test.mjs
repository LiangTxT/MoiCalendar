import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarPaging.js', import.meta.url), 'utf8');
function setup({ reduce = false, overflow = false, gutter = 0 } = {}) {
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
    const horizontal = { scrollLeft: 0, clientWidth: 400 - gutter, scrollWidth: overflow ? 1000 : 400,
        getBoundingClientRect: () => ({ right: 400 }), cloneNode: () => ghost };
    frame.getBoundingClientRect = () => ({ right: horizontal.scrollWidth - horizontal.scrollLeft });
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
    const touch = (x = 600, y = 100, extra = {}) => event({
        touches: [{ identifier: 7, clientX: x, clientY: y }],
        changedTouches: [{ identifier: 7, clientX: x, clientY: y }], ...extra });
    return { ui, surface, horizontal, window, timeline, ghost, dotNet, motions, calls, target, event, touch, flush,
        tick: n => clock += n, removed: () => removed,
        navigation: () => calls.filter(x => x.name === 'NavigateTimeGridPeriod') };
}

test('超宽周表格由原生触摸处理周内滚动，不使用指针捕获', async () => {
    for (const header of [true, false]) for (const left of [0, 300, 600]) {
        const f = setup({ overflow: true });
        f.horizontal.scrollLeft = left;
        const e = extra => f.event({ pointerType: 'touch', target: f.target(header), ...extra });
        f.surface.fire('pointerdown', e());
        const move = e({clientX: 350});
        f.window.fire('pointermove', move);
        await f.window.fire('pointerup', e({clientX: 350}));
        assert.equal(move.prevented, undefined);
        assert.equal(f.calls.length, 0);
        assert.equal(f.surface.classList.contains('has-week-overflow'), true);
    }
});

test('超宽周表格两侧向外触摸滑动切换周，进入相邻周的连续边缘', async () => {
    for (const header of [true, false]) for (const [left, dx, direction] of [[0, 250, -1], [600, -250, 1]]) {
        const f = setup({ overflow: true });
        f.horizontal.scrollLeft = left;
        const extra = { target: f.target(header) };
        f.surface.fire('touchstart', f.touch(600, 100, extra));
        const move = f.touch(600 + dx, 100, extra);
        f.surface.fire('touchmove', move);
        f.tick(500);
        await f.surface.fire('touchend', f.touch(600 + dx, 100, { ...extra, touches: [] }));
        assert.equal(move.prevented, true);
        assert.equal(f.navigation().length, 1);
        assert.equal(f.navigation()[0].direction, direction);
        assert.equal(f.horizontal.scrollLeft, direction > 0 ? 0 : 600);
        assert.equal(f.timeline.scrollTop, 640);
        assert.equal(f.surface.classList.contains('is-period-paging'), false);
        const click = f.event(); f.surface.fire('click', click);
        assert.equal(click.stopped, true, '跨周滑动不能误打开日程编辑器');
    }
});

test('周内触摸和边缘向内滑动不翻周，即使同一次滚动到达边缘', async () => {
    for (const [left, dx] of [[0, -250], [600, 250], [300, -250], [300, 250]]) {
        const f = setup({ overflow: true });
        f.horizontal.scrollLeft = left;
        f.surface.fire('touchstart', f.touch());
        f.horizontal.scrollLeft = dx < 0 ? 600 : 0;
        const move = f.touch(600 + dx);
        f.surface.fire('touchmove', move);
        await f.surface.fire('touchend', f.touch(600 + dx, 100, { touches: [] }));
        assert.equal(move.prevented, undefined);
        assert.equal(f.calls.length, 0);
    }
});

test('稳定滚动条槽使 clientWidth 偏小，实际右边缘仍可切换下一周', async () => {
    const f = setup({ overflow: true, gutter: 8 });
    f.horizontal.scrollLeft = 600;
    f.surface.fire('touchstart', f.touch());
    f.surface.fire('touchmove', f.touch(350));
    await f.surface.fire('touchend', f.touch(350, 100, { touches: [] }));
    assert.equal(f.navigation()[0]?.direction, 1);
});

test('边缘短滑、取消、纵滑、长按和多指手势不切换周', async () => {
    for (const mode of ['short', 'cancel', 'vertical', 'hold', 'pinch']) {
        const f = setup({ overflow: true });
        f.surface.fire('touchstart', f.touch(600, 100, { target: f.target(false) }));
        if (mode === 'hold') f.tick(400);
        const x = mode === 'short' ? 630 : 850;
        const move = f.touch(x, mode === 'vertical' ? 400 : 100);
        f.surface.fire('touchmove', move);
        if (mode === 'pinch') f.surface.fire('touchmove', f.touch(x, 100, { touches: [move.touches[0], { identifier: 8 }] }));
        f.tick(500);
        const type = mode === 'cancel' ? 'touchcancel' : 'touchend';
        await f.surface.fire(type, f.touch(x, 100, { touches: [], type }));
        assert.equal(f.navigation().length, 0, mode);
        assert.equal(f.surface.classList.contains('is-period-paging'), false, mode);
    }
});

test('边缘触摸保留浮层、日程拖动及减少动效的约束，并清理监听器', async () => {
    for (const mode of ['overlay', 'drag', 'reduce']) {
        const f = setup({ overflow: true, reduce: mode === 'reduce' });
        if (mode === 'overlay') f.ui.rendered(f.surface, 'same', false);
        if (mode === 'drag') f.surface.classList.add('is-browser-interacting');
        f.surface.fire('touchstart', f.touch());
        f.surface.fire('touchmove', f.touch(850));
        await f.surface.fire('touchend', f.touch(850, 100, { touches: [] }));
        assert.equal(f.navigation().length, mode === 'reduce' ? 1 : 0);
        assert.equal(f.motions.length, 0);
        f.ui.dispose(f.surface);
        assert.equal(f.surface.handlers.size, 0);
        assert.equal(f.window.handlers.size, 0);
    }
});

test('边缘拖动期间第二根手指放下后直接抬起，也立即取消位移', async () => {
    const f = setup({ overflow: true });
    f.surface.fire('touchstart', f.touch());
    f.surface.fire('touchmove', f.touch(850));
    assert.equal(f.surface.classList.contains('is-period-paging'), true);
    f.surface.fire('touchstart', f.touch(850, 100, { touches: [
        { identifier: 7, clientX: 850, clientY: 100 }, { identifier: 8, clientX: 900, clientY: 100 }
    ] }));
    await f.surface.fire('touchend', f.touch(850, 100, { touches: [] }));
    assert.equal(f.surface.classList.contains('is-period-paging'), false);
    assert.equal(f.navigation().length, 0);
});

test('超宽表格横向滚轮不拦截；宽度恢复后重新允许翻页', async () => {
    const f = setup({overflow: true});
    const wheel = f.event({deltaX: 100, deltaY: 0});
    f.surface.fire('wheel', wheel);
    assert.equal(wheel.prevented, undefined);
    assert.equal(f.navigation().length, 0);
    f.horizontal.scrollWidth = 400;
    f.ui.rendered(f.surface, 'week:2026-10-05', true);
    assert.equal(f.surface.classList.contains('has-week-overflow'), false);
    f.surface.fire('wheel', f.event({deltaX: 100, deltaY: 0}));
    await f.flush();
    assert.equal(f.navigation().length, 1);
});

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
