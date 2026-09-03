using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class IndexedDbEventRepositoryTests
{
    [Fact]
    public async Task Repository_InitializesModuleOnceAcrossOperations()
    {
        var module = new FakeJsModule();
        var jsRuntime = new FakeJsRuntime(module);
        await using var connection = new IndexedDbConnection(jsRuntime);
        var repository = new IndexedDbEventRepository(connection);
        var calendarEvent = CreateEvent();

        var created = await repository.CreateAsync(calendarEvent);
        var events = await repository.GetByRangeAsync(
            calendarEvent.StartUtc.AddDays(-1),
            calendarEvent.EndUtc.AddDays(1));

        Assert.Equal(calendarEvent, created);
        Assert.Empty(events);
        Assert.Equal(1, module.InitializationCount);
        Assert.Equal(1, jsRuntime.ImportCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Repository_ConvertsInteropAndSerializationFailuresToRepositoryError(bool serializationFailure)
    {
        var module = new FakeJsModule
        {
            Failure = serializationFailure
                ? new JsonException("损坏的事件 JSON")
                : new JSException("IndexedDB 请求失败")
        };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbEventRepository(connection);

        var exception = await Assert.ThrowsAsync<EventRepositoryException>(
            () => repository.GetByIdAsync(Guid.NewGuid()));

        Assert.Contains("读取事件失败", exception.Message);
        Assert.Same(module.Failure, exception.InnerException);
    }

    [Fact]
    public async Task Repository_ReadsAllEventsIncludingDeletionMarkers()
    {
        var active = CreateEvent();
        var deleted = CreateEvent() with
        {
            Id = Guid.NewGuid(),
            DeletedAtUtc = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero)
        };
        var module = new FakeJsModule { AllEvents = [active, deleted] };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbEventRepository(connection);

        var events = await repository.GetAllIncludingDeletedAsync();

        Assert.Equal([active, deleted], events);
    }

    [Fact]
    public async Task Repository_ReadsRecurringMastersThroughDedicatedQuery()
    {
        var recurring = CreateEvent() with { RecurrenceRule = "FREQ=DAILY" };
        var module = new FakeJsModule { RecurringEvents = [recurring] };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbEventRepository(connection);

        var events = await repository.GetRecurringMastersAsync();

        Assert.Equal([recurring], events);
    }

    private static CalendarEvent CreateEvent()
    {
        var start = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "IndexedDB 测试事件",
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            CreatedAtUtc = start.AddDays(-1),
            UpdatedAtUtc = start.AddDays(-1)
        };
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public int ImportCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("import", identifier);
            ImportCount++;
            return ValueTask.FromResult((TValue)module);
        }
    }

    private sealed class FakeJsModule : IJSObjectReference
    {
        public int InitializationCount { get; private set; }

        public Exception? Failure { get; init; }

        public CalendarEvent[] AllEvents { get; init; } = [];

        public CalendarEvent[] RecurringEvents { get; init; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "initialize")
            {
                InitializationCount++;
                return ValueTask.FromResult(default(TValue)!);
            }

            if (Failure is not null)
            {
                return ValueTask.FromException<TValue>(Failure);
            }

            object? result = identifier switch
            {
                "createEvent" => args![0],
                "getEventsByRange" => Array.Empty<CalendarEvent>(),
                "getAllEventsIncludingDeleted" => AllEvents,
                "getRecurringEventMasters" => RecurringEvents,
                _ => default(TValue)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
