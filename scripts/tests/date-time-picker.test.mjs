import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/dateTimePicker.js', import.meta.url), 'utf8');
function fixture(supported = true) {
    const handlers = new Map(), timers = new Map(), lifecycle = new Map();
    const rootClasses = new Set(); let id = 0;
    const document = { activeElement: null, documentElement: { classList: {
        toggle(key, value) { value ? rootClasses.add(key) : rootClasses.delete(key); }, remove(key) { rootClasses.delete(key); }
    } }, addEventListener(name, handler) { handlers.set(name, handler); }, removeEventListener(name) { handlers.delete(name); } };
    function HTMLInputElement() {}
    if (supported) HTMLInputElement.prototype.showPicker = () => {};
    const window = { addEventListener(name, handler) { lifecycle.set(name, handler); }, removeEventListener(name) { lifecycle.delete(name); } };
    const context = vm.createContext({ window, document, HTMLInputElement,
        setTimeout(fn, delay) { timers.set(++id, { fn, delay }); return id; }, clearTimeout(key) { timers.delete(key); } });
    vm.runInContext(source, context);
    const input = (type = 'date') => {
        const classes = new Set();
        return { type, value: '2026-10-06', isConnected: true, disabled: false, readOnly: false, calls: 0,
            matches: () => true, focus() { document.activeElement = this; }, showPicker() { this.calls++; },
            classList: { contains: key => classes.has(key), add(key) { classes.add(key); } } };
    };
    const fire = (name, target, options = {}) => {
        const event = { target, detail: 1, prevented: false, preventDefault() { this.prevented = true; }, ...options };
        handlers.get(name)(event); return event;
    };
    return { input, fire, timers, document, window, context, handlers, lifecycle, rootClasses,
        run() { const work = [...timers.values()]; timers.clear(); work.forEach(t => t.fn()); } };
}
for (const type of ['date', 'time', 'datetime-local']) test(`单击 ${type} 整框打开原生选择器，不修改值`, () => {
    const f = fixture(), e = f.input(type); f.fire('click', e);
    assert.equal(e.calls, 1); assert.equal(f.timers.size, 0);
    f.run(); assert.equal(e.calls, 1); assert.equal(e.value, '2026-10-06');
});
test('双击进入手动输入，后续分段点击保留键盘输入直到离焦', () => {
    const f = fixture(), e = f.input(); f.fire('click', e); f.fire('click', e, { detail: 2 }); f.fire('dblclick', e);
    f.run(); assert.equal(e.calls, 1); assert.equal(f.document.activeElement, e);
    f.fire('click', e); assert.equal(f.timers.size, 0);
    f.fire('focusout', e); f.fire('click', e); f.run(); assert.equal(e.calls, 2);
});
test('键盘编辑进入手动模式，Alt+下可立即打开', () => {
    const f = fixture(), e = f.input(); f.fire('click', e); f.fire('keydown', e, { key: '2' });
    f.run(); assert.equal(e.calls, 1);
    f.fire('keydown', e, { key: 'ArrowDown', altKey: true }); assert.equal(e.calls, 2);
});
test('触屏点击同步打开，避免延迟失去用户激活', () => {
    const f = fixture(), e = f.input(); f.fire('click', e, { pointerType: 'touch' });
    assert.equal(e.calls, 1); assert.equal(f.timers.size, 0);
});
test('移除、禁用或类型变化的字段不会打开选择器', () => {
    for (const invalidate of [
        (f, e) => { e.isConnected = false; }, (f, e) => { e.disabled = true; }, (f, e) => { e.type = 'text'; }
    ]) { const f = fixture(), e = f.input(); invalidate(f, e); f.fire('click', e); f.run(); assert.equal(e.calls, 0); }
});
test('不支持/只读/禁用字段保持浏览器默认行为', () => {
    for (const mode of ['unsupported', 'readonly', 'disabled', 'unmarked']) {
        const f = fixture(mode !== 'unsupported'), e = f.input();
        if (mode === 'readonly') e.readOnly = true;
        if (mode === 'disabled') e.disabled = true;
        if (mode === 'unmarked') e.matches = () => false;
        assert.equal(f.fire('click', e).prevented, false); assert.equal(f.timers.size, 0);
    }
});
test('API异常恢复图标原生入口，不抛出未处理异常', () => {
    const f = fixture(), e = f.input(); e.showPicker = () => { throw new Error('activation'); };
    f.fire('click', e); f.run(); assert.equal(e.classList.contains('picker-native-fallback'), true);
    assert.equal(f.fire('click', e).prevented, false);
});
test('没有延迟回调，重复加载、卸载清理监听', () => {
    const f = fixture(), e = f.input(); f.fire('click', e); assert.equal(f.timers.size, 0);
    f.fire('click', e); vm.runInContext(source, f.context); assert.equal(f.timers.size, 0);
    assert.equal(f.handlers.size, 4); f.window.moicalendarDateTimePicker.dispose(); assert.equal(f.handlers.size, 0);
});

test('主题弹层单击同步打开且保持源字段焦点，双击撤销未确认草稿', () => {
    const f = fixture(), e = f.input('time'); let opened = 0, closed = 0;
    f.window.moicalendarFormPicker = { open(input, preserveFocus) { assert.equal(input, e); assert.equal(preserveFocus, true); opened++; }, close(restore) { assert.equal(restore, false); closed++; } };
    f.fire('click', e); assert.equal(opened, 1); assert.equal(f.timers.size, 0); assert.equal(f.document.activeElement, e);
    f.fire('click', e, { detail: 2 }); assert.equal(closed, 1); assert.equal(e.value, '2026-10-06');
});
