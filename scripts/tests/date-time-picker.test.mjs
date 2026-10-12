import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/dateTimePicker.js', import.meta.url), 'utf8');
function fixture() {
    const handlers = new Map(), calls = [];
    const input = { type: 'hidden', value: '23:52' };
    const trigger = { dataset: { temporalTarget: 'start-value' }, matches: () => false };
    trigger.closest = selector => selector === 'button[data-temporal-target]' ? trigger : null;
    const document = { getElementById: id => id === 'start-value' ? input : null,
        addEventListener: (name, fn) => handlers.set(name, fn), removeEventListener: name => handlers.delete(name) };
    const window = { moicalendarFormPicker: { open: (...args) => calls.push(args) } };
    const context = vm.createContext({ window, document });
    vm.runInContext(source, context);
    const fire = (name, target = trigger, extra = {}) => {
        const event = { target, preventDefault() { this.prevented = true; }, ...extra };
        handlers.get(name)?.(event); return event;
    };
    return { handlers, calls, input, trigger, document, window, context, fire };
}
test('按钮一次点击只打开网页弹层，传值字段不获得焦点', () => {
    const f = fixture(); const event = f.fire('click');
    assert.equal(event.prevented, true); assert.equal(f.calls.length, 1);
    assert.equal(f.calls[0][0], f.input); assert.equal(f.calls[0][1], f.trigger); assert.equal(f.input.value, '23:52');
});
test('触摸与键盘生成的 click 都只走同一入口', () => {
    for (const options of [{ pointerType: 'touch', detail: 1 }, { detail: 0 }]) {
        const f = fixture(); f.fire('click', f.trigger, options); assert.equal(f.calls.length, 1);
    }
});
test('不再通过双击切换到原生输入', () => {
    const f = fixture(); f.fire('click', f.trigger, { detail: 2 }); f.fire('dblclick');
    assert.equal(f.calls.length, 1); assert.equal(f.handlers.has('dblclick'), false);
});
test('Alt+下打开，普通输入按键不劫持', () => {
    const f = fixture(); f.fire('keydown', f.trigger, { key: 'ArrowDown', altKey: true });
    assert.equal(f.calls.length, 1);
    const event = f.fire('keydown', f.trigger, { key: '2' }); assert.equal(event.prevented, undefined);
});
test('禁用、移除字段或弹层脚本缺失时不打开', () => {
    for (const mode of ['disabled', 'removed', 'missing']) {
        const f = fixture();
        if (mode === 'disabled') f.trigger.matches = () => true;
        if (mode === 'removed') f.document.getElementById = () => null;
        if (mode === 'missing') delete f.window.moicalendarFormPicker;
        f.fire('click'); assert.equal(f.calls.length, 0);
    }
});
test('原生输入框不被增强脚本聚焦或再次打开', () => {
    const f = fixture(); f.fire('click', { closest: () => null }); assert.equal(f.calls.length, 0);
    assert.doesNotMatch(source, /\.showPicker\(|\.focus\(/);
});
test('点击按钮内部内容也能解析入口', () => {
    const f = fixture(); f.fire('click', { closest: () => f.trigger }); assert.equal(f.calls.length, 1);
});
test('重复加载和卸载清理监听', () => {
    const f = fixture(); vm.runInContext(source, f.context);
    assert.equal(f.handlers.size, 2); f.window.moicalendarDateTimePicker.dispose(); assert.equal(f.handlers.size, 0);
});
