using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class AppearancePreferenceStoreTests
{
    [Fact]
    public async Task Store_ReadsAndSavesAppearancePreference()
    {
        var module = new FakeJsModule { StoredAppearance = "Ocean|Dark|Studio" };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbAppearancePreferenceStore(connection);

        var preference = await store.GetAsync();
        await store.SaveAsync(new AppearancePreference(
            ThemeColorId.Sage,
            AppearanceMode.Light,
            TypographyTheme.Editorial));

        Assert.Equal(
            new AppearancePreference(ThemeColorId.Ocean, AppearanceMode.Dark, TypographyTheme.Studio),
            preference);
        Assert.Equal("Sage", module.SavedThemeColor);
        Assert.Equal("Light", module.SavedMode);
        Assert.Equal("Editorial", module.SavedTypography);
    }

    [Theory]
    [InlineData("FutureTheme|Dark|Studio", ThemeColorId.Sage, AppearanceMode.Dark, TypographyTheme.Studio)]
    [InlineData("|Light|Gallery", ThemeColorId.Sage, AppearanceMode.Light, TypographyTheme.Gallery)]
    [InlineData("Ocean|FutureMode|Editorial", ThemeColorId.Ocean, AppearanceMode.System, TypographyTheme.Editorial)]
    [InlineData("Ocean|Dark|FutureTypography", ThemeColorId.Ocean, AppearanceMode.Dark, TypographyTheme.System)]
    [InlineData("Ocean|Light", ThemeColorId.Ocean, AppearanceMode.Light, TypographyTheme.System)]
    [InlineData("Cupertino|Dark", ThemeColorId.Cupertino, AppearanceMode.Dark, TypographyTheme.System)]
    public async Task Store_FallsBackInvalidFieldsIndependently(
        string storedValue,
        ThemeColorId expectedTheme,
        AppearanceMode expectedMode,
        TypographyTheme expectedTypography)
    {
        var module = new FakeJsModule { StoredAppearance = storedValue };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbAppearancePreferenceStore(connection);

        Assert.Equal(
            new AppearancePreference(expectedTheme, expectedMode, expectedTypography),
            await store.GetAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Store_TreatsMissingPreferenceAsUnsaved(string storedValue)
    {
        var module = new FakeJsModule { StoredAppearance = storedValue };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbAppearancePreferenceStore(connection);

        Assert.Null(await store.GetAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Store_WrapsInteropAndSerializationFailures(bool serializationFailure)
    {
        var module = new FakeJsModule
        {
            Failure = serializationFailure
                ? new JsonException("损坏的外观偏好")
                : new JSException("IndexedDB 请求失败")
        };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbAppearancePreferenceStore(connection);

        var exception = await Assert.ThrowsAsync<EventRepositoryException>(() => store.GetAsync());

        Assert.Contains("读取外观偏好失败", exception.Message);
        Assert.Same(module.Failure, exception.InnerException);
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ValueTask.FromResult((TValue)module);
    }

    private sealed class FakeJsModule : IJSObjectReference
    {
        public string? StoredAppearance { get; init; }
        public string? SavedThemeColor { get; private set; }
        public string? SavedMode { get; private set; }
        public string? SavedTypography { get; private set; }
        public Exception? Failure { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier != "initialize" && Failure is not null)
            {
                return ValueTask.FromException<TValue>(Failure);
            }

            object? result = identifier switch
            {
                "getAppearancePreference" => StoredAppearance,
                "saveAppearancePreference" => Save(args),
                _ => default(TValue)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private object? Save(object?[]? arguments)
        {
            SavedThemeColor = Assert.IsType<string>(arguments![0]);
            SavedMode = Assert.IsType<string>(arguments[1]);
            SavedTypography = Assert.IsType<string>(arguments[2]);
            return null;
        }
    }
}
