using Microsoft.JSInterop;
using MoiCalendar.App;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class AppearanceServiceTests
{
    [Fact]
    public void DefaultPreference_FollowsSystem() =>
        Assert.Equal(AppearanceMode.System, AppearancePreference.Default.Mode);

    [Fact]
    public void InitialDocument_UsesSystemAppearanceAndSingleV4Stylesheet()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(
            root, "src", "MoiCalendar.App", "wwwroot", "index.html"));

        Assert.Contains("data-appearance=\"system\"", index, StringComparison.Ordinal);
        Assert.Contains("css/v4.css", index, StringComparison.Ordinal);
        Assert.DoesNotContain("css/app.css", index, StringComparison.Ordinal);
        Assert.DoesNotContain("css/typography.css", index, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            root, "src", "MoiCalendar.App", "wwwroot", "css", "app.css")));
        Assert.False(File.Exists(Path.Combine(
            root, "src", "MoiCalendar.App", "wwwroot", "css", "typography.css")));
    }

    [Fact]
    public async Task Initialize_NewUserAppliesSystemPreference()
    {
        var js = new RecordingJsRuntime();
        var service = new AppearanceService(new FakePreferenceStore(), js);

        await service.InitializeAsync();

        Assert.Equal(AppearancePreference.Default, service.Current);
        Assert.Equal("system", js.LastAppearance);
    }

    [Fact]
    public async Task ModeChange_AppliesAndPersistsImmediately()
    {
        var store = new FakePreferenceStore
        {
            Stored = new AppearancePreference(AppearanceMode.Dark)
        };
        var js = new RecordingJsRuntime();
        var service = new AppearanceService(store, js);
        await service.InitializeAsync();

        Assert.Equal("dark", js.LastAppearance);

        await service.SetModeAsync(AppearanceMode.Light);

        Assert.Equal(new AppearancePreference(AppearanceMode.Light), store.Stored);
        Assert.Equal("light", js.LastAppearance);
        Assert.Equal(1, store.SaveCount);
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
        public string? LastAppearance { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("moicalendarUi.applyAppearance", identifier);
            var arguments = Assert.IsType<object?[]>(args);
            Assert.Single(arguments);
            LastAppearance = Assert.IsType<string>(arguments[0]);
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
