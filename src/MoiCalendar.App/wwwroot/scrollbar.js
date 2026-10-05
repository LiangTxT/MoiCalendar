/* 纯视觉活动状态；没有滚动位置写入、业务数据、.NET 调用或持久化。 */
(() => {
    window.moicalendarScrollbars?.dispose();
    const pending = new Set();
    const timers = new Map();
    let frame = 0;
    let listening = false;
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const clear = () => {
        if (frame) cancelAnimationFrame(frame);
        frame = 0;
        pending.clear();
        for (const [element, timer] of timers) {
            clearTimeout(timer);
            element.classList.remove('is-scrolling');
        }
        timers.clear();
    };
    const onScroll = event => {
        const element = event.target;
        if (!element?.matches?.('.app-scrollable:not(.scrollbar-hidden)')) return;
        const oldTimer = timers.get(element);
        if (oldTimer !== undefined) clearTimeout(oldTimer);
        pending.add(element);
        timers.set(element, setTimeout(() => {
            pending.delete(element);
            element.classList.remove('is-scrolling');
            timers.delete(element);
        }, reducedMotion.matches ? 100 : 650));
        if (!frame) frame = requestAnimationFrame(() => {
            frame = 0;
            for (const element of pending) {
                if (element.isConnected && !element.classList.contains('is-scrolling'))
                    element.classList.add('is-scrolling');
            }
            pending.clear();
        });
    };
    const initialize = () => {
        if (listening) return;
        document.addEventListener('scroll', onScroll, { capture: true, passive: true });
        listening = true;
    };
    const suspend = () => {
        document.removeEventListener('scroll', onScroll, true);
        listening = false;
        clear();
    };
    window.addEventListener('pagehide', suspend);
    window.addEventListener('pageshow', initialize);
    window.moicalendarScrollbars = {
        initialize,
        dispose: () => {
            suspend();
            window.removeEventListener('pagehide', suspend);
            window.removeEventListener('pageshow', initialize);
        }
    };
    initialize();
})();
