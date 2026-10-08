import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarUi.js', import.meta.url), 'utf8');
function setup(heights = [600, 600], top = 700) {
    let callback, connected = true, finish;
    const listeners = new Map(), writes = [];
    let position = top, sizes = heights;
    const root = {
        clientWidth: 820, clientHeight: 1000,
        get scrollTop() { return position; }, set scrollTop(value) { position = value; writes.push(value); },
        getBoundingClientRect: () => ({ top: 100 }),
        querySelectorAll: () => nodes,
        addEventListener: (name, handler) => listeners.set(name, handler),
        removeEventListener: (name) => listeners.delete(name)
    };
    const nodes = heights.map((_, index) => ({
        isConnected: true,
        getBoundingClientRect: () => ({ top: 100 + sizes.slice(0, index).reduce((a, b) => a + b, 0) - position, height: sizes[index] })
    }));
    const context = { window: {}, ResizeObserver: class {
        constructor(handler) { callback = handler; } observe() {} disconnect() { connected = false; }
    } };
    vm.runInNewContext(source, context);
    const activity = { whenIdle: () => finish ? new Promise(resolve => { finish = resolve; }) : Promise.resolve() };
    const anchor = context.window.moicalendarUi.observeResizeAnchor(root, '.block', activity);
    return { root, nodes, writes, listeners, anchor,
        resize: (newSizes, width = 1180, height = 640) => { sizes = newSizes; root.clientWidth = width; root.clientHeight = height; return callback(); },
        scroll: value => { position = value; listeners.get('scroll')?.(); },
        hold: () => { finish = () => {}; }, release: () => { const resolve = finish; finish = null; resolve(); },
        connected: () => connected };
}

test('月视图旋转后保留同一周及周内比例，来回旋转不跳月份', async () => {
    const s = setup();
    await s.resize([360, 360]); assert.equal(s.root.scrollTop, 420);
    await s.resize([600, 600], 820, 1000); assert.equal(s.root.scrollTop, 700);
});
test('日程插图变高时保留可见日程，不按整个月份高度缩放位置', async () => {
    const s = setup([300, 60, 60], 375);
    await s.resize([450, 60, 60]); assert.equal(s.root.scrollTop, 525);
});
test('正常滚动刷新数值后旋转，保持最新位置', async () => {
    const s = setup(); s.scroll(850);
    await s.resize([360, 360]); assert.equal(s.root.scrollTop, 510);
});
test('旋转期间浏览器钳制滚动值或 DOM 刷新不覆盖旧锚点', async () => {
    const s = setup(); s.hold();
    const waiting = s.resize([360, 360]); s.scroll(300); s.anchor.refresh();
    assert.equal(s.writes.length, 0);
    s.release(); await waiting; assert.equal(s.root.scrollTop, 420);
});
test('连续尺寸变化只应用最后一次补偿', async () => {
    const s = setup(); const first = s.resize([360, 360]); const second = s.resize([480, 480], 1000, 800);
    await Promise.all([first, second]); assert.deepEqual(s.writes, [560]);
});
test('卸载期间等待结束后不写位置，移除尺寸和滚动监听', async () => {
    const s = setup(); s.hold(); const waiting = s.resize([360, 360]); s.anchor.dispose();
    s.release(); await waiting;
    assert.equal(s.writes.length, 0); assert.equal(s.listeners.size, 0); assert.equal(s.connected(), false);
});
test('原节点已移除时不跳到旧位置，新布局刷新后可继续旋转', async () => {
    const s = setup(); for (const node of s.nodes) node.isConnected = false;
    await s.resize([360, 360]); assert.equal(s.writes.length, 0);
    for (const node of s.nodes) node.isConnected = true;
    s.anchor.refresh(); await s.resize([600, 600], 820, 1000);
    assert.ok(Math.abs(s.root.scrollTop - 700 / 360 * 600) < 0.001);
});
