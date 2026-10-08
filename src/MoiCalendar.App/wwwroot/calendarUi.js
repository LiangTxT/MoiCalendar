// 原生滚动期间不得回收列表或写 scrollTop，否则 WebKit 的惯性滚动会被打断。
function observeCalendarScrollActivity(root, onIdle = () => {}) {
    let timer = 0, touching = false, disposed = false;
    const waiters = [];
    const schedule = () => {
        clearTimeout(timer);
        timer = setTimeout(() => {
            timer = 0;
            if (disposed || touching) return;
            for (const resolve of waiters.splice(0)) resolve();
            onIdle();
        }, 180);
    };
    const start = () => { touching = true; schedule(); };
    const end = event => { touching = (event?.touches?.length ?? 0) > 0; schedule(); };
    root.addEventListener('scroll', schedule, { passive: true });
    root.addEventListener('wheel', schedule, { passive: true });
    root.addEventListener('touchstart', start, { passive: true });
    root.addEventListener('touchend', end, { passive: true });
    root.addEventListener('touchcancel', end, { passive: true });
    return {
        busy: () => touching || timer !== 0,
        whenIdle: () => disposed || (!touching && !timer) ? Promise.resolve() : new Promise(resolve => waiters.push(resolve)),
        dispose() {
            disposed = true; clearTimeout(timer);
            root.removeEventListener('scroll', schedule);
            root.removeEventListener('wheel', schedule);
            root.removeEventListener('touchstart', start);
            root.removeEventListener('touchend', end);
            root.removeEventListener('touchcancel', end);
            for (const resolve of waiters.splice(0)) resolve();
        }
    };
}

// 尺寸变化时保留可见内容，而不是保留旧像素位置。普通滚动只记录数值，
// 不逐帧测量整个列表；横竖屏切换也必须等原生滚动结束后再补偿。
function observeCalendarResizeAnchor(root, selector, activity) {
    if (typeof ResizeObserver === 'undefined') return { refresh() {}, dispose() {} };
    let snapshot = null, disposed = false, version = 0;
    const dimensionsMatch = () => snapshot && snapshot.width === root.clientWidth && snapshot.height === root.clientHeight;
    const measure = () => {
        const viewportTop = root.getBoundingClientRect().top;
        snapshot = {
            width: root.clientWidth, height: root.clientHeight, top: root.scrollTop,
            items: [...root.querySelectorAll(selector)].map(node => {
                const rect = node.getBoundingClientRect();
                return { node, top: rect.top - viewportTop + root.scrollTop, height: rect.height };
            })
        };
    };
    const refresh = () => {
        // DOM 更新和 resize 同时发生时，不覆盖旋转前的锚点。
        if (!disposed && (!snapshot || dimensionsMatch())) measure();
    };
    const remember = () => {
        // 浏览器可能在 resize 后先派发被钳制的 scroll；不要记成用户滚动。
        if (dimensionsMatch()) snapshot.top = root.scrollTop;
    };
    measure();
    const observer = new ResizeObserver(async () => {
        if (disposed || dimensionsMatch()) return;
        const previous = snapshot, request = ++version;
        await activity.whenIdle();
        if (disposed || request !== version) return;
        const anchor = previous.items.find(item => item.node.isConnected && item.height > 0 && item.top + item.height > previous.top + 1);
        if (anchor) {
            const rect = anchor.node.getBoundingClientRect();
            const contentTop = rect.top - root.getBoundingClientRect().top + root.scrollTop;
            root.scrollTop = contentTop + (previous.top - anchor.top) / anchor.height * rect.height;
        }
        measure();
    });
    observer.observe(root);
    root.addEventListener('scroll', remember, { passive: true });
    return {
        refresh,
        dispose() {
            disposed = true; ++version;
            observer.disconnect(); root.removeEventListener('scroll', remember);
        }
    };
}

window.moicalendarUi = {
    observeScrollActivity: observeCalendarScrollActivity,
    observeResizeAnchor: observeCalendarResizeAnchor,
    waitForScrollIdle: root => root?._moicalendarScrollActivity?.whenIdle() ?? Promise.resolve(),
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
    initializeCalendarScroll: (timeline, contextKey, initialMinute, visibleStartMinute, visibleEndMinute, selectedDayIndex = 0) => {
        if (!timeline || visibleEndMinute <= visibleStartMinute) {
            return;
        }

        const frame = timeline.closest(".week-grid-frame");
        if (frame) {
            // offsetWidth/clientWidth 会取整，缩放时累积到最后一列；使用真实内容边界。
            const contentWidth = timeline.querySelector?.(".week-timed-layout")?.getBoundingClientRect().width;
            const viewportWidth = timeline.getBoundingClientRect?.().width;
            const scrollbarWidth = Math.max(0,
                Number.isFinite(contentWidth) && Number.isFinite(viewportWidth)
                    ? viewportWidth - contentWidth
                    : timeline.offsetWidth - timeline.clientWidth);
            frame.style.setProperty("--time-grid-scrollbar-width", `${scrollbarWidth}px`);
        }

        window.moicalendarUi.disposeCalendarScroll(timeline);
        window.moicalendarZoom?.attach(timeline);

        const positions = window.moicalendarUi.calendarScrollPositions;
        const remembered = positions[contextKey];
        if (Number.isFinite(remembered?.top)) {
            timeline.scrollTop = remembered.top;
        } else {
            const relative = Math.max(
                0,
                Math.min(1, (initialMinute - visibleStartMinute) / (visibleEndMinute - visibleStartMinute)));
            // 顶部留白不是时间；以真实日期网格测量初始时刻，不能把它摊进 24 小时。
            const timeGrid = timeline.querySelector?.(".week-timed-days");
            const gridHeight = timeGrid?.clientHeight ?? timeline.scrollHeight;
            const gridTop = timeGrid
                ? timeGrid.getBoundingClientRect().top - timeline.getBoundingClientRect().top
                    + timeline.scrollTop - (timeline.clientTop ?? 0)
                : 0;
            timeline.scrollTop = Math.max(0, gridTop + relative * gridHeight - timeline.clientHeight * 0.25);
        }

        const horizontal = timeline.closest(".week-horizontal-scroll");
        if (horizontal) {
            if (Number.isFinite(remembered?.left)) {
                horizontal.scrollLeft = remembered.left;
            } else {
                // 日期选择由 .NET 决定；这里只测量目标列，首次进入时让它出现在窄屏内。
                const heading = frame?.querySelectorAll(".week-day-heading")[selectedDayIndex];
                if (heading && horizontal.scrollWidth > horizontal.clientWidth) {
                    const target = heading.getBoundingClientRect();
                    const viewport = horizontal.getBoundingClientRect();
                    horizontal.scrollLeft = Math.max(0, horizontal.scrollLeft + target.left - viewport.left
                        - (horizontal.clientWidth - target.width) / 2);
                }
            }
        }
        const remember = () => {
            positions[contextKey] = { top: timeline.scrollTop, left: horizontal?.scrollLeft ?? 0 };
            frame?.style.setProperty("--time-grid-horizontal-offset", `${horizontal?.scrollLeft ?? 0}px`);
        };
        remember();
        timeline._moicalendarScrollDispose = () => {
            remember();
            timeline.removeEventListener("scroll", remember);
            horizontal?.removeEventListener("scroll", remember);
        };
        timeline.addEventListener("scroll", remember, { passive: true });
        horizontal?.addEventListener("scroll", remember, { passive: true });
    },
    disposeCalendarScroll: timeline => {
        window.moicalendarZoom?.dispose(timeline);
        timeline?._moicalendarScrollDispose?.();
        if (timeline) delete timeline._moicalendarScrollDispose;
    },
    rememberCalendarFocus: () => {
        window.moicalendarUi.rememberedFocus.push(document.activeElement);
    },
    restoreCalendarFocus: () => {
        const canRestore = element => element?.isConnected && typeof element.focus === "function"
            && !element.disabled && element.getClientRects().length > 0
            && getComputedStyle(element).visibility !== "hidden";
        const remembered = window.moicalendarUi.rememberedFocus;
        while (remembered.length > 0) {
            const element = remembered.pop();
            if (canRestore(element)) {
                element.focus({ preventScroll: true });
                return;
            }
        }
        // 月份虚拟窗口或视图切换可能移除原入口，不把键盘用户留在 body。
        const main = document.getElementById("main-content");
        if (canRestore(main)) {
            main.focus({ preventScroll: true });
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
            // Only the overlay host may close a dialog and restore its unsaved editor.
            if (key === "escape" && target?.closest?.(".calendar-overlay")) return;
            if (key === " " && !target?.closest?.("button, a") && target?.closest?.("[role='gridcell']")) {
                event.preventDefault(); // The grid owns Space; do not also scroll the page.
                return;
            }
            // Native button activation and the gridcell's own handler must retain Enter.
            if ((key === "enter" || key === " ") && target?.closest?.("button, a, [role='gridcell']")) return;
            const isSearchShortcut = key === "k" && !event.altKey && event.ctrlKey !== event.metaKey;
            if (target?.closest?.(".month-navigation-rail, .form-picker") || isTextEntry || (!isSearchShortcut && (event.ctrlKey || event.altKey || event.metaKey)) || !supported.has(key)) {
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
        if (panels.length < 5) return;
        const centerIndex = Math.floor(panels.length / 2);
        const panelTop = panel => panel.getBoundingClientRect().top - stream.getBoundingClientRect().top + stream.scrollTop;

        let frame = 0;
        let changing = true;
        let pendingAnchor = null;
        let disposed = false;
        let reportedMonth = panels[centerIndex].dataset.month;
        let reportVersion = 0;
        frame = requestAnimationFrame(() => {
            frame = 0;
            if (disposed) return;
            stream.scrollTop = panelTop(panels[centerIndex]);
            changing = false;
            resizeAnchor.refresh();
        });

        const handler = () => {
            if (disposed) return;
            if (pendingAnchor) pendingAnchor.scrollTop = stream.scrollTop;
            if (frame || changing || activity.busy()) return;
            frame = requestAnimationFrame(async () => {
                frame = 0;
                if (disposed || activity.busy()) return;
                const currentPanels = getPanels();
                if (currentPanels.length !== panels.length) return;
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
                    const requestedMonth = reportedMonth;
                    const requestVersion = ++reportVersion;
                    const [year, month] = requestedMonth.split("-").map(Number);
                    void dotnetReference.invokeMethodAsync("SetDisplayedMonth", year, month).catch(() => {
                        // 下次用户滚动时重试，不创建自动重试循环；旧失败不能撤销新报告。
                        if (!disposed && reportVersion === requestVersion) reportedMonth = null;
                    });
                }
                const previousBoundary = positions[centerIndex - 1].top;
                const nextBoundary = positions[centerIndex + 1].top;
                if (top > previousBoundary + 2 && top < nextBoundary - 2) return;
                const targetIndex = currentPanels.indexOf(displayedPanel);
                if (targetIndex === centerIndex) return;
                changing = true;
                const anchorMonth = currentPanels[targetIndex].dataset.month;
                pendingAnchor = { month: anchorMonth, contentTop: positions[targetIndex].top, scrollTop: top };
                try {
                    await dotnetReference.invokeMethodAsync("ChangeVisibleMonth", targetIndex - centerIndex);
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
            if (updatedPanels[centerIndex]?.dataset.month !== pendingAnchor.month) return;
            const anchor = updatedPanels[centerIndex];
            const expectedViewportTop = pendingAnchor.contentTop - pendingAnchor.scrollTop;
            stream.scrollTop = panelTop(anchor) - expectedViewportTop;
            pendingAnchor = null;
            changing = false;
            resizeAnchor.refresh();
            handler();
        };
        const activity = observeCalendarScrollActivity(stream, handler);
        const resizeAnchor = observeCalendarResizeAnchor(stream, ':scope > .month-panel .month-week-row', activity);
        stream._moicalendarScrollActivity = activity;
        const observer = new MutationObserver(preserveAnchor);
        observer.observe(stream, { childList: true });
        stream._moicalendarMonthStreamDispose = () => {
            disposed = true;
            observer.disconnect();
            resizeAnchor.dispose();
            activity.dispose();
            delete stream._moicalendarScrollActivity;
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
