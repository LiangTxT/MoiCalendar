import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarInteraction.js', import.meta.url), 'utf8');
function fixture(pointerType = 'mouse') {
    const handlers = new Map();
    const frames = new Map();
    const calls = [];
    let time = 0;
    let frameId = 0;
    const node = () => ({
        style: { setProperty() {} },
        setAttribute() {},
        classList: { add() {}, remove() {}, contains() { return false; } },
        children: [],
        append(...children) { this.children.push(...children); for (const child of children) child.parentElement = this; },
        remove() { this.removed = true; }
    });
    const window = {
        addEventListener: (name, fn) => handlers.set(name, fn),
        removeEventListener: (name, fn) => { if (handlers.get(name) === fn) handlers.delete(name); }
    };
    const body = node();
    vm.runInNewContext(source, {
        window, document: { createElement: node, body, activeElement: null },
        performance: { now: () => time },
        requestAnimationFrame: fn => { frames.set(++frameId, fn); return frameId; },
        cancelAnimationFrame: id => frames.delete(id)
    });
    const surface = { ...node(), setPointerCapture() {}, hasPointerCapture: () => false };
    const grid = {
        children: Array.from({ length: 7 }, node), scrollHeight: 1440,
        getBoundingClientRect: () => ({ left: 0, top: 100, width: 700 })
    };
    const scroll = { scrollTop: 0, getBoundingClientRect: () => ({ top: 100, bottom: 1000 }) };
    const config = {
        pointerId: 1, pointerType, startX: 50, startY: 640, kind: 'move',
        dates: ['2026-10-05', '2026-10-06', '2026-10-07', '2026-10-08', '2026-10-09', '2026-10-10', '2026-10-11'],
        originalDate: '2026-10-05', visibleStartMinute: 0, visibleEndMinute: 1440,
        eventStartMinute: 540, durationMinutes: 60, label: '测试事件'
    };
    window.moicalendarInteraction.beginTimeGridInteraction(surface, grid, scroll,
        { invokeMethodAsync: async (method, result) => calls.push({ method, result }) }, config);
    const event = (options = {}) => ({ pointerId: 1, clientX: 50, clientY: 640, preventDefault() { this.prevented = true; }, ...options });
    const dispatch = (name, value) => handlers.get(name)?.(value);
    const flush = () => { const pending = [...frames.values()]; frames.clear(); for (const fn of pending) fn(); };
    return { handlers, calls, frames, body, scroll, event, dispatch, flush, advance: ms => time += ms };
}

test('轻点完成通知 .NET 清理临时状态，不创建镜像或提交', async () => {
    const f = fixture();
    await f.dispatch('pointerup', f.event());
    assert.equal(f.calls.length, 1);
    assert.equal(f.calls[0].result.cancelled, true);
    assert.equal(f.body.children.length, 0);
    assert.equal(f.handlers.size, 0);
});

test('快速触摸滑动退出长按识别，不在滚动中途变成拖拽', async () => {
    const f = fixture('touch');
    f.advance(100);
    const move = f.event({ clientY: 670 });
    f.dispatch('pointermove', move);
    f.advance(400);
    f.dispatch('pointermove', f.event({ clientY: 720 }));
    assert.equal(move.prevented, undefined);
    assert.equal(f.calls.length, 1);
    assert.equal(f.calls[0].result.cancelled, true);
    assert.equal(f.body.children.length, 0);
    assert.equal(f.handlers.size, 0);
});

test('单指长按拖拽才阻止原生触摸移动，松手后监听器完全清理', async () => {
    const f = fixture('touch');
    f.advance(360);
    f.dispatch('pointermove', f.event({ clientY: 700 }));
    const touch = f.event({ touches: [{}] });
    f.dispatch('touchmove', touch);
    assert.equal(touch.prevented, true);
    await f.dispatch('pointerup', f.event({ clientY: 700 }));
    assert.equal(f.calls[0].result.cancelled, false);
    assert.equal(f.handlers.size, 0);
});

test('提交松手最终坐标，不使用上一帧的日期和时间', async () => {
    const f = fixture();
    f.dispatch('pointermove', f.event({ clientY: 700 }));
    f.flush();
    await f.dispatch('pointerup', f.event({ clientX: 150, clientY: 760 }));
    const result = f.calls[0].result;
    assert.equal(result.targetDate, '2026-10-06');
    assert.equal(result.startMinute, 660);
    assert.equal(result.endMinute, 720);
    assert.equal(f.frames.size, 0);
});

test('取消不执行剩余动画帧或边缘自动滚动，只报告取消', async () => {
    const f = fixture();
    f.dispatch('pointermove', f.event({ clientY: 990 }));
    await f.dispatch('pointercancel', f.event({ clientY: 990 }));
    assert.equal(f.scroll.scrollTop, 0);
    assert.equal(f.frames.size, 0);
    assert.equal(f.calls[0].result.cancelled, true);
    assert.ok(f.body.children.every(child => child.removed));
});

test('重复结束回调只通知一次，忽略其他指针', async () => {
    const f = fixture();
    const up = f.handlers.get('pointerup');
    await up(f.event({ pointerId: 2 }));
    assert.equal(f.calls.length, 0);
    f.dispatch('pointermove', f.event({ clientY: 700 }));
    await up(f.event({ clientY: 700 }));
    await up(f.event({ clientY: 700 }));
    assert.equal(f.calls.length, 1);
});
