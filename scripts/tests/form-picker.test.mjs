import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
const base = new URL('../../src/MoiCalendar.App/wwwroot/', import.meta.url);
function fixture() {
    const handlers = new Map(); let observer;
    const document = { activeElement: null, addEventListener(name, fn) { handlers.set(name, fn); }, removeEventListener(name) { handlers.delete(name); } };
    class Node {
        constructor(tag) { this.tagName = tag; this.children = []; this.attrs = new Map(); this.listeners = new Map(); this.style = {}; this.dataset = {}; this.className = ''; this.tabIndex = 0; this.isConnected = true; this.value = ''; this.min = ''; this.max = ''; this.offsetWidth = 304; this.offsetHeight = 340; this.events = []; this.classList = { contains: key => this.className.split(' ').includes(key), toggle: (key, enabled) => { const classes = new Set(this.className.split(' ')); enabled ? classes.add(key) : classes.delete(key); this.className = [...classes].join(' '); } }; }
        setAttribute(key, value) { this.attrs.set(key, value); }
        getAttribute(key) { return this.attrs.get(key) ?? null; }
        removeAttribute(key) { this.attrs.delete(key); }
        append(...nodes) { for (const node of nodes) { node.parentElement = this; this.children.push(node); } }
        replaceChildren() { this.children = []; }
        remove() { this.isConnected = false; if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(node => node !== this); }
        addEventListener(key, fn) { this.listeners.set(key, fn); }
        focus() { document.activeElement = this; }
        showPopover() { this.shown = true; }
        scrollIntoView() {}
        getBoundingClientRect() { return { left: 100, top: 200, bottom: 240, width: 300 }; }
        closest() { return document.body; }
        contains(target) { return this === target || this.children.some(node => node.contains(target)); }
        matches(selector) {
            if (selector === ':disabled') return !!this.disabled || !!this.parentElement?.disabled;
            if (selector === 'input[data-date-time-picker]') return this.tagName === 'input' && this.marked;
            if (selector.includes('select')) return this.tagName === 'select';
            if (selector.includes(',')) return selector.split(',').some(part => this.matches(part.trim()));
            if (selector.includes(':not([disabled])') && this.disabled) return false;
            const cls = /\.([\w-]+)/.exec(selector); if (cls && !this.className.split(' ').includes(cls[1])) return false;
            const attr = /\[([\w-]+)="([^"]*)"\]/.exec(selector); if (attr) { const value = attr[1] === 'tabindex' ? String(this.tabIndex) : attr[1] === 'data-date' ? this.dataset.date : this.getAttribute(attr[1]); if (value !== attr[2]) return false; }
            const tag = /^(input|button)/.exec(selector); return !tag || this.tagName === tag[1];
        }
        querySelectorAll(selector) { return this.children.flatMap(node => [...(node.matches(selector) ? [node] : []), ...node.querySelectorAll(selector)]); }
        querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
        dispatchEvent(event) { this.events.push(event.type); this.listeners.get(event.type)?.(event); }
        click() { if (!this.disabled) this.listeners.get('click')?.({ target: this, stopPropagation() {} }); }
    }
    document.createElement = tag => new Node(tag); document.createElementNS = (_, tag) => new Node(tag); document.body = new Node('body');
    const window = { innerWidth: 1000, innerHeight: 800, addEventListener() {}, removeEventListener() {} };
    const context = vm.createContext({ window, document, Date, Event: class { constructor(type) { this.type = type; } }, requestAnimationFrame: () => 1, cancelAnimationFrame() {}, MutationObserver: class { constructor(fn) { observer = fn; } observe() {} disconnect() {} } });
    for (const file of ['formPickerModel.js', 'formPicker.js']) vm.runInContext(readFileSync(new URL(file, base), 'utf8'), context);
    const input = (type = 'datetime-local') => { const node = new Node('input'); node.marked = true; node.type = type; node.value = type === 'time' ? '08:34' : type === 'date' ? '2026-10-06' : '2026-10-06T08:34'; node.labels = [{ textContent: '开始日期/时间' }]; document.body.append(node); return node; };
    const fire = (name, target, values = {}) => { const event = { target, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...values }; handlers.get(name)?.(event); return event; };
    return { input, document, window, fire, Node, handlers, observer: () => observer(), popup: () => document.body.children.find(node => node.className.includes('form-picker')) };
}
test('日期时间草稿一分钟精度，确认前不更新源字段', () => {
    const f = fixture(), input = f.input(); f.window.moicalendarFormPicker.open(input);
    const minute = f.popup().querySelectorAll('.form-picker-time-number')[1]; minute.value = '35'; minute.dispatchEvent({ type: 'input' });
    assert.equal(input.value, '2026-10-06T08:34');
    f.popup().querySelector('.form-picker-apply').click(); assert.equal(input.value, '2026-10-06T08:35'); assert.deepEqual(input.events, ['input', 'change']); assert.equal(f.popup(), undefined);
});
test('Escape 取消未确认草稿且不关闭父编辑器，恢复字段焦点', () => {
    const f = fixture(), input = f.input('time'); f.window.moicalendarFormPicker.open(input);
    const e = f.fire('keydown', f.document.activeElement, { key: 'Escape' });
    assert.equal(e.stopped, true); assert.equal(f.popup(), undefined); assert.equal(input.value, '08:34'); assert.equal(f.document.activeElement, input);
});
test('越过最小日期的按钮禁用，确认时间越界时不可提交', () => {
    const f = fixture(), input = f.input(); input.min = '2026-10-06T09:00'; f.window.moicalendarFormPicker.open(input);
    assert.equal(f.popup().querySelector('[data-date="2026-10-05"]').disabled, true);
    assert.equal(f.popup().querySelector('.form-picker-apply').disabled, true); f.popup().querySelector('.form-picker-apply').click(); assert.deepEqual(input.events, []);
});
test('外部点击撤销，移除字段或禁用祖先会清理弹层', () => {
    for (const mode of ['outside', 'removed', 'disabled']) {
        const f = fixture(), input = f.input(); f.window.moicalendarFormPicker.open(input);
        if (mode === 'outside') f.fire('pointerdown', new f.Node('div'));
        else { if (mode === 'removed') input.isConnected = false; else input.disabled = true; f.observer(); }
        assert.equal(f.popup(), undefined); assert.deepEqual(input.events, []);
    }
});
test('只读或禁用字段不会打开，多个字段只有一个弹层', () => {
    const f = fixture(), input = f.input(); input.readOnly = true; f.window.moicalendarFormPicker.open(input); assert.equal(f.popup(), undefined);
    input.readOnly = false; f.window.moicalendarFormPicker.open(input); const other = f.input('date'); f.window.moicalendarFormPicker.open(other);
    assert.equal(f.document.body.children.filter(node => node.className.includes('form-picker')).length, 1); assert.equal(input.getAttribute('aria-expanded'), null);
});
test('下拉键盘跳过禁用选项，Enter 一次更新既有 change 绑定', () => {
    const f = fixture(), select = new f.Node('select'); select.value = 'never'; select.options = ['never', 'disabled', 'date'].map(value => ({ value, textContent: value, disabled: value === 'disabled' })); f.document.body.append(select);
    f.fire('keydown', select, { key: 'ArrowDown' }); assert.equal(f.popup().getAttribute('role'), 'listbox');
    f.fire('keydown', f.document.activeElement, { key: 'ArrowDown' }); f.fire('keydown', f.document.activeElement, { key: 'Enter' });
    assert.equal(select.value, 'date'); assert.deepEqual(select.events, ['input', 'change']); assert.equal(f.popup(), undefined);
});
test('直接输入时间后 Enter 只确认控件，不提交父表单', () => {
    const f = fixture(), input = f.input('time'); f.window.moicalendarFormPicker.open(input); const number = f.popup().querySelector('.form-picker-time-number');
    number.value = '9'; number.dispatchEvent({ type: 'input' }); const e = f.fire('keydown', number, { key: 'Enter' });
    assert.equal(e.prevented, true); assert.equal(e.stopped, true); assert.equal(input.value, '09:34');
});
test('组件脚本销毁移除监听、属性和弹层', () => {
    const f = fixture(), input = f.input(); f.window.moicalendarFormPicker.open(input); f.window.moicalendarFormPicker.dispose();
    assert.equal(f.popup(), undefined); assert.equal(f.handlers.size, 0); assert.equal(input.getAttribute('aria-controls'), null);
});
test('输入无效分钟不能确认，修正后恢复；无效输入不污染草稿', () => {
    const f = fixture(), input = f.input('time'); f.window.moicalendarFormPicker.open(input);
    const number = f.popup().querySelectorAll('.form-picker-time-number')[1], apply = f.popup().querySelector('.form-picker-apply');
    for (const value of ['60', '-1', '1.5', '']) { number.value = value; number.dispatchEvent({ type: 'input' }); assert.equal(apply.disabled, true); }
    assert.equal(input.value, '08:34'); number.value = '59'; number.dispatchEvent({ type: 'input' }); assert.equal(apply.disabled, false); apply.click(); assert.equal(input.value, '08:59');
});
