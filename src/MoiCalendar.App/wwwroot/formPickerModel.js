/* 日期选择器的纯 UI 投影。仅使用民用日期，不涉及事件时区转换或持久化。 */
(() => {
    const pad = value => String(value).padStart(2, '0');
    const dateKey = date => `${String(date.getUTCFullYear()).padStart(4, '0')}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`;
    const date = (year, month, day) => { const result = new Date(0); result.setUTCFullYear(year, month - 1, day); result.setUTCHours(0, 0, 0, 0); return result; };
    const parseDate = value => {
        const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '');
        if (!match) return null;
        const [year, month, day] = match.slice(1).map(Number);
        const result = date(year, month, day);
        return year >= 1 && year <= 9999 && dateKey(result) === value ? result : null;
    };
    const shiftDate = (value, days) => { const result = parseDate(value); if (!result) return null; result.setUTCDate(result.getUTCDate() + days); return result.getUTCFullYear() >= 1 && result.getUTCFullYear() <= 9999 ? dateKey(result) : null; };
    const shiftMonth = (value, offset) => {
        const original = parseDate(value); if (!original) return null;
        const first = date(original.getUTCFullYear(), original.getUTCMonth() + 1 + offset, 1);
        if (first.getUTCFullYear() < 1 || first.getUTCFullYear() > 9999) return null;
        const last = date(first.getUTCFullYear(), first.getUTCMonth() + 2, 0).getUTCDate();
        return dateKey(date(first.getUTCFullYear(), first.getUTCMonth() + 1, Math.min(original.getUTCDate(), last)));
    };
    const calendarCells = value => {
        const selected = parseDate(value); if (!selected) return [];
        const first = date(selected.getUTCFullYear(), selected.getUTCMonth() + 1, 1);
        first.setUTCDate(1 - (first.getUTCDay() + 6) % 7);
        return Array.from({ length: 42 }, (_, index) => {
            const current = new Date(first); current.setUTCDate(first.getUTCDate() + index);
            return { value: dateKey(current), day: current.getUTCDate(), adjacent: current.getUTCMonth() !== selected.getUTCMonth(), valid: current.getUTCFullYear() >= 1 && current.getUTCFullYear() <= 9999 };
        });
    };
    const validTime = value => /^([01]\d|2[0-3]):[0-5]\d$/.test(value);
    const inBounds = (value, min, max) => (!min || value >= min) && (!max || value <= max);
    const position = (anchor, width, height, viewport) => {
        const gap = 8, inset = 12;
        const below = viewport.height - anchor.bottom - gap - inset;
        const above = anchor.top - gap - inset;
        const side = below >= height || below >= above ? 'bottom' : 'top';
        const available = Math.max(0, side === 'bottom' ? below : above);
        // 两侧都不足时使用视口内的完整高度，而不是把确认按钮挤进滚动区。
        if (height > available) return { left: Math.max(inset, Math.min(anchor.left, viewport.width - width - inset)), top: Math.max(inset, Math.min(anchor.bottom + gap, viewport.height - height - inset)), maxHeight: viewport.height - inset * 2, side };
        const actualHeight = Math.min(height, available);
        return { left: Math.max(inset, Math.min(anchor.left, viewport.width - width - inset)), top: side === 'bottom' ? anchor.bottom + gap : Math.max(inset, anchor.top - actualHeight - gap), maxHeight: available, side };
    };
    window.moicalendarPickerModel = { pad, dateKey, parseDate, shiftDate, shiftMonth, calendarCells, validTime, inBounds, position };
})();
