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
        window.removeEventListener("touchmove", session.touchMove, true);
        if (session.frame) cancelAnimationFrame(session.frame);
        session.mirror?.remove();
        session.preview?.remove();
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

    function createPreview(config) {
        const preview = document.createElement("div");
        preview.className = "calendar-interaction-preview";
        preview.setAttribute("aria-hidden", "true");
        preview.style.setProperty("--event-left", "0%");
        preview.style.setProperty("--event-width", "100%");
        const title = document.createElement("strong");
        title.textContent = config.label;
        const time = document.createElement("span");
        preview.append(title, time);
        return { preview, time };
    }

    function calculate(config, grid, scroll, x, y) {
        const bounds = grid.getBoundingClientRect();
        const index = clamp(Math.floor((x - bounds.left) / Math.max(1, bounds.width) * config.dates.length), 0, config.dates.length - 1);
        const contentY = y - bounds.top;
        const minuteRange = config.visibleEndMinute - config.visibleStartMinute;
        const pointerMinute = clamp(snap(config.visibleStartMinute + contentY / Math.max(1, grid.scrollHeight) * minuteRange), config.visibleStartMinute, config.visibleEndMinute);
        let startMinute;
        let endMinute;

        if (config.kind === "resizestart") {
            endMinute = config.eventStartMinute + config.durationMinutes;
            startMinute = clamp(pointerMinute, config.visibleStartMinute, endMinute - snapMinutes);
        } else if (config.kind === "resize") {
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

        return { cancelled: false, targetDate: config.kind === "resize" || config.kind === "resizestart" ? config.originalDate : config.dates[index], startMinute, endMinute };
    }

    function formatMinute(value) {
        const normalized = Math.max(0, value);
        return `${String(Math.floor(normalized / 60)).padStart(2, "0")}:${String(normalized % 60).padStart(2, "0")}`;
    }

    window.moicalendarInteraction = {
        getMonthDateAtPoint: (element, x, y) => {
            const cell = document.elementFromPoint(x, y)?.closest?.(".calendar-date");
            return cell && element.contains(cell) ? cell.getAttribute("aria-label") : null;
        },
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
            if (surface.classList.contains("is-period-paging")) return;
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
                preview: null,
                previewTime: null,
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
                const targetIndex = interactionConfig.dates.indexOf(session.result.targetDate);
                const targetDay = grid.children[targetIndex];
                if (targetDay && session.preview) {
                    if (session.preview.parentElement !== targetDay) targetDay.append(session.preview);
                    const range = interactionConfig.visibleEndMinute - interactionConfig.visibleStartMinute;
                    session.preview.style.setProperty("--event-top", `${(session.result.startMinute - interactionConfig.visibleStartMinute) / range * 100}%`);
                    session.preview.style.setProperty("--event-height", `${(session.result.endMinute - session.result.startMinute) / range * 100}%`);
                    session.previewTime.textContent = `${formatMinute(session.result.startMinute)}–${formatMinute(session.result.endMinute)}`;
                }
            };

            session.move = event => {
                if (event.pointerId !== session.pointerId) return;
                const dx = event.clientX - config.startX;
                const dy = event.clientY - config.startY;
                if (!session.active) {
                    if (dx * dx + dy * dy < movementThreshold * movementThreshold) return;
                    if (config.pointerType === "touch" && performance.now() - session.startedAt < touchHoldMilliseconds) {
                        // 一旦用户开始滑动就放弃长按识别，不能在同一次滚动中途突然拖动事件。
                        void session.finish(event, true);
                        return;
                    }
                    session.active = true;
                    const created = createMirror(config);
                    session.mirror = created.mirror;
                    session.time = created.time;
                    const preview = createPreview(config);
                    session.preview = preview.preview;
                    session.previewTime = preview.time;
                    session.source?.classList.add("interaction-source");
                    surface.classList.add("is-browser-interacting");
                    surface.setPointerCapture?.(config.pointerId);
                }
                event.preventDefault();
                session.latest = { x: event.clientX, y: event.clientY };
                if (!session.frame) session.frame = requestAnimationFrame(update);
            };

            session.finish = async (event, cancelled) => {
                if (event.pointerId !== session.pointerId || session.finished) return;
                session.finished = true;
                if (!session.active) {
                    cleanup(surface, session);
                    await dotNet.invokeMethodAsync("CompleteBrowserInteraction", {
                        cancelled: true,
                        targetDate: config.originalDate,
                        startMinute: config.eventStartMinute,
                        endMinute: config.eventStartMinute + config.durationMinutes
                    });
                    return;
                }
                if (session.frame) {
                    cancelAnimationFrame(session.frame);
                    session.frame = 0;
                }
                // 松手位置可能晚于最后一个 pointermove/动画帧；只提交最终位置一次。
                if (!cancelled) {
                    session.latest = { x: event.clientX, y: event.clientY };
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
            // touch-action 允许正常滑动；仅已识别的单指长按拖拽拦截原生触摸滚动。
            session.touchMove = event => {
                if (config.pointerType === "touch" && session.active && event.touches.length === 1) {
                    event.preventDefault();
                }
            };
            active.set(surface, session);
            window.addEventListener("pointermove", session.move, { capture: true, passive: false });
            window.addEventListener("pointerup", session.up, true);
            window.addEventListener("pointercancel", session.cancel, true);
            if (config.pointerType === "touch") {
                window.addEventListener("touchmove", session.touchMove, { capture: true, passive: false });
            }
        }
    };
})();
