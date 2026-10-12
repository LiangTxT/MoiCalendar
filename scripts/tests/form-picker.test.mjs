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
        closest(selector) { if (selector.includes('calendar-overlay')) return document.body; if (this.matches(selector)) return this; return this.parentElement?.closest(selector) ?? null; }
        contains(target) { return this === target || this.children.some(node => node.contains(target)); }
        matches(selector) {
            if (selector === '[hidden]') return !!this.hidden;
            if (selector === 'button[data-select-target]') return this.tagName === 'button' && !!this.dataset.selectTarget;
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
    document.getElementById = id => document.body.querySelectorAll('*').find(node => node.id === id);
    const window = { innerWidth: 1000, innerHeight: 800, addEventListener() {}, removeEventListener() {} };
    const context = vm.createContext({ window, document, Date, Event: class { constructor(type) { this.type = type; } }, requestAnimationFrame: () => 1, cancelAnimationFrame() {}, MutationObserver: class { constructor(fn) { observer = fn; } observe() {} disconnect() {} } });
    for (const file of ['formPickerModel.js', 'formPicker.js']) vm.runInContext(readFileSync(new URL(file, base), 'utf8'), context);
    const input = (type = 'datetime-local') => { const node = new Node('input'); node.marked = true; node.type = type; node.value = type === 'time' ? '08:34' : type === 'date' ? '2026-10-06' : '2026-10-06T08:34'; node.labels = [{ textContent: '开始日期/时间' }]; document.body.append(node); return node; };
    const fire = (name, target, values = {}) => { const event = { target, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...values }; handlers.get(name)?.(event); return event; };
    const trigger = (input, kind = 'select') => { input.id = `value-${document.body.children.length}`; const node = new Node('button'); node.dataset[kind === 'select' ? 'selectTarget' : 'temporalTarget'] = input.id; input.hidden = true; document.body.append(node); return node; };
    return { input, document, window, fire, Node, handlers, trigger, observer: () => observer(), popup: () => document.body.children.find(node => node.className.split(' ').includes('form-picker')) };
}
test('日期时间草稿一分钟精度，确认前不更新源字段', () => {
    const f = fixture(), input = f.input(); f.window.moicalendarFormPicker.open(input);
    f.popup().querySelectorAll('.form-picker-time-option')[24 + 35].click();
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
    const trigger = f.trigger(select); f.fire('keydown', trigger, { key: 'ArrowDown' }); assert.equal(f.popup().getAttribute('role'), 'listbox');
    f.fire('keydown', f.document.activeElement, { key: 'ArrowDown' }); f.fire('keydown', f.document.activeElement, { key: 'Enter' });
    assert.equal(select.value, 'date'); assert.deepEqual(select.events, ['input', 'change']); assert.equal(f.popup(), undefined);
});
test('直接输入时间后 Enter 只确认控件，不提交父表单', () => {
    const f = fixture(), input = f.input('time'); f.window.moicalendarFormPicker.open(input); f.popup().querySelector('.form-picker-manual-toggle').click(); const number = f.popup().querySelector('.form-picker-manual-input');
    number.value = '09:34'; number.dispatchEvent({ type: 'input' }); const e = f.fire('keydown', number, { key: 'Enter' });
    assert.equal(e.prevented, true); assert.equal(e.stopped, true); assert.equal(input.value, '09:34');
});
test('组件脚本销毁移除监听、属性和弹层', () => {
    const f = fixture(), input = f.input(); f.window.moicalendarFormPicker.open(input); f.window.moicalendarFormPicker.dispose();
    assert.equal(f.popup(), undefined); assert.equal(f.handlers.size, 0); assert.equal(input.getAttribute('aria-controls'), null);
});
test('输入无效分钟不能确认，修正后恢复；无效输入不污染草稿', () => {
    const f = fixture(), input = f.input('time'); f.window.moicalendarFormPicker.open(input);
    f.popup().querySelector('.form-picker-manual-toggle').click();
    const number = f.popup().querySelector('.form-picker-manual-input'), apply = f.popup().querySelector('.form-picker-apply');
    for (const value of ['08:60', '-1:34', '08:1.5', '', '24:00', '8:34']) { number.value = value; number.dispatchEvent({ type: 'input' }); assert.equal(apply.disabled, true); }
    assert.equal(input.value, '08:34'); number.value = '08:59'; number.dispatchEvent({ type: 'input' }); assert.equal(apply.disabled, false); apply.click(); assert.equal(input.value, '08:59');
});

test('纯时间弹层使用紧凑宽度，打开不聚焦原生字段或键盘输入', () => {
    const f = fixture(), input = f.input('time'), trigger = f.trigger(input, 'time');
    f.window.moicalendarFormPicker.open(input, trigger);
    assert.equal(f.document.activeElement, f.popup().querySelector('.form-picker-apply'));
    assert.equal(f.popup().style.width, '244px');
    assert.equal(f.popup().dataset.mode, 'time');
    assert.equal(input.events.length, 0);
});

test('只有一个日历选项时下拉仍打开，选择当前项不产生变更', () => {
    const f = fixture(), select = new f.Node('select'); select.value = 'local'; select.options = [{ value: 'local', textContent: '本地日历' }]; f.document.body.append(select);
    const trigger = f.trigger(select); f.fire('pointerdown', trigger); f.fire('click', trigger);
    assert.equal(f.popup().getAttribute('role'), 'listbox');
    f.popup().children[0].click(); assert.equal(f.popup(), undefined); assert.deepEqual(select.events, []);
    const component = readFileSync(new URL('../../src/MoiCalendar.App/Components/EventQuickCreate.razor', import.meta.url), 'utf8');
    assert.match(component, /<select id="quick-event-calendar">/);
});

test('纯时间两列占满可用宽度，不留右侧空白且保持分钟输入宽度一致', () => {
    const css = readFileSync(new URL('css/v4.css', base), 'utf8');
    assert.ok(css.includes('.form-picker[data-mode="time"] .form-picker-time { width: 100%; }'));
    assert.ok(css.includes('.form-picker[data-mode="time"] .form-picker-time-number { width: 100%; }'));
});

test('24 小时文本字段显示 23 点并可确认 00:22，不转换为 12 小时制', () => {
    const f = fixture(), input = f.input('time'); input.type = 'text'; input.dataset.temporalType = 'time'; input.value = '23:52';
    f.window.moicalendarFormPicker.open(input);
    assert.equal(f.popup().dataset.mode, 'time');
    assert.equal(f.popup().querySelector('.form-picker-summary').textContent, '23:52');
    assert.equal(f.popup().querySelectorAll('.form-picker-time-number')[0].textContent, '23');
    assert.equal(f.popup().querySelectorAll('.form-picker-time-option').length, 84);
    for (const index of [0, 24 + 22]) f.popup().querySelectorAll('.form-picker-time-option')[index].click();
    assert.equal(input.value, '23:52');
    f.popup().querySelector('.form-picker-apply').click(); assert.equal(input.value, '00:22');
    assert.deepEqual(input.events, ['input','change']);
});

test('24 小时文本字段取消草稿不改变时间，移除类型标记关闭弹层', () => {
    const f = fixture(), input = f.input('time'); input.type = 'text'; input.dataset.temporalType = 'time'; input.value = '23:52';
    f.window.moicalendarFormPicker.open(input);
    f.fire('keydown', f.document.activeElement, {key:'Escape'}); assert.equal(input.value, '23:52');
    f.window.moicalendarFormPicker.open(input); delete input.dataset.temporalType; f.observer();
    assert.equal(f.popup(), undefined); assert.equal(input.value, '23:52');
});

test('隐藏时间字段由按钮定位，确定与取消只恢复按钮焦点', () => {
    for (const action of ['apply', 'cancel']) {
        const f = fixture(), input = f.input('time'); input.type = 'hidden'; input.dataset.temporalType = 'time';
        const trigger = f.trigger(input, 'time'); f.window.moicalendarFormPicker.open(input, trigger);
        assert.equal(trigger.getAttribute('aria-expanded'), 'true');
        assert.equal(f.document.activeElement, f.popup().querySelector('.form-picker-apply'));
        if (action === 'apply') { f.popup().querySelectorAll('.form-picker-time-option')[9].click(); f.popup().querySelector('.form-picker-apply').click(); }
        else f.fire('keydown', f.document.activeElement, { key: 'Escape' });
        assert.equal(f.document.activeElement, trigger); assert.equal(f.popup(), undefined);
        assert.equal(input.value, action === 'apply' ? '09:34' : '08:34');
    }
});
test('提醒触摸 pointerdown 不打开原生控件，click 只打开一次且选完不重开', () => {
    const f = fixture(), select = new f.Node('select'); select.value = '-1'; select.options = [{ value: '-1', textContent: '不提醒' }, { value: '0', textContent: '开始时' }]; f.document.body.append(select);
    const trigger = f.trigger(select); f.fire('pointerdown', trigger); assert.equal(f.popup(), undefined);
    f.fire('click', trigger); const popup = f.popup(); f.fire('click', trigger); assert.equal(f.popup(), popup);
    popup.children[1].click(); assert.equal(select.value, '0'); assert.equal(f.document.activeElement, trigger);
    assert.equal(f.popup(), undefined); assert.deepEqual(select.events, ['input', 'change']);
});
test('手动日期输入校验合法日期与范围，取消不改变原值', () => {
    const f = fixture(), input = f.input('date'); input.min = '2026-10-01'; f.window.moicalendarFormPicker.open(input);
    const manual = f.popup().querySelector('.form-picker-manual-input'); assert.equal(f.popup().querySelector('.form-picker-manual').hidden, true);
    f.popup().querySelector('.form-picker-manual-toggle').click(); assert.equal(f.document.activeElement, manual);
    for (const value of ['2026-02-30', '2026-09-30', '']) { manual.value = value; manual.dispatchEvent({type:'input'}); assert.equal(f.popup().querySelector('.form-picker-apply').disabled, true); }
    manual.value = '2026-10-08'; manual.dispatchEvent({type:'input'}); assert.equal(f.popup().querySelector('.form-picker-apply').disabled, false);
    f.fire('keydown', manual, {key:'Escape'}); assert.equal(input.value, '2026-10-06');
});
test('非目标原生下拉不被劫持，保留单一系统选择器', () => {
    const f = fixture(), select = new f.Node('select'); f.document.body.append(select);
    f.fire('pointerdown', select); f.fire('click', select); f.fire('keydown', select, {key:'ArrowDown'});
    assert.equal(f.popup(), undefined);
});

test('手动输入后改用日期或时间选项，显示值与确认值保持一致', () => {
    for (const type of ['time', 'date']) {
        const f = fixture(), input = f.input(type); f.window.moicalendarFormPicker.open(input);
        f.popup().querySelector('.form-picker-manual-toggle').click();
        const manual = f.popup().querySelector('.form-picker-manual-input');
        manual.value = 'invalid'; manual.dispatchEvent({ type: 'input' });
        assert.equal(f.popup().querySelector('.form-picker-apply').disabled, true);
        if (type === 'time') f.popup().querySelectorAll('.form-picker-time-option')[9].click();
        else f.popup().querySelector('[data-date="2026-10-08"]').click();
        const expected = type === 'time' ? '09:34' : '2026-10-08';
        assert.equal(manual.value, expected); assert.equal(manual.getAttribute('aria-invalid'), 'false');
        f.popup().querySelector('.form-picker-apply').click(); assert.equal(input.value, expected);
    }
});
