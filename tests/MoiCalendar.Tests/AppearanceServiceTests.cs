using Microsoft.JSInterop;
using MoiCalendar.App;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class AppearanceServiceTests
{
    [Fact]
    public void DefaultPreference_IsSageFollowingSystemWithSystemTypography()
    {
        Assert.Equal(ThemeColorId.Sage, AppearancePreference.Default.ThemeColor);
        Assert.Equal(AppearanceMode.System, AppearancePreference.Default.Mode);
        Assert.Equal(TypographyTheme.System, AppearancePreference.Default.Typography);
    }

    [Fact]
    public void TypographyCatalog_ContainsExactlyFourCuratedOptions()
    {
        Assert.Equal(
            [TypographyTheme.System, TypographyTheme.Gallery, TypographyTheme.Editorial, TypographyTheme.Studio],
            TypographyCatalog.All.Select(typography => typography.Id));
    }

    [Fact]
    public void Catalog_ContainsSevenCuratedThemesWithEightCalendarColors()
    {
        Assert.Equal(
            [ThemeColorId.Cupertino, ThemeColorId.Ocean, ThemeColorId.Sage, ThemeColorId.Lavender,
             ThemeColorId.Sunset, ThemeColorId.Rose, ThemeColorId.Graphite],
            ThemeColorCatalog.All.Select(theme => theme.Id));
        Assert.All(ThemeColorCatalog.All, theme => Assert.Equal(8, theme.CalendarColors.Count));
    }

    [Fact]
    public void Stylesheet_DeclaresEveryThemeAndKeepsSemanticStatusColorsSeparate()
    {
        var stylesheet = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "css",
            "app.css"));

        Assert.Contains("--color-accent:", stylesheet, StringComparison.Ordinal);
        Assert.Contains("--color-selection:", stylesheet, StringComparison.Ordinal);
        Assert.Contains("--color-today:", stylesheet, StringComparison.Ordinal);
        Assert.Contains("--destructive: #d70015", stylesheet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--success: #248a3d", stylesheet, StringComparison.OrdinalIgnoreCase);

        foreach (var theme in ThemeColorCatalog.All)
        {
            Assert.Contains(
                $"data-color-theme=\"{theme.CssId}\"",
                stylesheet,
                StringComparison.Ordinal);
            Assert.All(
                theme.CalendarColors,
                color => Assert.Contains(color, stylesheet, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TypographyStylesheet_UsesSemanticTokensAndSelfHostedSwapFonts()
    {
        var root = FindRepositoryRoot();
        var stylesheet = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "css",
            "typography.css"));
        var appStylesheet = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "css",
            "app.css"));

        foreach (var token in new[]
                 {
                     "--font-display", "--font-ui", "--font-body", "--font-calendar", "--font-mono",
                     "--font-weight-regular", "--font-weight-medium", "--font-weight-semibold"
                 })
        {
            Assert.Contains(token, stylesheet, StringComparison.Ordinal);
        }

        foreach (var typography in TypographyCatalog.All)
        {
            Assert.Contains(
                $"data-typography=\"{typography.CssId}\"",
                stylesheet,
                StringComparison.Ordinal);
        }

        Assert.Contains("font-display: swap", stylesheet, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", stylesheet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("font-family: system-ui", appStylesheet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("font-family: var(--font-calendar)", appStylesheet, StringComparison.Ordinal);
        Assert.Contains("font-family: var(--font-display)", appStylesheet, StringComparison.Ordinal);

        var fontsDirectory = Path.Combine(root, "src", "MoiCalendar.App", "wwwroot", "fonts");
        Assert.Equal(18, Directory.GetFiles(fontsDirectory, "*.woff2").Length);
        Assert.Equal(6, Directory.GetFiles(fontsDirectory, "LICENSE-*.txt").Length);

        var serviceWorker = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "service-worker.published.js"));
        Assert.Contains("runtimeFontPattern", serviceWorker, StringComparison.Ordinal);
        Assert.Contains("cache.put(event.request, networkResponse.clone())", serviceWorker, StringComparison.Ordinal);
        var precacheLine = serviceWorker.Split('\n')
            .Single(line => line.StartsWith("const offlineAssetsInclude", StringComparison.Ordinal));
        Assert.DoesNotContain("woff2", precacheLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitialDocument_UsesSageSystemDefaultsBeforePreferencesLoad()
    {
        var index = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.App",
            "wwwroot",
            "index.html"));

        Assert.Contains("data-color-theme=\"sage\"", index, StringComparison.Ordinal);
        Assert.Contains("data-appearance=\"system\"", index, StringComparison.Ordinal);
        Assert.Contains("data-typography=\"system\"", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_NewUserReceivesSageAndSystemTypography()
    {
        var js = new RecordingJsRuntime();
        var service = new AppearanceService(new FakePreferenceStore(), js);

        await service.InitializeAsync();

        Assert.Equal(AppearancePreference.Default, service.Current);
        Assert.Equal(("sage", "system", "system"), js.LastAppearance);
    }

    [Fact]
    public async Task Initialize_RestoresPreferenceAndAppliesRootAttributes()
    {
        var store = new FakePreferenceStore
        {
            Stored = new AppearancePreference(
                ThemeColorId.Lavender,
                AppearanceMode.Dark,
                TypographyTheme.Editorial)
        };
        var js = new RecordingJsRuntime();
        var service = new AppearanceService(store, js);

        await service.InitializeAsync();

        Assert.Equal(store.Stored, service.Current);
        Assert.Equal(("lavender", "dark", "editorial"), js.LastAppearance);
    }

    [Fact]
    public async Task ThemeModeAndTypography_ChangeIndependentlyAndPersistImmediately()
    {
        var store = new FakePreferenceStore();
        var js = new RecordingJsRuntime();
        var service = new AppearanceService(store, js);
        await service.InitializeAsync();

        await service.SetThemeColorAsync(ThemeColorId.Ocean);
        Assert.Equal(
            new AppearancePreference(ThemeColorId.Ocean, AppearanceMode.System, TypographyTheme.System),
            store.Stored);
        Assert.Equal(("ocean", "system", "system"), js.LastAppearance);

        await service.SetModeAsync(AppearanceMode.Light);
        Assert.Equal(
            new AppearancePreference(ThemeColorId.Ocean, AppearanceMode.Light, TypographyTheme.System),
            store.Stored);
        Assert.Equal(("ocean", "light", "system"), js.LastAppearance);

        await service.SetTypographyAsync(TypographyTheme.Studio);
        Assert.Equal(
            new AppearancePreference(ThemeColorId.Ocean, AppearanceMode.Light, TypographyTheme.Studio),
            store.Stored);
        Assert.Equal(("ocean", "light", "studio"), js.LastAppearance);
        Assert.Equal(3, store.SaveCount);
    }

    [Fact]
    public async Task NewService_RestoresPreviouslySavedThemeWithoutCalendarOrSyncDependencies()
    {
        var store = new FakePreferenceStore();
        var first = new AppearanceService(store, new RecordingJsRuntime());
        await first.InitializeAsync();
        await first.SetThemeColorAsync(ThemeColorId.Cupertino);
        await first.SetTypographyAsync(TypographyTheme.Gallery);

        var secondJs = new RecordingJsRuntime();
        var second = new AppearanceService(store, secondJs);
        await second.InitializeAsync();

        Assert.Equal(ThemeColorId.Cupertino, second.Current.ThemeColor);
        Assert.Equal(TypographyTheme.Gallery, second.Current.Typography);
        Assert.Equal(("cupertino", "system", "gallery"), secondJs.LastAppearance);
    }

    private sealed class FakePreferenceStore : IAppearancePreferenceStore
    {
        public AppearancePreference? Stored { get; set; }
        public int SaveCount { get; private set; }

        public Task<AppearancePreference?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Stored);

        public Task SaveAsync(
            AppearancePreference preference,
            CancellationToken cancellationToken = default)
        {
            Stored = preference;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public (string Theme, string Mode, string Typography)? LastAppearance { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("moicalendarUi.applyAppearance", identifier);
            LastAppearance = (
                Assert.IsType<string>(args![0]),
                Assert.IsType<string>(args[1]),
                Assert.IsType<string>(args[2]));
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MoiCalendar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
