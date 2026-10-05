/* 仅负责原生输入交互；值和校验仍由现有 Blazor 表单管理。 */
(() => {
    window.moicalendarDateTimePicker?.dispose();
    let pending = null;
    const manual = new WeakSet();
    const supported = !!window.moicalendarFormPicker || typeof HTMLInputElement.prototype.showPicker === 'function';
    const eligible = input => input?.matches?.('input[data-date-time-picker]') &&
        ['date', 'time', 'datetime-local'].includes(input.type) && !input.disabled && !input.readOnly;
    const cancel = () => {
        if (pending) clearTimeout(pending.timer);
        pending = null;
    };
    const enterManual = input => {
        cancel();
        window.moicalendarFormPicker?.close(false);
        manual.add(input);
        input.focus({ preventScroll: true });
    };
    const open = input => {
        if (!eligible(input) || !input.isConnected || document.activeElement !== input) return;
        try { if (window.moicalendarFormPicker) window.moicalendarFormPicker.open(input); else input.showPicker(); }
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
        cancel();
        input.focus({ preventScroll: true });
        if (event.pointerType === 'touch') { open(input); return; }
        pending = { input, timer: setTimeout(() => {
            pending = null;
            open(input);
        }, 350) };
    };
    const doubleClick = event => {
        if (supported && eligible(event.target)) enterManual(event.target);
    };
    const pointerDown = event => {
        if (pending && event.target !== pending.input) cancel();
    };
    const focusOut = event => {
        if (pending?.input === event.target) cancel();
        manual.delete(event.target);
    };
    const keyDown = event => {
        if (!eligible(event.target)) return;
        if (event.altKey && event.key === 'ArrowDown' && supported) {
            cancel();
            event.preventDefault();
            open(event.target);
        } else if (pending?.input === event.target) enterManual(event.target);
    };
    const handlers = { click, dblclick: doubleClick, pointerdown: pointerDown, focusout: focusOut, keydown: keyDown };
    for (const [name, handler] of Object.entries(handlers)) document.addEventListener(name, handler, true);
    document.documentElement.classList.toggle('date-time-picker-supported', supported);
    window.addEventListener('pagehide', cancel);
    window.moicalendarDateTimePicker = { dispose: () => {
        cancel();
        for (const [name, handler] of Object.entries(handlers)) document.removeEventListener(name, handler, true);
        document.documentElement.classList.remove('date-time-picker-supported');
        window.removeEventListener('pagehide', cancel);
    } };
})();
