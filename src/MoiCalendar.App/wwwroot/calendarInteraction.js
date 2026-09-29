(() => {
    const active = new WeakMap();
    const snapMinutes = 15;
    const movementThreshold = 6;
    const touchHoldMilliseconds = 350;
    const edgeSize = 40;
    const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
    const snap = value => Math.round(value / snapMinutes) * snapMinutes;

    function cleanup(surface, session) {
        if (!session) return;
        window.removeEventListener("pointermove", session.move, true);
        window.removeEventListener("pointerup", session.up, true);
        window.removeEventListener("pointercancel", session.cancel, true);
        if (session.frame) cancelAnimationFrame(session.frame);
        session.mirror?.remove();
        session.source?.classList.remove("interaction-source");
        surface?.classList.remove("is-browser-interacting");
        if (surface?.hasPointerCapture?.(session.pointerId)) surface.releasePointerCapture(session.pointerId);
        active.delete(surface);
    }

    function createMirror(config) {
        const mirror = document.createElement("div");
        mirror.className = "calendar-drag-mirror";
        const title = document.createElement("strong");
        title.textContent = config.label;
        const time = document.createElement("span");
        mirror.append(title, time);
        document.body.append(mirror);
        return { mirror, time };
    }

    function calculate(config, grid, scroll, x, y) {
        const bounds = grid.getBoundingClientRect();
        const index = clamp(Math.floor((x - bounds.left) / Math.max(1, bounds.width) * config.dates.length), 0, config.dates.length - 1);
        const contentY = y - bounds.top;
        const minuteRange = config.visibleEndMinute - config.visibleStartMinute;
        const pointerMinute = clamp(snap(config.visibleStartMinute + contentY / Math.max(1, grid.scrollHeight) * minuteRange), config.visibleStartMinute, config.visibleEndMinute);
        let startMinute;
        let endMinute;

        if (config.kind === "resize") {
            startMinute = config.eventStartMinute;
            endMinute = clamp(Math.max(startMinute + snapMinutes, pointerMinute), startMinute + snapMinutes, config.visibleEndMinute);
        } else if (config.kind === "selection") {
            const anchor = config.pointerStartMinute;
            startMinute = Math.min(anchor, pointerMinute);
            endMinute = Math.max(anchor, pointerMinute);
            if (endMinute - startMinute < snapMinutes) endMinute = Math.min(config.visibleEndMinute, startMinute + snapMinutes);
        } else {
            const grabOffsetMinutes = config.pointerStartMinute - config.eventStartMinute;
            startMinute = clamp(pointerMinute - grabOffsetMinutes, config.visibleStartMinute, config.visibleEndMinute - config.durationMinutes);
            endMinute = Math.min(config.visibleEndMinute, startMinute + config.durationMinutes);
        }

        return { cancelled: false, targetDate: config.kind === "resize" ? config.originalDate : config.dates[index], startMinute, endMinute };
    }

    function formatMinute(value) {
        const normalized = Math.max(0, value);
        return `${String(Math.floor(normalized / 60)).padStart(2, "0")}:${String(normalized % 60).padStart(2, "0")}`;
    }

    window.moicalendarInteraction = {
        getElementBounds: element => {
            const rect = element.getBoundingClientRect();
            return { left: rect.left, top: rect.top, width: rect.width, height: rect.height, scrollLeft: element.scrollLeft ?? 0, scrollTop: element.scrollTop ?? 0 };
        },
        capturePointer: (element, pointerId) => element?.setPointerCapture?.(pointerId),
        releasePointer: (element, pointerId) => {
            if (element?.hasPointerCapture?.(pointerId)) element.releasePointerCapture(pointerId);
        },
        cancelActiveInteraction: surface => cleanup(surface, active.get(surface)),
        beginTimeGridInteraction: (surface, grid, scroll, dotNet, config) => {
            cleanup(surface, active.get(surface));
            const initialBounds = grid.getBoundingClientRect();
            const minuteRange = config.visibleEndMinute - config.visibleStartMinute;
            const pointerStartMinute = clamp(
                snap(config.visibleStartMinute + (config.startY - initialBounds.top) / Math.max(1, grid.scrollHeight) * minuteRange),
                config.visibleStartMinute,
                config.visibleEndMinute);
            const interactionConfig = { ...config, pointerStartMinute };
            const session = {
                pointerId: config.pointerId,
                startedAt: performance.now(),
                active: false,
                latest: null,
                frame: 0,
                mirror: null,
                time: null,
                source: document.activeElement?.closest?.(".week-timed-event") ?? null
            };

            const update = () => {
                session.frame = 0;
                if (!session.latest || !session.mirror) return;
                const { x, y } = session.latest;
                const scrollBounds = scroll.getBoundingClientRect();
                if (y < scrollBounds.top + edgeSize) scroll.scrollTop -= 12;
                else if (y > scrollBounds.bottom - edgeSize) scroll.scrollTop += 12;
                session.result = calculate(interactionConfig, grid, scroll, x, y);
                session.mirror.style.transform = `translate3d(${Math.round(x + 12)}px, ${Math.round(y + 12)}px, 0)`;
                session.time.textContent = `${session.result.targetDate}  ${formatMinute(session.result.startMinute)}–${formatMinute(session.result.endMinute)}`;
            };

            session.move = event => {
                if (event.pointerId !== session.pointerId) return;
                const dx = event.clientX - config.startX;
                const dy = event.clientY - config.startY;
                if (!session.active) {
                    if (dx * dx + dy * dy < movementThreshold * movementThreshold) return;
                    if (config.pointerType === "touch" && performance.now() - session.startedAt < touchHoldMilliseconds) return;
                    session.active = true;
                    const created = createMirror(config);
                    session.mirror = created.mirror;
                    session.time = created.time;
                    session.source?.classList.add("interaction-source");
                    surface.classList.add("is-browser-interacting");
                    surface.setPointerCapture?.(config.pointerId);
                }
                event.preventDefault();
                session.latest = { x: event.clientX, y: event.clientY };
                if (!session.frame) session.frame = requestAnimationFrame(update);
            };

            session.finish = async (event, cancelled) => {
                if (event.pointerId !== session.pointerId) return;
                if (!session.active) {
                    cleanup(surface, session);
                    return;
                }
                if (session.frame) {
                    cancelAnimationFrame(session.frame);
                    session.frame = 0;
                    update();
                }
                const result = session.result
                    ? { ...session.result, cancelled }
                    : { cancelled: true, targetDate: config.originalDate, startMinute: config.eventStartMinute, endMinute: config.eventStartMinute + config.durationMinutes };
                cleanup(surface, session);
                await dotNet.invokeMethodAsync("CompleteBrowserInteraction", result);
            };
            session.up = event => session.finish(event, false);
            session.cancel = event => session.finish(event, true);
            active.set(surface, session);
            window.addEventListener("pointermove", session.move, { capture: true, passive: false });
            window.addEventListener("pointerup", session.up, true);
            window.addEventListener("pointercancel", session.cancel, true);
        }
    };
})();
