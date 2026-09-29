window.moicalendarUi = {
    rememberedFocus: [],
    calendarScrollPositions: {},
    shortcutHandler: null,
    isWideViewport: () => window.matchMedia("(min-width: 1181px)").matches,
    applyAppearance: (appearance) => {
        const root = document.documentElement;
        root.dataset.appearance = appearance;

        const systemIsDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
        const isDark = appearance === "dark" || (appearance === "system" && systemIsDark);
        const themeColor = document.querySelector('meta[name="theme-color"]');
        if (themeColor) {
            themeColor.content = isDark ? "#1c1c1e" : "#f5f5f7";
        }
    },
    initializeCalendarScroll: (timeline, contextKey, initialMinute, visibleStartMinute, visibleEndMinute) => {
        if (!timeline || visibleEndMinute <= visibleStartMinute) {
            return;
        }

        const frame = timeline.closest(".week-grid-frame");
        if (frame) {
            const scrollbarWidth = Math.max(0, timeline.offsetWidth - timeline.clientWidth);
            frame.style.setProperty("--time-grid-scrollbar-width", `${scrollbarWidth}px`);
        }

        if (timeline._moicalendarScrollHandler) {
            timeline.removeEventListener("scroll", timeline._moicalendarScrollHandler);
        }

        const positions = window.moicalendarUi.calendarScrollPositions;
        const remembered = positions[contextKey];
        if (Number.isFinite(remembered)) {
            timeline.scrollTop = remembered;
        } else {
            const relative = Math.max(
                0,
                Math.min(1, (initialMinute - visibleStartMinute) / (visibleEndMinute - visibleStartMinute)));
            timeline.scrollTop = Math.max(0, relative * timeline.scrollHeight - timeline.clientHeight * 0.25);
        }

        const handler = () => {
            positions[contextKey] = timeline.scrollTop;
        };
        timeline._moicalendarScrollHandler = handler;
        timeline.addEventListener("scroll", handler, { passive: true });
    },
    rememberCalendarFocus: () => {
        window.moicalendarUi.rememberedFocus.push(document.activeElement);
    },
    restoreCalendarFocus: () => {
        const element = window.moicalendarUi.rememberedFocus.pop();
        if (element?.isConnected && element.focus) {
            element.focus({ preventScroll: true });
        }
    },
    rememberCalendarFocusAndGetAnchor: () => {
        const element = document.activeElement;
        window.moicalendarUi.rememberedFocus.push(element);
        const rect = element?.getBoundingClientRect?.();
        if (!rect) {
            return null;
        }

        return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
    },
    initializeCalendarShortcuts: dotnetReference => {
        if (window.moicalendarUi.shortcutHandler) {
            document.removeEventListener("keydown", window.moicalendarUi.shortcutHandler);
        }

        const supported = new Set(["n", "t", "m", "w", "d", "a", "k", "arrowleft", "arrowright", "escape", "enter", "delete"]);
        window.moicalendarUi.shortcutHandler = event => {
            const target = event.target;
            const tag = target?.tagName?.toLowerCase();
            const isTextEntry = tag === "input" || tag === "textarea" || tag === "select" || target?.isContentEditable === true;
            const key = event.key.toLowerCase();
            const isSearchShortcut = key === "k" && !event.altKey && event.ctrlKey !== event.metaKey;
            if (isTextEntry || (!isSearchShortcut && (event.ctrlKey || event.altKey || event.metaKey)) || !supported.has(key)) {
                return;
            }

            event.preventDefault();
            dotnetReference.invokeMethodAsync(
                "HandleCalendarShortcut",
                event.key,
                isTextEntry,
                event.ctrlKey,
                event.altKey,
                event.metaKey);
        };
        document.addEventListener("keydown", window.moicalendarUi.shortcutHandler);
    },
    disposeCalendarShortcuts: () => {
        if (window.moicalendarUi.shortcutHandler) {
            document.removeEventListener("keydown", window.moicalendarUi.shortcutHandler);
            window.moicalendarUi.shortcutHandler = null;
        }
    },
    initializeMonthStream: (stream, dotnetReference) => {
        if (!stream) return;
        window.moicalendarUi.disposeMonthStream(stream);
        const panels = Array.from(stream.querySelectorAll(":scope > .month-panel"));
        if (panels.length !== 3) return;

        let frame = 0;
        let changing = false;
        let ignoreUntil = performance.now() + 300;
        const centerCurrentMonth = () => {
            stream.scrollTop = panels[1].offsetTop;
        };
        requestAnimationFrame(centerCurrentMonth);

        const handler = () => {
            if (frame || changing || performance.now() < ignoreUntil) return;
            frame = requestAnimationFrame(async () => {
                frame = 0;
                const center = stream.scrollTop + stream.clientHeight / 2;
                const distances = panels.map(panel =>
                    Math.abs(panel.offsetTop + panel.offsetHeight / 2 - center));
                const closestIndex = distances.indexOf(Math.min(...distances));
                if (closestIndex === 1) return;
                changing = true;
                await dotnetReference.invokeMethodAsync("ChangeVisibleMonth", closestIndex === 0 ? -1 : 1);
            });
        };
        stream._moicalendarMonthStreamHandler = handler;
        stream._moicalendarMonthStreamFrame = () => frame;
        stream.addEventListener("scroll", handler, { passive: true });
    },
    disposeMonthStream: stream => {
        if (!stream?._moicalendarMonthStreamHandler) return;
        stream.removeEventListener("scroll", stream._moicalendarMonthStreamHandler);
        const frame = stream._moicalendarMonthStreamFrame?.();
        if (frame) cancelAnimationFrame(frame);
        delete stream._moicalendarMonthStreamHandler;
        delete stream._moicalendarMonthStreamFrame;
    }
};
