window.moicalendarUi = {
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
    scrollWeekToActiveHours: () => {
        const timeline = document.querySelector(".week-timed-scroll");
        if (!timeline || window.matchMedia("(max-width: 42rem)").matches) {
            return;
        }

        const hour = new Date().getHours();
        timeline.scrollTop = Math.max(0, (Math.min(18, Math.max(7, hour)) - 2) * 48);
    }
};
