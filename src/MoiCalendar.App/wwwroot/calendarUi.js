window.moicalendarUi = {
    scrollToSection: (id) => {
        // Route content can finish loading after its first render.
        const findSection = (attempt) => {
            const target = document.getElementById(id);
            if (target) {
                target.scrollIntoView({ block: "start", behavior: "instant" });
                if (!target.hasAttribute("tabindex")) target.setAttribute("tabindex", "-1");
                target.focus({ preventScroll: true });
            } else if (attempt < 120) {
                requestAnimationFrame(() => findSection(attempt + 1));
            }
        };
        findSection(0);
    },
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
        const getPanels = () => Array.from(stream.querySelectorAll(":scope > .month-panel"));
        const panels = getPanels();
        if (panels.length !== 5) return;
        const panelTop = panel => panel.getBoundingClientRect().top - stream.getBoundingClientRect().top + stream.scrollTop;

        let frame = 0;
        let changing = true;
        let pendingAnchor = null;
        let disposed = false;
        let reportedMonth = panels[2].dataset.month;
        requestAnimationFrame(() => {
            stream.scrollTop = panelTop(panels[2]);
            changing = false;
        });

        const handler = () => {
            if (disposed) return;
            if (pendingAnchor) pendingAnchor.scrollTop = stream.scrollTop;
            if (frame || changing) return;
            frame = requestAnimationFrame(async () => {
                frame = 0;
                const currentPanels = getPanels();
                if (currentPanels.length !== 5) return;
                const top = stream.scrollTop;
                const viewportCenter = top + stream.clientHeight / 2;
                const positions = currentPanels.map(panel => ({ panel, top: panelTop(panel), height: panel.offsetHeight }));
                const displayedPanel = positions.reduce((nearest, position) =>
                    Math.abs(position.top + position.height / 2 - viewportCenter) <
                    Math.abs(nearest.top + nearest.height / 2 - viewportCenter)
                        ? position
                        : nearest).panel;
                if (displayedPanel.dataset.month !== reportedMonth) {
                    reportedMonth = displayedPanel.dataset.month;
                    const [year, month] = reportedMonth.split("-").map(Number);
                    void dotnetReference.invokeMethodAsync("SetDisplayedMonth", year, month);
                }
                const previousBoundary = panelTop(currentPanels[1]);
                const nextBoundary = panelTop(currentPanels[3]);
                const targetIndex = top <= previousBoundary + 2
                    ? 1
                    : top >= nextBoundary - 2 ? 3 : 2;
                if (targetIndex === 2) return;
                changing = true;
                const anchorMonth = currentPanels[targetIndex].dataset.month;
                pendingAnchor = { month: anchorMonth, contentTop: positions[targetIndex].top, scrollTop: top };
                try {
                    await dotnetReference.invokeMethodAsync("ChangeVisibleMonth", targetIndex === 1 ? -1 : 1);
                    preserveAnchor();
                } catch {
                    pendingAnchor = null;
                    changing = false;
                }
            });
        };
        // Blazor 更新月份节点后，在同一帧绘制前补偿高度变化。
        // 加载期间继续记录滚动位置，避免把用户拉回加载前的位置。
        const preserveAnchor = () => {
            if (disposed || !pendingAnchor) return;
            const updatedPanels = getPanels();
            if (updatedPanels[2]?.dataset.month !== pendingAnchor.month) return;
            const anchor = updatedPanels[2];
            const expectedViewportTop = pendingAnchor.contentTop - pendingAnchor.scrollTop;
            stream.scrollTop = panelTop(anchor) - expectedViewportTop;
            pendingAnchor = null;
            changing = false;
            handler();
        };
        const observer = new MutationObserver(preserveAnchor);
        observer.observe(stream, { childList: true });
        stream._moicalendarMonthStreamDispose = () => {
            disposed = true;
            observer.disconnect();
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
        stream._moicalendarMonthStreamDispose?.();
        delete stream._moicalendarMonthStreamDispose;
        delete stream._moicalendarMonthStreamHandler;
        delete stream._moicalendarMonthStreamFrame;
    }
};
