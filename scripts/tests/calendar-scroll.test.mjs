import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarUi.js', import.meta.url), 'utf8');
function setup() {
    const window = {};
    vm.runInNewContext(source, { window });
    const listeners = () => ({
        handlers: new Set(),
        addEventListener(type, handler) { this.handlers.add(handler); },
        removeEventListener(type, handler) { this.handlers.delete(handler); },
        scroll() { for (const handler of this.handlers) handler(); }
    });
    const headings = Array.from({ length: 7 }, (_, index) => ({
        getBoundingClientRect: () => ({ left: 52 + index * 100, width: 100 })
    }));
    const properties = {};
    const frame = { style: { setProperty(name, value) { properties[name] = value; } }, querySelectorAll: () => headings };
    const horizontal = {
        ...listeners(), scrollLeft: 0, scrollWidth: 752, clientWidth: 375,
        getBoundingClientRect: () => ({ left: 0 })
    };
    const timeline = {
        ...listeners(), scrollTop: 0, scrollHeight: 1440, clientHeight: 480,
        offsetWidth: 752, clientWidth: 740,
        closest: selector => selector === '.week-grid-frame' ? frame : horizontal
    };
    const ui = window.moicalendarUi;
    const initialize = (key = 'week:0-24', selectedDayIndex = 4) =>
        ui.initializeCalendarScroll(timeline, key, 540, 0, 1440, selectedDayIndex);
    return { ui, timeline, horizontal, initialize, properties };
}

test('窄屏首次进入定位选中日期和初始时刻', () => {
    const { timeline, horizontal, initialize } = setup();
    initialize();
    assert.equal(timeline.scrollTop, 420);
    assert.equal(horizontal.scrollLeft, 314.5);
});

test('周 → 日 → 周恢复两轴位置，不按选中日期再次吸附', () => {
    const { ui, timeline, horizontal, initialize } = setup();
    initialize();
    timeline.scrollTop = 780;
    horizontal.scrollLeft = 112;
    horizontal.scroll();
    ui.disposeCalendarScroll(timeline);
    initialize('day:0-24', 0);
    timeline.scrollTop = 300;
    timeline.scroll();
    ui.disposeCalendarScroll(timeline);
    initialize('week:0-24', 6);
    assert.equal(timeline.scrollTop, 780);
    assert.equal(horizontal.scrollLeft, 112);
});

test('卸载保存位置并移除监听器，重复初始化不增加监听器', () => {
    const { ui, timeline, horizontal, initialize } = setup();
    initialize();
    initialize();
    assert.equal(timeline.handlers.size, 1);
    assert.equal(horizontal.handlers.size, 1);
    timeline.scrollTop = 600;
    ui.disposeCalendarScroll(timeline);
    assert.equal(timeline.handlers.size, 0);
    assert.equal(horizontal.handlers.size, 0);
    assert.equal(ui.calendarScrollPositions['week:0-24'].top, 600);
});

test('宽屏不产生横向定位，时段上下文独立', () => {
    const { timeline, horizontal, initialize } = setup();
    horizontal.clientWidth = 900;
    initialize();
    assert.equal(horizontal.scrollLeft, 0);
    timeline.scrollTop = 900;
    timeline.scroll();
    initialize('week:8-18');
    assert.equal(timeline.scrollTop, 420);
});

test('缺少 DOM 或无效时段安全退出', () => {
    const { ui, timeline } = setup();
    ui.initializeCalendarScroll(null, 'week', 540, 0, 1440);
    ui.initializeCalendarScroll(timeline, 'week', 540, 600, 600);
    assert.equal(timeline.handlers.size, 0);
    ui.disposeCalendarScroll(null);
});

test('横向滚动同步时间标尺偏移，不改变事件时间', () => {
    const { horizontal, initialize, properties } = setup();
    initialize();
    assert.equal(properties['--time-grid-horizontal-offset'], '314.5px');
    horizontal.scrollLeft = 100;
    horizontal.scroll();
    assert.equal(properties['--time-grid-horizontal-offset'], '100px');
});

for (const gutter of [0, 8, 10]) {
    test(`原生滚动条宽度 ${gutter}px 时，表头使用实际测量值`, () => {
        const { timeline, initialize, properties } = setup();
        timeline.clientWidth = timeline.offsetWidth - gutter;
        initialize();
        assert.equal(properties['--time-grid-scrollbar-width'], `${gutter}px`);
    });
}
