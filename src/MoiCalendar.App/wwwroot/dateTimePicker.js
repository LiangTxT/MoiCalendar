/* 日期／时间仅由按钮打开网页弹层，不聚焦原生输入，也不监听双击。 */
(() => {
    window.moicalendarDateTimePicker?.dispose();
    const open = event => {
        const trigger = event.target.closest?.('button[data-temporal-target]');
        if (!trigger || trigger.matches(':disabled')) return;
        const input = document.getElementById(trigger.dataset.temporalTarget);
        if (!input || !window.moicalendarFormPicker) return;
        event.preventDefault();
        window.moicalendarFormPicker.open(input, trigger);
    };
    const keydown = event => {
        if (event.altKey && event.key === 'ArrowDown') open(event);
    };
    document.addEventListener('click', open, true);
    document.addEventListener('keydown', keydown, true);
    window.moicalendarDateTimePicker = { dispose: () => {
        document.removeEventListener('click', open, true);
        document.removeEventListener('keydown', keydown, true);
    } };
})();
