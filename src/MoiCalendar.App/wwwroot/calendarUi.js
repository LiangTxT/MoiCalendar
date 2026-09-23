window.moicalendarUi = {
    rememberedFocus: [],
    shortcutHandler: null,
    isWideViewport: () => window.matchMedia("(min-width: 56rem)").matches,
    applyAppearance: (colorTheme, appearance, typography) => {
        const root = document.documentElement;
        root.dataset.colorTheme = colorTheme;
        root.dataset.appearance = appearance;
        root.dataset.typography = typography;

        const systemIsDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
        const isDark = appearance === "dark" || (appearance === "system" && systemIsDark);
        const themeColor = document.querySelector('meta[name="theme-color"]');
        if (themeColor) {
            themeColor.content = isDark ? "#1c1c1e" : "#f5f5f7";
        }
    },
    scrollCalendarToActiveHours: () => {
        const timeline = document.querySelector(".week-timed-scroll");
        if (!timeline) {
            return;
        }

        const hour = new Date().getHours();
        timeline.scrollTop = Math.max(0, (Math.min(18, Math.max(7, hour)) - 2) * 48);
    },
    getElementBounds: element => {
        const rect = element.getBoundingClientRect();
        return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
    },
    capturePointer: (element, pointerId) => {
        if (element?.setPointerCapture) {
            element.setPointerCapture(pointerId);
        }
    },
    releasePointer: (element, pointerId) => {
        if (element?.hasPointerCapture?.(pointerId)) {
            element.releasePointerCapture(pointerId);
        }
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

        const supported = new Set(["n", "t", "m", "w", "d", "a", "arrowleft", "arrowright", "escape", "enter", "delete"]);
        window.moicalendarUi.shortcutHandler = event => {
            const target = event.target;
            const tag = target?.tagName?.toLowerCase();
            const isTextEntry = tag === "input" || tag === "textarea" || tag === "select" || target?.isContentEditable === true;
            const key = event.key.toLowerCase();
            if (isTextEntry || event.ctrlKey || event.altKey || event.metaKey || !supported.has(key)) {
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
    }
};
