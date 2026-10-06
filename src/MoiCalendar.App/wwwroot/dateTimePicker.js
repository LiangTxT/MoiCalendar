/* 仅负责原生输入交互；值和校验仍由现有 Blazor 表单管理。 */
(() => {
    window.moicalendarDateTimePicker?.dispose();
    const manual = new WeakSet();
    const supported = !!window.moicalendarFormPicker || typeof HTMLInputElement.prototype.showPicker === 'function';
    const eligible = input => input?.matches?.('input[data-date-time-picker]') &&
        ['date', 'time', 'datetime-local'].includes(input.type) && !input.disabled && !input.readOnly;
    const enterManual = input => {
        window.moicalendarFormPicker?.close(false);
        manual.add(input);
        input.focus({ preventScroll: true });
    };
    const open = (input, preserveFocus = false) => {
        if (!eligible(input) || !input.isConnected || document.activeElement !== input) return;
        try { if (window.moicalendarFormPicker) window.moicalendarFormPicker.open(input, preserveFocus); else input.showPicker(); }
        catch {
            // 缺少用户激活、嵌入限制或平台不支持时保留原生图标与键盘输入。
            input.classList.add('picker-native-fallback');
        }
    };
    const click = event => {
        const input = event.target;
        if (!supported || !eligible(input) || input.classList.contains('picker-native-fallback')) return;
        if (event.detail > 1) {
            event.preventDefault();
            enterManual(input);
            return;
        }
        if (manual.has(input) || event.detail === 0) return;
        event.preventDefault();
        input.focus({ preventScroll: true });
        open(input, true);
    };
    const doubleClick = event => {
        if (supported && eligible(event.target)) enterManual(event.target);
    };
    const focusOut = event => {
        manual.delete(event.target);
    };
    const keyDown = event => {
        if (!eligible(event.target)) return;
        if (event.altKey && event.key === 'ArrowDown' && supported) {
            event.preventDefault();
            open(event.target);
        } else if (event.key.length === 1 || ['Backspace', 'Delete', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) enterManual(event.target);
    };
    const handlers = { click, dblclick: doubleClick, focusout: focusOut, keydown: keyDown };
    for (const [name, handler] of Object.entries(handlers)) document.addEventListener(name, handler, true);
    document.documentElement.classList.toggle('date-time-picker-supported', supported);
    window.moicalendarDateTimePicker = { dispose: () => {
        for (const [name, handler] of Object.entries(handlers)) document.removeEventListener(name, handler, true);
        document.documentElement.classList.remove('date-time-picker-supported');
    } };
})();
