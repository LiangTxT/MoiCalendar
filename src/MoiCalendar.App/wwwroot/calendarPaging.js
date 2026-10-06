// 浏览器只处理手势和视觉；日期计算、事件读取仍由 Home 的应用服务负责。
(() => {
    const sessions = new WeakMap();
    const ease = "cubic-bezier(0.16, 1, 0.3, 1)";
    const clamp = (n, min, max) => Math.min(max, Math.max(min, n));
    const reduced = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    function reset(s) {
        if (s.raf) cancelAnimationFrame(s.raf);
        s.raf = 0;
        for (const animation of s.animations) animation.cancel();
        s.animations = [];
        s.ghost?.remove();
        s.ghost = null;
        s.frame.style.transform = "";
        s.surface.classList.remove("is-period-paging");
        s.busy = false;
        s.offset = 0;
    }

    function paint(s, offset) {
        const width = s.gestureWidth || s.surface.clientWidth;
        s.offset = clamp(offset, -width, width);
        s.surface.classList.add("is-period-paging");
        if (!s.raf) s.raf = requestAnimationFrame(() => {
            s.raf = 0;
            s.frame.style.transform = reduced() ? "" : `translate3d(${s.offset}px,0,0)`;
        });
    }

    function animate(s, element, from, to, duration = 320) {
        if (reduced() || !element.animate) return Promise.resolve();
        const animation = element.animate([
            { transform: `translate3d(${from}px,0,0)` },
            { transform: `translate3d(${to}px,0,0)` }
        ], { duration, easing: ease, fill: "both" });
        s.animations.push(animation);
        return animation.finished.catch(() => {});
    }

    // 单个不可交互的离场快照：无需重渲染两套 Blazor 网格，保留滚动与事件位置。
    function snapshot(s) {
        const original = s.surface.querySelector(".week-horizontal-scroll");
        const ghost = original.cloneNode(true);
        ghost.classList.add("period-page-snapshot");
        ghost.setAttribute("aria-hidden", "true");
        ghost.inert = true;
        for (const node of ghost.querySelectorAll("[id]")) node.removeAttribute("id");
        const frame = ghost.querySelector(".week-grid-frame");
        frame.style.transform = "";
        s.surface.append(ghost);
        ghost.scrollLeft = original.scrollLeft;
        ghost.querySelector(".week-timed-scroll").scrollTop = s.timeline.scrollTop;
        return ghost;
    }

    async function settle(s, direction) {
        if (s.busy || s.disposed) return;
        s.busy = true;
        if (s.raf) cancelAnimationFrame(s.raf);
        s.raf = 0;
        s.suppressUntil = performance.now() + 500;
        const offset = s.offset;
        if (!direction) {
            await animate(s, s.frame, offset, 0, 180);
            reset(s);
            return;
        }
        const top = s.timeline.scrollTop;
        const width = s.surface.clientWidth;
        s.ghost = snapshot(s);
        // 加载期间保持松手时的位移，不先跳回原点再开始离场。
        s.ghost.style.transform = reduced() ? "" : `translate3d(${offset}px,0,0)`;
        s.surface.classList.add("is-period-paging");
        s.frame.style.transform = `translate3d(${direction * width}px,0,0)`;
        const oldKey = s.key;
        try {
            // .NET 返回之后，等待包含新日程的实际渲染，避免先展示旧内容再闪换。
            const rendered = new Promise(resolve => { s.resolveRender = resolve; });
            await s.dotNet.invokeMethodAsync("NavigateTimeGridPeriod", direction);
            if (s.key === oldKey && s.enabled) {
                await Promise.race([rendered, new Promise(resolve => { s.timeout = setTimeout(resolve, 2000); })]);
            }
            if (s.disposed) return;
            clearTimeout(s.timeout);
            if (s.key === oldKey) return;
            s.timeline.scrollTop = top;
            await Promise.all([
                animate(s, s.ghost, offset, -direction * width),
                animate(s, s.frame, direction * width, 0)
            ]);
        } catch (error) {
            // 加载失败恢复原画布；本地存储错误由页面显示，不吞掉数据错误。
            console.warn("日历翻页未完成", error);
        } finally {
            clearTimeout(s.timeout);
            s.resolveRender = null;
            reset(s);
            s.lastWheel = performance.now();
        }
    }

    window.moicalendarPaging = {
        attach(surface, dotNet) {
            this.dispose(surface);
            const s = { surface, dotNet, frame: surface.querySelector(".week-grid-frame"),
                timeline: surface.querySelector(".week-timed-scroll"), enabled: true, key: null,
                offset: 0, animations: [], lastWheel: -Infinity, suppressUntil: 0, wheelAmount: 0 };
            sessions.set(surface, s);
            surface.classList.add("has-period-paging");
            const blocked = () => !s.enabled || s.busy || surface.classList.contains("is-browser-interacting");
            s.down = event => {
                if (blocked() || event.button !== 0 || event.isPrimary === false) return;
                const header = event.target.closest(".week-day-headings");
                if (event.pointerType === "mouse" && !header) return;
                if (!header && !event.target.closest(".week-timed-days,.week-all-day-row")) return;
                s.pointer = { id: event.pointerId, x: event.clientX, y: event.clientY,
                    time: performance.now(), type: event.pointerType, header: !!header, axis: null };
                s.gestureWidth = surface.clientWidth;
            };
            s.move = event => {
                const p = s.pointer;
                if (!p || p.id !== event.pointerId || s.busy) return;
                const dx = event.clientX - p.x, dy = event.clientY - p.y;
                if (!p.axis) {
                    if (Math.max(Math.abs(dx), Math.abs(dy)) < 8) return;
                    if (Math.abs(dx) <= Math.abs(dy) * 1.3 || blocked() ||
                        (p.type === "touch" && !p.header && performance.now() - p.time >= 350)) { s.pointer = null; return; }
                    p.axis = "x";
                    s.suppressUntil = performance.now() + 500;
                    surface.setPointerCapture?.(p.id);
                    window.moicalendarInteraction?.cancelActiveInteraction(surface);
                    s.cancelPromise = dotNet.invokeMethodAsync("CancelTimeGridForPaging");
                }
                event.preventDefault();
                paint(s, dx);
            };
            s.up = async event => {
                const p = s.pointer;
                if (!p || p.id !== event.pointerId) return;
                s.pointer = null;
                if (surface.hasPointerCapture?.(p.id)) surface.releasePointerCapture(p.id);
                if (!p.axis) return;
                s.suppressUntil = performance.now() + 500;
                const dx = event.clientX - p.x;
                const speed = Math.abs(dx) / Math.max(1, performance.now() - p.time);
                const commit = event.type !== "pointercancel" && (Math.abs(dx) >= Math.min(140, surface.clientWidth * .22) || (Math.abs(dx) > 32 && speed > .5));
                paint(s, dx);
                await s.cancelPromise;
                await settle(s, commit ? dx < 0 ? 1 : -1 : 0);
            };
            s.touch = event => {
                if (event.touches.length > 1) {
                    s.pointer = null;
                    if (!s.busy) reset(s);
                } else if (s.pointer?.axis === "x") event.preventDefault();
            };
            s.wheel = event => {
                if (event.ctrlKey || !s.enabled) return; // 保留触控板缩放。
                const header = event.target.closest(".week-day-headings");
                const dx = event.shiftKey && !event.deltaX ? event.deltaY : event.deltaX;
                if (!header && Math.abs(dx) <= Math.abs(event.deltaY) * 1.3) return;
                event.preventDefault();
                const now = performance.now();
                if (blocked()) { s.wheelLocked = true; s.lastWheel = now; return; }
                // 惯性尾部必须静止一段时间再接受下一次，防止一次滑动连跳几周。
                if (s.wheelLocked && now - s.lastWheel < 180) { s.lastWheel = now; return; }
                s.wheelLocked = false;
                s.lastWheel = now;
                const delta = (dx || (header ? event.deltaY : 0)) * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? surface.clientWidth : 1);
                if (!delta) return;
                s.wheelAmount += delta;
                if (!s.wheelTimer) s.gestureWidth = surface.clientWidth;
                paint(s, -s.wheelAmount);
                clearTimeout(s.wheelTimer);
                if (Math.abs(s.wheelAmount) >= 64) {
                    const amount = s.wheelAmount;
                    s.wheelAmount = 0;
                    s.wheelTimer = 0;
                    s.wheelLocked = true;
                    void settle(s, amount > 0 ? 1 : -1);
                    return;
                }
                s.wheelTimer = setTimeout(() => {
                    s.wheelTimer = 0;
                    const amount = s.wheelAmount;
                    s.wheelAmount = 0;
                    s.wheelLocked = true;
                    if (!blocked()) void settle(s, Math.abs(amount) >= 32 ? amount > 0 ? 1 : -1 : 0);
                }, 80);
            };
            s.click = event => {
                if (performance.now() < s.suppressUntil || s.pointer?.axis === "x") {
                    event.preventDefault(); event.stopImmediatePropagation();
                }
            };
            surface.addEventListener("pointerdown", s.down, true);
            window.addEventListener("pointermove", s.move, { capture: true, passive: false });
            window.addEventListener("pointerup", s.up, true);
            window.addEventListener("pointercancel", s.up, true);
            surface.addEventListener("touchmove", s.touch, { passive: false });
            surface.addEventListener("wheel", s.wheel, { passive: false });
            surface.addEventListener("click", s.click, true);
        },
        rendered(surface, key, enabled) {
            const s = sessions.get(surface);
            if (!s) return;
            // Blazor 更新拖动状态的 class 时可能替换增强类，渲染后恢复。
            surface.classList.add("has-period-paging");
            if (s.busy || s.pointer?.axis === "x") surface.classList.add("is-period-paging");
            s.enabled = enabled;
            if (s.key !== key) { s.key = key; s.resolveRender?.(); }
        },
        dispose(surface) {
            const s = sessions.get(surface);
            if (!s) return;
            s.disposed = true;
            s.resolveRender?.();
            clearTimeout(s.wheelTimer);
            clearTimeout(s.timeout);
            reset(s);
            surface.removeEventListener("pointerdown", s.down, true);
            window.removeEventListener("pointermove", s.move, true);
            window.removeEventListener("pointerup", s.up, true);
            window.removeEventListener("pointercancel", s.up, true);
            surface.removeEventListener("touchmove", s.touch);
            surface.removeEventListener("wheel", s.wheel);
            surface.removeEventListener("click", s.click, true);
            surface.classList.remove("has-period-paging");
            sessions.delete(surface);
        }
    };
})();
