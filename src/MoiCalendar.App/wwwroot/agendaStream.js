const states = new WeakMap();
export function connect(root, reference, allowAutoLoad, contextKey = '') {
    let state = states.get(root);
    if (!state) {
        state = { observer: null, height: null, frame: 0, key: null, onScroll: null,
            disposed: false, pending: false, allowAutoLoad: false, previousTop: root.scrollTop,
            touchY: null, reference, next: null };
        state.extend = direction => {
            if (state.disposed || state.pending || !state.allowAutoLoad) return;
            state.pending = true;
            state.reference.invokeMethodAsync('ExtendAsync', direction).catch(() => {}).finally(() => {
                state.pending = false;
            });
        };
        state.tryEarlier = () => {
            if (root.scrollTop <= 260) state.extend(-1);
        };
        state.onScroll = () => {
            const currentTop = root.scrollTop;
            if (currentTop < state.previousTop) state.tryEarlier();
            state.previousTop = currentTop;
            if (state.frame) return;
            state.frame = requestAnimationFrame(() => {
                state.frame = 0;
                if (state.disposed) return;
                const top = root.getBoundingClientRect().top;
                const sections = [...root.querySelectorAll('[data-agenda-month]')];
                const active = sections.find(section => section.getBoundingClientRect().bottom > top + 60);
                const key = active?.dataset.agendaMonth;
                if (key && key !== state.key) {
                    state.key = key;
                    const [year, month] = key.split('-').map(Number);
                    state.reference.invokeMethodAsync('ReportVisibleMonth', year, month).catch(() => {
                        if (state.key === key) state.key = null;
                    });
                }
            });
        };
        root.addEventListener('scroll', state.onScroll, { passive: true });
        // 位于 scrollTop=0 时，向上滚轮不会产生 scroll；仍要响应用户继续向前浏览。
        state.onWheel = event => { if (event.deltaY < 0) state.tryEarlier(); };
        state.onTouchStart = event => { state.touchY = event.touches.length === 1 ? event.touches[0].clientY : null; };
        state.onTouchMove = event => {
            if (state.touchY !== null && event.touches.length === 1 && event.touches[0].clientY > state.touchY + 12) state.tryEarlier();
        };
        state.onTouchEnd = () => { state.touchY = null; };
        state.onKey = event => {
            if (event.target?.matches?.('input, textarea, select, [contenteditable="true"]')) return;
            if (['ArrowUp', 'PageUp', 'Home'].includes(event.key)) state.tryEarlier();
        };
        root.addEventListener('wheel', state.onWheel, { passive: true });
        root.addEventListener('touchstart', state.onTouchStart, { passive: true });
        root.addEventListener('touchmove', state.onTouchMove, { passive: true });
        root.addEventListener('touchend', state.onTouchEnd, { passive: true });
        root.addEventListener('touchcancel', state.onTouchEnd, { passive: true });
        root.addEventListener('keydown', state.onKey);
        state.observer = new IntersectionObserver(entries => {
            if (entries.some(entry => entry.target === state.next && entry.isIntersecting)) state.extend(1);
        }, { root, rootMargin: '0px 0px 300px 0px' });
        states.set(root, state);
    }
    state.reference = reference;
    state.allowAutoLoad = allowAutoLoad;
    if (state.contextKey !== contextKey) {
        state.contextKey = contextKey;
        state.height = null;
        state.previousTop = root.scrollTop;
        state.key = null;
    }
    if (state.height !== null) {
        root.scrollTop += root.scrollHeight - state.height;
        state.height = null;
        state.previousTop = root.scrollTop;
    }
    const next = root.querySelector('[data-agenda-next]');
    const observing = !!(allowAutoLoad && next && !next.disabled);
    if (state.next !== next || state.observing !== observing) {
        state.observer.disconnect();
        state.next = next;
        state.observing = observing;
        if (state.observing) state.observer.observe(next);
    }
    state.onScroll();
}
export function rememberPosition(root) {
    const state = states.get(root);
    if (state) state.height = root.scrollHeight;
}
export function disconnect(root) {
    const state = states.get(root);
    state?.observer.disconnect();
    if (state) {
        state.disposed = true;
        root.removeEventListener('scroll', state.onScroll);
        root.removeEventListener('wheel', state.onWheel);
        root.removeEventListener('touchstart', state.onTouchStart);
        root.removeEventListener('touchmove', state.onTouchMove);
        root.removeEventListener('touchend', state.onTouchEnd);
        root.removeEventListener('touchcancel', state.onTouchEnd);
        root.removeEventListener('keydown', state.onKey);
        cancelAnimationFrame(state.frame);
    }
    states.delete(root);
}
