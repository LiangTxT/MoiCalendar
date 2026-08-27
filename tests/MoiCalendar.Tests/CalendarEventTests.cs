using MoiCalendar.Core;
using MoiCalendar.Storage;
using System.Text.Json;

namespace MoiCalendar.Tests;

public sealed class CalendarEventTests
{
    [Fact]
    public async Task Service_CreatesUpdatesAndDeletesEvent()
    {
        var repository = new InMemoryEventRepository();
        var operationRepository = new InMemoryOperationRepository();
        var deviceService = new InMemoryDeviceService("test-device");
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 23, 1, 0, 0, TimeSpan.Zero));
        var service = new CalendarEventService(
            repository,
            deviceService,
            new InMemorySyncService(repository, operationRepository),
            clock);
        var draft = service.CreateDraft(new DateOnly(2026, 8, 23), TimeZoneInfo.Utc.Id);
        draft.Title = "初始标题";
        draft.Description = "描述";
        draft.Location = "地点";

        var created = await service.CreateAsync(draft);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero), created.StartUtc);
        Assert.Equal(clock.UtcNow, created.CreatedAtUtc);
        Assert.Equal(clock.UtcNow, created.UpdatedAtUtc);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        draft.Title = "更新后的标题";
        var updated = await service.UpdateAsync(created.Id, draft);

        Assert.Equal("更新后的标题", updated.Title);
        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal(clock.UtcNow, updated.UpdatedAtUtc);
        Assert.Equal(updated, await service.GetByIdAsync(created.Id));

        clock.UtcNow = clock.UtcNow.AddHours(1);
        Assert.True(await service.DeleteAsync(created.Id));
        Assert.Null(await service.GetByIdAsync(created.Id));

        var operations = await operationRepository.GetByStatusAsync(SyncOperationStatus.Pending);
        Assert.Equal(3, operations.Count);
        Assert.Equal(
            new[] { SyncOperationType.Create, SyncOperationType.Update, SyncOperationType.Delete },
            operations.Select(operation => operation.OperationType));
        Assert.All(operations, operation =>
        {
            Assert.Equal("test-device", operation.DeviceId);
            Assert.Equal(created.Id, operation.EntityId);
        });
        var deletePayload = JsonSerializer.Deserialize<CalendarEvent>(
            operations[^1].Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(clock.UtcNow, deletePayload?.DeletedAtUtc);
    }

    [Fact]
    public async Task Repository_GetByRangeReturnsOverlappingEventsInTimeOrder()
    {
        var repository = new InMemoryEventRepository();
        var day = new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
        var later = CreateEvent("稍后", day.AddHours(14), day.AddHours(15));
        var earlier = CreateEvent("较早", day.AddHours(8), day.AddHours(9));
        var outside = CreateEvent("范围外", day.AddDays(1), day.AddDays(1).AddHours(1));
        await repository.CreateAsync(later);
        await repository.CreateAsync(outside);
        await repository.CreateAsync(earlier);

        var events = await repository.GetByRangeAsync(day, day.AddDays(1));

        Assert.Equal(new[] { earlier.Id, later.Id }, events.Select(calendarEvent => calendarEvent.Id));
    }

    [Fact]
    public async Task MonthViewOrdersAllDayThenTimedEventsByTime()
    {
        var repository = new InMemoryEventRepository();
        var operationRepository = new InMemoryOperationRepository();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("month-view-device"),
            new InMemorySyncService(repository, operationRepository),
            clock);
        var date = new DateOnly(2026, 8, 23);

        var later = service.CreateDraft(date, TimeZoneInfo.Utc.Id);
        later.Title = "下午事件";
        later.StartLocal = date.ToDateTime(new TimeOnly(15, 0));
        later.EndLocal = date.ToDateTime(new TimeOnly(16, 0));
        await service.CreateAsync(later);

        var earlier = service.CreateDraft(date, TimeZoneInfo.Utc.Id);
        earlier.Title = "上午事件";
        earlier.StartLocal = date.ToDateTime(new TimeOnly(8, 0));
        earlier.EndLocal = date.ToDateTime(new TimeOnly(9, 0));
        await service.CreateAsync(earlier);

        var allDay = service.CreateDraft(date, TimeZoneInfo.Utc.Id);
        allDay.Title = "全天事件";
        allDay.SetAllDay(true);
        await service.CreateAsync(allDay);

        var calendar = new CalendarMonth(2026, 8).CreateView(date);
        var eventView = await service.GetMonthViewAsync(calendar, TimeZoneInfo.Utc.Id);

        Assert.Equal(
            new[] { "全天事件", "上午事件", "下午事件" },
            eventView.GetEvents(date).Select(calendarEvent => calendarEvent.Title));
    }

    private static CalendarEvent CreateEvent(
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = startUtc,
        EndUtc = endUtc,
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = false,
        CreatedAtUtc = startUtc.AddDays(-1),
        UpdatedAtUtc = startUtc.AddDays(-1)
    };

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
