import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarUi.js', import.meta.url), 'utf8');

test('手指未松开时，即使滚动间隔超过静默时间也不回收月份或重设位置', async () => {
    const f = setup(); f.initialize(); f.takeFrame()();
    f.handlers.get('touchstart')();
    f.scrollTo(0); f.flushTimers();
    assert.equal(f.frames.size, 0);
    assert.equal(f.calls.length, 0);
    assert.equal(f.stream.scrollTop, 0);
    f.handlers.get('touchend')();
    await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'ChangeVisibleMonth').length, 1);
});

test('快速越过多个月份，只按最终可见月份一次补充窗口', async () => {
    const f = setup();
    f.setMonths(['2026-06','2026-07','2026-08','2026-09','2026-10','2026-11','2026-12','2027-01','2027-02'], Array(9).fill(600));
    f.initialize(); f.takeFrame()();
    assert.equal(f.stream.scrollTop, 2400);
    f.scrollTo(1700); f.scrollTo(900); f.scrollTo(100);
    assert.equal(f.calls.length, 0);
    await f.takeFrame()();
    assert.deepEqual(f.calls.filter(c => c[0] === 'ChangeVisibleMonth'), [['ChangeVisibleMonth', -4]]);
});

test('查询返回时重新开始触摸，提交必须继续等待松手和惯性滚动结束', async () => {
    const f = setup(); f.initialize(); f.takeFrame()();
    f.handlers.get('touchstart')();
    let settled = false;
    const waiting = f.ui.waitForScrollIdle(f.stream).then(() => { settled = true; });
    f.flushTimers(); await Promise.resolve(); assert.equal(settled, false);
    f.handlers.get('touchend')();
    f.scrollTo(1150); await Promise.resolve(); assert.equal(settled, false);
    f.flushTimers(); await waiting; assert.equal(settled, true);
});

test('卸载释放等待滚动结束的请求，不留下悬挂 Promise', async () => {
    const f = setup(); f.initialize(); f.takeFrame()();
    f.handlers.get('touchstart')();
    const waiting = f.ui.waitForScrollIdle(f.stream);
    f.ui.disposeMonthStream(f.stream); await waiting;
    assert.equal(f.timers.size, 0); assert.equal(f.handlers.size, 0);
});

test('多指操作只抬起一根手指时，仍不允许回收月份', async () => {
    const f = setup(); f.initialize(); f.takeFrame()();
    f.handlers.get('touchstart')(); f.scrollTo(0);
    f.handlers.get('touchend')({touches:[{}]}); f.flushTimers();
    assert.equal(f.frames.size, 0); assert.equal(f.calls.length, 0);
    f.handlers.get('touchend')({touches:[]}); await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'ChangeVisibleMonth').length, 1);
});

function setup() {
    const frames = new Map();
    const observers = [];
    const calls = [];
    const listeners = new Set(), handlers = new Map(), timers = new Map();
    let nextFrame = 1;
    let months = ['2026-08', '2026-09', '2026-10', '2026-11', '2026-12'];
    let heights = [600, 600, 600, 600, 600];
    let pendingChange;
    let pendingDisplay;
    const stream = {
        scrollTop: 0, clientHeight: 600,
        getBoundingClientRect: () => ({ top: 0 }),
        querySelectorAll: () => months.map((month, index) => {
            const contentTop = heights.slice(0, index).reduce((sum, height) => sum + height, 0);
            return {
                dataset: { month }, offsetHeight: heights[index],
                getBoundingClientRect: () => ({ top: contentTop - stream.scrollTop })
            };
        }),
        addEventListener: (name, handler) => { if (name === 'scroll') listeners.add(handler); else handlers.set(name, handler); },
        removeEventListener: (name, handler) => { if (name === 'scroll') listeners.delete(handler); else handlers.delete(name); }
    };
    const window = {};
    vm.runInNewContext(source, {
        window,
        setTimeout: callback => { const id = nextFrame++; timers.set(id, callback); return id; },
        clearTimeout: id => timers.delete(id),
        requestAnimationFrame: callback => { const id = nextFrame++; frames.set(id, callback); return id; },
        cancelAnimationFrame: id => frames.delete(id),
        MutationObserver: class {
            constructor(callback) { this.callback = callback; observers.push(this); }
            observe() { this.connected = true; }
            disconnect() { this.connected = false; }
        }
    });
    const dotnet = {
        invokeMethodAsync(name, ...args) {
            calls.push([name, ...args]);
            if (name === 'SetDisplayedMonth' && pendingDisplay) return pendingDisplay;
            return name === 'ChangeVisibleMonth' && pendingChange ? pendingChange : Promise.resolve();
        }
    };
    const flushTimers = () => {
        for (const [id, callback] of [...timers]) { timers.delete(id); callback(); }
    };
    const takeFrame = () => {
        flushTimers();
        const [id, callback] = frames.entries().next().value;
        frames.delete(id);
        return callback;
    };
    return {
        ui: window.moicalendarUi, stream, frames, observers, calls, listeners, handlers, timers,
        initialize: () => window.moicalendarUi.initializeMonthStream(stream, dotnet),
        takeFrame, flushTimers,
        scrollTo(top) { stream.scrollTop = top; for (const handler of listeners) handler(); },
        deferChange(promise) { pendingChange = promise; },
        deferDisplay(promise) { pendingDisplay = promise; },
        setMonths(value, panelHeights = heights) { months = value; heights = panelHeights; },
        mutate() { for (const observer of observers) if (observer.connected) observer.callback(); }
    };
}

test('卸载取消尚未执行的月份首次定位', () => {
    const f = setup();
    f.initialize();
    f.ui.disposeMonthStream(f.stream);
    assert.equal(f.frames.size, 0);
    assert.equal(f.stream.scrollTop, 0);
    assert.equal(f.listeners.size, 0);
    assert.equal(f.observers[0].connected, false);
});

test('重复初始化只保留一个首次定位回调和监听器', () => {
    const f = setup();
    f.initialize();
    f.initialize();
    assert.equal(f.frames.size, 1);
    assert.equal(f.listeners.size, 2);
    assert.equal(f.observers.filter(o => o.connected).length, 1);
    f.takeFrame()();
    assert.equal(f.stream.scrollTop, 1200);
});

test('已取出的旧定位回调在卸载后也不得修改位置', () => {
    const f = setup();
    f.initialize();
    const callback = f.takeFrame();
    f.ui.disposeMonthStream(f.stream);
    callback();
    assert.equal(f.stream.scrollTop, 0);
});

test('中心月份内滚动只合并到一帧，不触发窗口换月', async () => {
    const f = setup();
    f.initialize(); f.takeFrame()();
    f.scrollTo(1300); f.scrollTo(1400);
    assert.equal(f.timers.size, 1);
    await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'ChangeVisibleMonth').length, 0);
});

test('卸载后已取出的旧滚动帧不得调用 .NET', async () => {
    const f = setup();
    f.initialize(); f.takeFrame()();
    f.scrollTo(1800);
    const callback = f.takeFrame();
    f.ui.disposeMonthStream(f.stream);
    await callback();
    assert.equal(f.calls.length, 0);
});

test('加载期间继续滚动，换月后保持最新视觉锚点且仅补偿一次', async () => {
    const f = setup();
    let resolveChange;
    f.deferChange(new Promise(resolve => { resolveChange = resolve; }));
    f.initialize(); f.takeFrame()();
    f.scrollTo(1800);
    const loading = f.takeFrame()();
    f.scrollTo(1900);
    f.setMonths(['2026-09', '2026-10', '2026-11', '2026-12', '2027-01']);
    f.mutate();
    assert.equal(f.stream.scrollTop, 1300);
    resolveChange(); await loading;
    assert.equal(f.stream.scrollTop, 1300);
    assert.equal(f.calls.filter(c => c[0] === 'ChangeVisibleMonth').length, 1);
});

test('换月请求尚未返回时卸载，不再补偿位置或请求月份', async () => {
    const f = setup();
    let resolveChange;
    f.deferChange(new Promise(resolve => { resolveChange = resolve; }));
    f.initialize(); f.takeFrame()();
    f.scrollTo(1800);
    const loading = f.takeFrame()();
    f.ui.disposeMonthStream(f.stream);
    f.setMonths(['2026-09', '2026-10', '2026-11', '2026-12', '2027-01']);
    f.mutate(); resolveChange(); await loading;
    assert.equal(f.stream.scrollTop, 1800);
    assert.equal(f.frames.size, 0);
    assert.equal(f.listeners.size, 0);
});

test('月份标题更新失败后，下一次滚动可重新报告同一月份', async () => {
    const f = setup();
    let rejectDisplay;
    const failure = new Promise((_, reject) => { rejectDisplay = reject; });
    // 夹具也收集拒绝，旧实现的失败只通过行为断言报告。
    failure.catch(() => {});
    f.deferDisplay(failure);
    f.initialize(); f.takeFrame()();
    f.scrollTo(1600); await f.takeFrame()();
    rejectDisplay(new Error('测试：标题回调失败'));
    await Promise.resolve();
    f.deferDisplay(null);
    f.scrollTo(1610); await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'SetDisplayedMonth').length, 2);
});

test('较旧月份报告失败不能撤销较新月份的成功报告', async () => {
    const f = setup();
    let rejectDisplay;
    const failure = new Promise((_, reject) => { rejectDisplay = reject; });
    failure.catch(() => {});
    f.deferDisplay(failure);
    f.initialize(); f.takeFrame()();
    f.scrollTo(1600); await f.takeFrame()();
    f.deferDisplay(null);
    f.scrollTo(1300); await f.takeFrame()();
    rejectDisplay(new Error('测试：旧报告迟到的失败'));
    await Promise.resolve();
    f.scrollTo(1310); await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'SetDisplayedMonth').length, 2);
});

test('窗口换月失败解除等待状态，下一次滚动可以重试', async () => {
    const f = setup();
    f.deferChange(Promise.reject(new Error('测试：月份加载失败')));
    f.initialize(); f.takeFrame()();
    f.scrollTo(1800); await f.takeFrame()();
    assert.equal(f.stream.scrollTop, 1800);
    f.deferChange(null);
    f.scrollTo(1810); await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'ChangeVisibleMonth').length, 2);
});

test('返回同一月份后，旧失败也不能使最新成功报告失效', async () => {
    const f = setup();
    let rejectDisplay;
    const failure = new Promise((_, reject) => { rejectDisplay = reject; });
    failure.catch(() => {});
    f.deferDisplay(failure);
    f.initialize(); f.takeFrame()();
    f.scrollTo(1600); await f.takeFrame()();
    f.deferDisplay(null);
    f.scrollTo(1300); await f.takeFrame()();
    f.scrollTo(1600); await f.takeFrame()();
    rejectDisplay(new Error('测试：同月旧请求失败'));
    await Promise.resolve();
    f.scrollTo(1610); await f.takeFrame()();
    assert.equal(f.calls.filter(c => c[0] === 'SetDisplayedMonth').length, 3);
});

test('不同高度月份向后切换，保留锚点而非假设每月等高', async () => {
    const f = setup();
    f.setMonths(['2026-08', '2026-09', '2026-10', '2026-11', '2026-12'], [480, 600, 720, 600, 480]);
    let resolveChange;
    f.deferChange(new Promise(resolve => { resolveChange = resolve; }));
    f.initialize(); f.takeFrame()();
    assert.equal(f.stream.scrollTop, 1080);
    f.scrollTo(1800);
    const loading = f.takeFrame()();
    f.scrollTo(1840);
    f.setMonths(['2026-09', '2026-10', '2026-11', '2026-12', '2027-01'], [600, 720, 600, 480, 600]);
    f.mutate(); resolveChange(); await loading;
    assert.equal(f.stream.scrollTop, 1360);
    assert.equal(1320 - f.stream.scrollTop, 1800 - 1840);
});

test('不同高度月份向前切换，按新增月份真实高度补偿', async () => {
    const f = setup();
    f.setMonths(['2026-08', '2026-09', '2026-10', '2026-11', '2026-12'], [480, 600, 720, 600, 480]);
    let resolveChange;
    f.deferChange(new Promise(resolve => { resolveChange = resolve; }));
    f.initialize(); f.takeFrame()();
    f.scrollTo(460);
    const loading = f.takeFrame()();
    f.setMonths(['2026-07', '2026-08', '2026-09', '2026-10', '2026-11'], [720, 480, 600, 720, 600]);
    f.mutate(); resolveChange(); await loading;
    assert.equal(f.stream.scrollTop, 1180);
    assert.equal(1200 - f.stream.scrollTop, 480 - 460);
    assert.equal(f.calls.find(c => c[0] === 'ChangeVisibleMonth')[1], -1);
});

test('跨年月份报告保留正确年份，窗口补偿不重复换月', async () => {
    const f = setup();
    f.setMonths(['2026-10', '2026-11', '2026-12', '2027-01', '2027-02']);
    let resolveChange;
    f.deferChange(new Promise(resolve => { resolveChange = resolve; }));
    f.initialize(); f.takeFrame()();
    f.scrollTo(1900);
    const loading = f.takeFrame()();
    f.setMonths(['2026-11', '2026-12', '2027-01', '2027-02', '2027-03']);
    f.mutate(); resolveChange(); await loading;
    await f.takeFrame()();
    assert.equal(f.stream.scrollTop, 1300);
    assert.deepEqual(f.calls.find(c => c[0] === 'SetDisplayedMonth'), ['SetDisplayedMonth', 2027, 1]);
    assert.equal(f.calls.filter(c => c[0] === 'ChangeVisibleMonth').length, 1);
});

test('等待月份加载时反向滚动，补偿保留反向后的最新位置', async () => {
    const f = setup();
    let resolveChange;
    f.deferChange(new Promise(resolve => { resolveChange = resolve; }));
    f.initialize(); f.takeFrame()();
    f.scrollTo(1800);
    const loading = f.takeFrame()();
    f.scrollTo(1500);
    f.setMonths(['2026-09', '2026-10', '2026-11', '2026-12', '2027-01']);
    f.mutate(); resolveChange(); await loading;
    assert.equal(f.stream.scrollTop, 900);
    assert.equal(1200 - f.stream.scrollTop, 1800 - 1500);
});
