using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class AppearancePreferenceStoreTests
{
    [Fact]
    public async Task Store_ReadsAndSavesModeOnlyPreference()
    {
        var module = new FakeJsModule { StoredAppearance = "Dark" };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbAppearancePreferenceStore(connection);

        Assert.Equal(new AppearancePreference(AppearanceMode.Dark), await store.GetAsync());

        await store.SaveAsync(new AppearancePreference(AppearanceMode.Light));

        Assert.Equal("Light", module.SavedMode);
    }

    [Theory]
    [InlineData("Ocean|Dark|Studio", AppearanceMode.Dark)]
    [InlineData("Sage|Light|System", AppearanceMode.Light)]
    [InlineData("FutureTheme|FutureMode|Studio", AppearanceMode.System)]
    [InlineData("Dark", AppearanceMode.Dark)]
    public async Task Store_MigratesLegacyFormatAndFallsBackInvalidMode(
        string storedValue,
        AppearanceMode expectedMode)
    {
        var module = new FakeJsModule { StoredAppearance = storedValue };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbAppearancePreferenceStore(connection);

        Assert.Equal(new AppearancePreference(expectedMode), await store.GetAsync());
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
        public string? SavedMode { get; private set; }
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
            var values = Assert.IsType<object?[]>(arguments);
            Assert.Single(values);
            SavedMode = Assert.IsType<string>(values[0]);
            return null;
        }
    }
}
