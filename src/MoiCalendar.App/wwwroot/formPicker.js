/* 一个活动控件弹层。只产生 input/change 事件，值、业务校验和保存仍归 Blazor。 */
(() => {
    window.moicalendarFormPicker?.dispose();
    const model = window.moicalendarPickerModel;
    let active = null, sequence = 0, positionFrame = 0;
    const eligible = input => input?.matches?.('input[data-date-time-picker]') && ['date', 'time', 'datetime-local'].includes(input.type) && !input.matches(':disabled') && !input.readOnly;
    const eligibleSelect = input => input?.matches?.('.calendar-overlay select, .settings-shell select') && !input.matches(':disabled') && !input.multiple;
    const element = (tag, className, text) => { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; };
    const button = (text, label, action, className = '') => {
        const node = element('button', className, text); node.type = 'button'; if (label) node.setAttribute('aria-label', label);
        node.addEventListener('click', action); return node;
    };
    const icon = direction => {
        const node = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        node.setAttribute('viewBox', '0 0 24 24'); node.setAttribute('aria-hidden', 'true');
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', direction < 0 ? 'M15 6l-6 6 6 6' : 'M9 6l6 6-6 6'); node.append(path); return node;
    };
    const label = input => input.labels?.[0]?.textContent.trim() || input.getAttribute('aria-label') || '选择日期和时间';
    const close = (restore = true) => {
        if (!active) return;
        const { input, popup, expanded, controls } = active; active = null;
        popup.remove();
        if (expanded === null) input.removeAttribute('aria-expanded'); else input.setAttribute('aria-expanded', expanded);
        if (controls === null) input.removeAttribute('aria-controls'); else input.setAttribute('aria-controls', controls);
        if (restore && input.isConnected && !input.matches(':disabled')) input.focus({ preventScroll: true });
    };
    const place = () => {
        if (!active) return;
        if (!active.input.isConnected || active.input.matches(':disabled')) { close(false); return; }
        const { popup, input } = active;
        const viewport = window.visualViewport;
        const width = viewport?.width ?? window.innerWidth, height = viewport?.height ?? window.innerHeight;
        const rect = input.getBoundingClientRect();
        if (rect.bottom < 0 || rect.top > height) { close(false); return; }
        popup.style.width = `${Math.min(active.kind === 'select' ? Math.max(rect.width, 200) : active.hasDate && active.hasTime ? 460 : 304, width - 24)}px`;
        popup.style.maxHeight = `${height - 24}px`;
        const bounds = { width: popup.offsetWidth, height: popup.offsetHeight };
        const position = model.position(rect, bounds.width, bounds.height, { width, height });
        // 手机上日期时间组合纵向排布，保留足够操作空间，不缩小点击目标。
        const sheet = width < 520 && active.kind !== 'select';
        popup.dataset.side = sheet ? 'sheet' : position.side;
        popup.style.left = `${sheet ? 12 : position.left}px`;
        popup.style.top = `${sheet ? Math.max(12, height - bounds.height - 12) : position.top}px`;
        popup.style.maxHeight = `${sheet ? height - 24 : position.maxHeight}px`;
    };
    const positionSoon = () => { if (!positionFrame) positionFrame = requestAnimationFrame(() => { positionFrame = 0; place(); }); };
    const mount = (input, kind) => {
        close(false);
        const popup = element('div', `form-picker form-picker-${kind} app-scrollable`);
        popup.id = `form-picker-${++sequence}`; popup.setAttribute('popover', 'manual'); popup.setAttribute('role', kind === 'select' ? 'listbox' : 'dialog');
        popup.setAttribute('aria-label', label(input));
        // 留在原有编辑器的 DOM 内，使其焦点边界包含控件弹层；top layer 避免被滚动容器裁切。
        (input.closest('.calendar-overlay, .settings-shell') ?? document.body).append(popup);
        popup.addEventListener('click', event => event.stopPropagation());
        active = { input, popup, kind, expanded: input.getAttribute('aria-expanded'), controls: input.getAttribute('aria-controls') };
        input.setAttribute('aria-expanded', 'true'); input.setAttribute('aria-controls', popup.id);
        return active;
    };
    const reveal = () => { if (typeof active.popup.showPopover === 'function') active.popup.showPopover(); place(); };
    const emit = (input, value) => {
        if (!input.isConnected || input.matches(':disabled') || input.readOnly || input.value === value) return;
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };
    const selectOpen = input => {
        if (!eligibleSelect(input)) return;
        const state = mount(input, 'select');
        state.options = [...input.options].filter(option => !option.hidden);
        const selected = state.options.findIndex(option => option.value === input.value);
        state.index = Math.max(0, selected);
        const choose = index => { const option = state.options[index]; if (!option || option.disabled || !eligibleSelect(input)) return; close(); emit(input, option.value); };
        state.choose = choose;
        state.options.forEach((option, index) => {
            const item = button(option.textContent, null, () => choose(index), 'form-picker-option');
            item.id = `${state.popup.id}-${index}`; item.setAttribute('role', 'option'); item.setAttribute('aria-selected', String(option.value === input.value));
            item.disabled = option.disabled || option.parentElement?.disabled === true; item.tabIndex = -1;
            item.addEventListener('pointermove', () => { state.index = index; item.focus({ preventScroll: true }); });
            state.popup.append(item);
        });
        reveal(); state.popup.children[state.index]?.focus({ preventScroll: true });
    };
    const open = input => {
        if (!eligible(input)) return;
        if (active?.input === input) return;
        const state = mount(input, 'date');
        const now = new Date();
        const today = `${now.getFullYear().toString().padStart(4, '0')}-${model.pad(now.getMonth() + 1)}-${model.pad(now.getDate())}`;
        state.hasDate = input.type !== 'time'; state.hasTime = input.type !== 'date';
        state.date = model.parseDate(input.value.slice(0, 10)) ? input.value.slice(0, 10) : today;
        const time = input.type === 'time' ? input.value : input.value.split('T')[1];
        state.time = model.validTime(time) ? time : `${model.pad(now.getHours())}:${model.pad(now.getMinutes())}`;
        state.month = state.date; state.cursor = state.date; state.invalidTime = new Set();
        const draftValue = () => state.hasDate ? state.hasTime ? `${state.date}T${state.time}` : state.date : state.time;
        const valid = () => !state.invalidTime.size && model.validTime(state.time) && model.inBounds(draftValue(), input.min, input.max);
        const header = element('header', 'form-picker-heading', label(input)); state.popup.append(header);
        const body = element('div', 'form-picker-body'); state.popup.append(body);
        const summary = element('p', 'form-picker-summary'); summary.setAttribute('aria-live', 'polite');
        const apply = button('确定', null, () => { if (!valid() || !eligible(input)) return; const value = draftValue(); close(); emit(input, value); }, 'form-picker-apply');
        const update = () => { summary.textContent = state.invalidTime.size ? '小时为 0–23，分钟为 0–59，请输入整数。' : `${draftValue().replace('T', ' ')}${valid() ? '' : ' · 超出允许范围'}`; apply.disabled = !valid(); summary.classList.toggle('invalid', !valid()); };
        const dateAllowed = value => model.inBounds(value, input.min?.slice(0, 10), input.max?.slice(0, 10));
        if (state.hasDate) {
            const calendar = element('section', 'form-picker-calendar'); body.append(calendar);
            const toolbar = element('div', 'form-picker-month'); calendar.append(toolbar);
            const previous = button('', '上个月', () => changeMonth(-1), 'form-picker-nav'); previous.append(icon(-1));
            const next = button('', '下个月', () => changeMonth(1), 'form-picker-nav'); next.append(icon(1));
            const monthInput = element('input', 'form-picker-month-input'); monthInput.type = 'text'; monthInput.inputMode = 'numeric'; monthInput.placeholder = 'YYYY-MM'; monthInput.setAttribute('aria-label', '跳转到月份，格式为年-月');
            toolbar.append(previous, monthInput, next);
            const weekdays = element('div', 'form-picker-weekdays'); ['一', '二', '三', '四', '五', '六', '日'].forEach(day => weekdays.append(element('span', '', day))); calendar.append(weekdays);
            const grid = element('div', 'form-picker-grid'); grid.setAttribute('role', 'grid'); grid.setAttribute('aria-label', '选择日期'); calendar.append(grid);
            const renderCalendar = (focus = false) => {
                grid.replaceChildren(); monthInput.value = state.month.slice(0, 7);
                previous.disabled = !model.shiftMonth(state.month, -1); next.disabled = !model.shiftMonth(state.month, 1);
                let row;
                model.calendarCells(state.month).forEach((cell, index) => {
                    if (index % 7 === 0) { row = element('div', 'form-picker-week'); row.setAttribute('role', 'row'); grid.append(row); }
                    const day = button(String(cell.day), cell.value, () => { state.date = cell.value; state.cursor = cell.value; if (cell.adjacent) state.month = cell.value; renderCalendar(true); update(); }, 'form-picker-day');
                    day.dataset.date = cell.value; day.disabled = !cell.valid || !dateAllowed(cell.value);
                    day.classList.toggle('adjacent', cell.adjacent); day.classList.toggle('selected', cell.value === state.date);
                    day.setAttribute('aria-pressed', String(cell.value === state.date)); if (cell.value === today) day.setAttribute('aria-current', 'date');
                    day.tabIndex = cell.value === state.cursor ? 0 : -1;
                    const cellNode = element('div'); cellNode.setAttribute('role', 'gridcell'); cellNode.append(day); row.append(cellNode);
                });
                if (focus) grid.querySelector(`[data-date="${state.cursor}"]`)?.focus({ preventScroll: true });
            };
            const changeMonth = offset => { const shifted = model.shiftMonth(state.month, offset); if (!shifted) return; state.month = shifted; state.cursor = shifted; renderCalendar(); positionSoon(); };
            monthInput.addEventListener('change', () => { const value = `${monthInput.value}-01`; if (model.parseDate(value)) { state.month = value; state.cursor = value; renderCalendar(); } });
            grid.addEventListener('keydown', event => {
                const key = event.key; let value;
                const offsets = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 };
                if (key in offsets) value = model.shiftDate(state.cursor, offsets[key]);
                else if (key === 'PageUp' || key === 'PageDown') value = model.shiftMonth(state.cursor, key === 'PageUp' ? -1 : 1);
                else if (key === 'Home' || key === 'End') { const date = model.parseDate(state.cursor); const weekday = (date.getUTCDay() + 6) % 7; value = model.shiftDate(state.cursor, key === 'Home' ? -weekday : 6 - weekday); }
                else return;
                event.preventDefault(); event.stopPropagation();
                if (!value || !dateAllowed(value)) return; state.cursor = value; if (value.slice(0, 7) !== state.month.slice(0, 7)) state.month = value; renderCalendar(true);
            });
            renderCalendar();
            calendar.append(button('今天', '跳转并选择今天', () => { if (!dateAllowed(today)) return; state.date = state.month = state.cursor = today; renderCalendar(true); update(); }, 'form-picker-today'));
        }
        if (state.hasTime) {
            const panel = element('section', 'form-picker-time'); panel.setAttribute('aria-label', '选择时间，一分钟精度'); body.append(panel);
            const columns = [];
            [24, 60].forEach((count, column) => {
                const wrapper = element('div', 'form-picker-time-column');
                const heading = element('label', '', column === 0 ? '小时' : '分钟');
                const number = element('input', 'form-picker-time-number'); number.type = 'number'; number.min = '0'; number.max = String(count - 1); number.step = '1'; number.id = `${state.popup.id}-time-${column}`; heading.htmlFor = number.id; number.value = String(Number(state.time.split(':')[column]));
                wrapper.append(heading, number);
                const list = element('div', 'form-picker-time-list app-scrollable'); list.setAttribute('role', 'listbox'); list.setAttribute('aria-label', column === 0 ? '小时' : '分钟');
                const setTime = value => {
                    if (!Number.isInteger(value) || value < 0 || value >= count) return;
                    state.invalidTime.delete(column);
                    const parts = state.time.split(':'); parts[column] = model.pad(value); state.time = parts.join(':'); number.value = String(value);
                    [...list.children].forEach((item, index) => { item.setAttribute('aria-selected', String(index === value)); item.tabIndex = index === value ? 0 : -1; }); update();
                };
                number.addEventListener('input', () => { const value = Number(number.value); if (number.value === '' || !Number.isInteger(value) || value < 0 || value >= count) { state.invalidTime.add(column); update(); } else setTime(value); });
                number.addEventListener('blur', () => { if (!state.invalidTime.has(column)) number.value = String(Number(state.time.split(':')[column])); });
                for (let value = 0; value < count; value++) {
                    const item = button(model.pad(value), null, () => setTime(value), 'form-picker-time-option'); item.setAttribute('role', 'option'); item.setAttribute('aria-selected', String(value === Number(state.time.split(':')[column]))); item.tabIndex = value === Number(state.time.split(':')[column]) ? 0 : -1;
                    item.addEventListener('keydown', event => { const offset = event.key === 'ArrowDown' ? 1 : event.key === 'ArrowUp' ? -1 : 0; if (!offset && !['Home', 'End'].includes(event.key)) return; event.preventDefault(); event.stopPropagation(); const next = event.key === 'Home' ? 0 : event.key === 'End' ? count - 1 : Math.max(0, Math.min(count - 1, Number(state.time.split(':')[column]) + offset)); setTime(next); list.children[next].focus(); });
                    list.append(item);
                }
                wrapper.append(list); panel.append(wrapper); columns.push(list);
            });
            state.scrollTime = () => columns.forEach(list => list.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'center', behavior: 'instant' }));
        }
        state.popup.append(summary);
        const footer = element('footer', 'form-picker-footer'); footer.append(button('取消', null, () => close()), apply); state.popup.append(footer); update(); reveal();
        state.scrollTime?.();
        (state.popup.querySelector('.form-picker-day[tabindex="0"]:not([disabled])') ?? state.popup.querySelector('.form-picker-time-number') ?? apply).focus({ preventScroll: true });
    };
    const pointerdown = event => {
        if (active && !active.popup.contains(event.target) && event.target !== active.input) close(false);
        if (eligibleSelect(event.target)) { event.preventDefault(); event.target.focus({ preventScroll: true }); selectOpen(event.target); }
    };
    const click = event => {
        if (eligibleSelect(event.target)) { event.preventDefault(); if (active?.input !== event.target) selectOpen(event.target); }
    };
    const keydown = event => {
        if (active && (active.popup.contains(event.target) || active.input === event.target)) {
            if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); return; }
            if (active.kind !== 'select' && event.key === 'Enter' && event.target.matches('input')) { event.preventDefault(); event.stopPropagation(); if (event.target.classList.contains('form-picker-month-input')) event.target.dispatchEvent(new Event('change')); else active.popup.querySelector('.form-picker-apply')?.click(); return; }
            if (event.key === 'Tab') {
                if (active.kind === 'select') { close(); return; }
                const nodes = [...active.popup.querySelectorAll('button:not([disabled]), input:not([disabled])')].filter(node => node.tabIndex >= 0);
                const index = nodes.indexOf(document.activeElement);
                if (event.shiftKey && index <= 0 || !event.shiftKey && index === nodes.length - 1) { event.preventDefault(); event.stopPropagation(); nodes[event.shiftKey ? nodes.length - 1 : 0]?.focus(); }
                return;
            }
            if (active.kind === 'select') {
                const state = active, enabled = state.options.map((option, index) => !option.disabled && !option.parentElement?.disabled ? index : -1).filter(index => index >= 0);
                if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
                    event.preventDefault(); event.stopPropagation(); const current = enabled.indexOf(state.index);
                    state.index = event.key === 'Home' ? enabled[0] : event.key === 'End' ? enabled.at(-1) : enabled[Math.max(0, Math.min(enabled.length - 1, current + (event.key === 'ArrowDown' ? 1 : -1)))];
                    state.popup.children[state.index]?.focus(); return;
                }
                if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); state.choose(state.index); return; }
                if (event.key.length === 1 && !event.ctrlKey && !event.metaKey) { event.preventDefault(); state.search = `${Date.now() - (state.searchTime ?? 0) > 700 ? '' : state.search ?? ''}${event.key}`; state.searchTime = Date.now(); const index = enabled.find(index => state.options[index].textContent.trim().startsWith(state.search)); if (index !== undefined) { state.index = index; state.popup.children[index]?.focus(); } }
            }
        } else if (eligibleSelect(event.target) && ['ArrowDown', 'ArrowUp', 'Enter', ' '].includes(event.key)) { event.preventDefault(); event.stopPropagation(); selectOpen(event.target); }
    };
    const observer = new MutationObserver(() => { if (active && (!active.input.isConnected || active.input.matches(':disabled') || active.kind !== 'select' && !eligible(active.input))) close(false); });
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['disabled', 'type'] });
    document.addEventListener('pointerdown', pointerdown, true); document.addEventListener('click', click, true); document.addEventListener('keydown', keydown, true);
    const pagehide = () => close(false);
    document.addEventListener('scroll', positionSoon, true); window.addEventListener('resize', positionSoon); window.addEventListener('pagehide', pagehide); window.visualViewport?.addEventListener('resize', positionSoon);
    window.moicalendarFormPicker = { open, close, dispose: () => { close(false); observer.disconnect(); cancelAnimationFrame(positionFrame); document.removeEventListener('pointerdown', pointerdown, true); document.removeEventListener('click', click, true); document.removeEventListener('keydown', keydown, true); document.removeEventListener('scroll', positionSoon, true); window.removeEventListener('resize', positionSoon); window.removeEventListener('pagehide', pagehide); window.visualViewport?.removeEventListener('resize', positionSoon); } };
})();
