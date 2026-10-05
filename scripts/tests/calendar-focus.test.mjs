import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/calendarUi.js', import.meta.url), 'utf8');
test('月份轨道保留原生 Enter 和方向键，不触发日历全局快捷键', () => {
    const window = {};
    const document = { addEventListener() {}, removeEventListener() {} };
    vm.runInNewContext(source, { window, document });
    let notifications = 0;
    window.moicalendarUi.initializeCalendarShortcuts({ invokeMethodAsync() { notifications++; } });
    for (const key of ['Enter', 'ArrowLeft', 'ArrowRight']) {
        let prevented = false;
        window.moicalendarUi.shortcutHandler({ key, target: { tagName: 'SPAN', closest: () => ({}) },
            preventDefault() { prevented = true; } });
        assert.equal(prevented, false);
    }
    assert.equal(notifications, 0);
    window.moicalendarUi.shortcutHandler({ key: 'Enter', target: { tagName: 'DIV', closest: () => null },
        preventDefault() {} });
    assert.equal(notifications, 1);
});

function fixture() {
    const document = { activeElement: null, getElementById: () => main };
    const node = (options = {}) => ({
        isConnected: true, getClientRects: () => [1],
        focus(args) { document.activeElement = this; this.preventScroll = args.preventScroll; },
        ...options
    });
    const main = node();
    const window = {};
    vm.runInNewContext(source, { window, document, getComputedStyle: element => ({ visibility: element.visibility ?? 'visible' }) });
    return { ui: window.moicalendarUi, document, node, main };
}

test('有效入口恢复焦点且不跳动滚动位置', () => {
    const f = fixture();
    const trigger = f.node();
    f.document.activeElement = trigger;
    f.ui.rememberCalendarFocus();
    f.document.activeElement = null;
    f.ui.restoreCalendarFocus();
    assert.equal(f.document.activeElement, trigger);
    assert.equal(trigger.preventScroll, true);
    assert.equal(f.ui.rememberedFocus.length, 0);
});

test('嵌套流程跳过已被移除的按钮，返回原日历入口', () => {
    const f = fixture();
    const trigger = f.node();
    f.ui.rememberedFocus.push(trigger, f.node({ isConnected: false }));
    f.ui.restoreCalendarFocus();
    assert.equal(f.document.activeElement, trigger);
    assert.equal(f.ui.rememberedFocus.length, 0);
});

test('跳过隐藏、不可见和禁用的密度变体按钮', () => {
    const f = fixture();
    const trigger = f.node();
    f.ui.rememberedFocus.push(trigger, f.node({ visibility: 'hidden' }),
        f.node({ getClientRects: () => [] }), f.node({ disabled: true }));
    f.ui.restoreCalendarFocus();
    assert.equal(f.document.activeElement, trigger);
});

test('入口全部失效时回到日历主区域，包括空栈', () => {
    const f = fixture();
    f.ui.rememberedFocus.push(null, f.node({ isConnected: false }));
    f.ui.restoreCalendarFocus();
    assert.equal(f.document.activeElement, f.main);
    assert.equal(f.main.preventScroll, true);
    f.document.activeElement = null;
    f.ui.restoreCalendarFocus();
    assert.equal(f.document.activeElement, f.main);
});

test('主区域不存在时安全退出', () => {
    const f = fixture();
    f.document.getElementById = () => null;
    assert.doesNotThrow(() => f.ui.restoreCalendarFocus());
});
