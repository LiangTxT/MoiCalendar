using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarViewPreferenceStoreTests
{
    [Fact]
    public async Task Store_ReadsAndSavesCalendarViewPreference()
    {
        var module = new FakeJsModule { StoredView = "Week" };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbCalendarViewPreferenceStore(connection);

        var viewMode = await store.GetAsync();
        await store.SaveAsync(CalendarViewMode.Agenda);

        Assert.Equal(CalendarViewMode.Week, viewMode);
        Assert.Equal("Agenda", module.SavedView);
    }

    [Fact]
    public async Task Store_IgnoresUnknownStoredValue()
    {
        var module = new FakeJsModule { StoredView = "FutureView" };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbCalendarViewPreferenceStore(connection);

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
                ? new JsonException("损坏的视图偏好")
                : new JSException("IndexedDB 请求失败")
        };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var store = new IndexedDbCalendarViewPreferenceStore(connection);

        var exception = await Assert.ThrowsAsync<EventRepositoryException>(() => store.GetAsync());

        Assert.Contains("读取日历视图偏好失败", exception.Message);
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
        public string? StoredView { get; init; }

        public string? SavedView { get; private set; }

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
                "getCalendarViewPreference" => StoredView,
                "saveCalendarViewPreference" => Save(args),
                _ => default(TValue)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private object? Save(object?[]? arguments)
        {
            SavedView = Assert.IsType<string>(arguments![0]);
            return null;
        }
    }
}
