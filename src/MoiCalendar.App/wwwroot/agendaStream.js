const states = new WeakMap();
export function connect(root, reference, allowAutoLoad) {
    let state = states.get(root);
    if (!state) {
        state = { observer: null, height: null, frame: 0, key: null, onScroll: null };
        state.onScroll = () => {
            if (state.frame) return;
            state.frame = requestAnimationFrame(() => {
                state.frame = 0;
                const top = root.getBoundingClientRect().top;
                const sections = [...root.querySelectorAll('[data-agenda-month]')];
                const active = sections.find(section => section.getBoundingClientRect().bottom > top + 60);
                const key = active?.dataset.agendaMonth;
                if (key && key !== state.key) {
                    state.key = key;
                    const [year, month] = key.split('-').map(Number);
                    reference.invokeMethodAsync('ReportVisibleMonth', year, month).catch(() => {});
                }
            });
        };
        root.addEventListener('scroll', state.onScroll, { passive: true });
        state.observer = new IntersectionObserver(entries => {
            if (entries.some(entry => entry.isIntersecting)) reference.invokeMethodAsync('ExtendAsync', 1).catch(() => {});
        }, { root, rootMargin: '0px 0px 300px 0px' });
        states.set(root, state);
    }
    if (state.height !== null) {
        root.scrollTop += root.scrollHeight - state.height;
        state.height = null;
    }
    state.observer.disconnect();
    const next = root.querySelector('[data-agenda-next]');
    if (allowAutoLoad && next && !next.disabled) state.observer.observe(next);
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
        root.removeEventListener('scroll', state.onScroll);
        cancelAnimationFrame(state.frame);
    }
    states.delete(root);
}
