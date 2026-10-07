// 缩放显示密度，不修改事件时间；所有命中测试仍使用网格真实边界。
(() => {
    const key = 'moicalendar.timeGridHourHeight.v1';
    const clamp = value => Math.max(32, Math.min(208, value));
    let height = 52;
    try {
        const saved = Number(localStorage.getItem(key));
        if (Number.isFinite(saved) && saved >= 32 && saved <= 208) height = saved;
    } catch { /* 存储不可用时仍允许缩放。 */ }
    window.moicalendarZoom = {
        attach(timeline) {
            this.dispose(timeline);
            const grid = timeline.querySelector('.week-timed-days');
            const surface = timeline.closest('.week-calendar');
            if (!grid || !surface) return;
            timeline.style.setProperty('--time-grid-hour-height', `${height}px`);
            let pinch = null, blocked = false, frame = 0, pending = null;
            const distance = touches => Math.hypot(touches[0].clientX - touches[1].clientX, touches[0].clientY - touches[1].clientY);
            const center = touches => (touches[0].clientY + touches[1].clientY) / 2;
            const apply = () => {
                frame = 0;
                if (!pending || !pinch) return;
                height = pending.height;
                timeline.style.setProperty('--time-grid-hour-height', `${height}px`);
                const gridTop = grid.getBoundingClientRect().top - timeline.getBoundingClientRect().top + timeline.scrollTop;
                timeline.scrollTop = Math.max(0, gridTop + pinch.anchor * grid.clientHeight - pending.center);
                pending = null;
            };
            const finish = () => {
                if (frame) cancelAnimationFrame(frame);
                if (pending) apply();
                pinch = null;
                frame = 0;
                try { localStorage.setItem(key, String(height)); } catch { }
            };
            const start = event => {
                if (event.touches.length !== 2) {
                    if (event.touches.length > 2) { finish(); blocked = true; }
                    return;
                }
                if (blocked || surface.classList.contains('is-period-paging')) return;
                const gap = distance(event.touches);
                if (gap < 10) return;
                event.preventDefault();
                window.moicalendarInteraction?.cancelActiveInteraction(surface);
                const y = center(event.touches);
                pinch = { distance: gap, height,
                    anchor: Math.max(0, Math.min(1, (y - grid.getBoundingClientRect().top) / grid.clientHeight)) };
            };
            const move = event => {
                if (!pinch || event.touches.length !== 2) return;
                event.preventDefault();
                pending = { height: clamp(pinch.height * distance(event.touches) / pinch.distance),
                    center: center(event.touches) - timeline.getBoundingClientRect().top };
                if (!frame) frame = requestAnimationFrame(apply);
            };
            const end = event => {
                if (pinch && event.touches.length !== 2) { finish(); blocked = true; }
                if (!event.touches.length) blocked = false;
            };
            timeline.addEventListener('touchstart', start, {passive: false});
            timeline.addEventListener('touchmove', move, {passive: false});
            timeline.addEventListener('touchend', end);
            timeline.addEventListener('touchcancel', end);
            timeline._moicalendarZoomDispose = () => {
                finish();
                timeline.removeEventListener('touchstart', start);
                timeline.removeEventListener('touchmove', move);
                timeline.removeEventListener('touchend', end);
                timeline.removeEventListener('touchcancel', end);
            };
        },
        dispose(timeline) {
            timeline?._moicalendarZoomDispose?.();
            if (timeline) delete timeline._moicalendarZoomDispose;
        }
    };
})();
