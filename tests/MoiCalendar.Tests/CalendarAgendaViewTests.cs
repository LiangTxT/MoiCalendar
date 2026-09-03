using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarAgendaViewTests
{
    [Fact]
    public async Task AgendaView_GroupsEventsByDateAndSortsDaysChronologically()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(CreateEvent("后一天", Utc(2026, 8, 20, 9), Utc(2026, 8, 20, 10)));
        await repository.CreateAsync(CreateEvent("前一天", Utc(2026, 8, 3, 9), Utc(2026, 8, 3, 10)));

        var agenda = await service.GetAgendaViewAsync(new CalendarMonth(2026, 8), TimeZoneInfo.Utc.Id);

        Assert.Equal(new[] { new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 20) },
            agenda.Days.Select(day => day.Date));
        Assert.Equal("前一天", agenda.Days[0].Events.Single().Title);
        Assert.Equal("后一天", agenda.Days[1].Events.Single().Title);
    }

    [Fact]
    public async Task AgendaView_OrdersAllDayBeforeTimedEventsThenByStartTime()
    {
        var (service, repository) = CreateService();
        var date = new DateOnly(2026, 8, 12);
        await repository.CreateAsync(CreateEvent("下午", Utc(2026, 8, 12, 15), Utc(2026, 8, 12, 16)));
        await repository.CreateAsync(CreateEvent("全天", Utc(2026, 8, 12), Utc(2026, 8, 13), isAllDay: true));
        await repository.CreateAsync(CreateEvent("上午", Utc(2026, 8, 12, 8), Utc(2026, 8, 12, 9)));

        var agenda = await service.GetAgendaViewAsync(new CalendarMonth(2026, 8), TimeZoneInfo.Utc.Id);

        var events = Assert.Single(agenda.Days, day => day.Date == date).Events;
        Assert.Equal(new[] { "全天", "上午", "下午" }, events.Select(calendarEvent => calendarEvent.Title));
        Assert.Equal(new[] { "全天", "08:00", "15:00" }, events.Select(calendarEvent => calendarEvent.TimeLabel));
    }

    [Fact]
    public async Task AgendaView_ListsMultipleEventsWithoutCreatingExtraCopies()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(CreateEvent("事件一", Utc(2026, 8, 8, 8), Utc(2026, 8, 8, 9)));
        await repository.CreateAsync(CreateEvent("事件二", Utc(2026, 8, 8, 10), Utc(2026, 8, 8, 11)));
        await repository.CreateAsync(CreateEvent("范围外", Utc(2026, 9, 1, 8), Utc(2026, 9, 1, 9)));

        var agenda = await service.GetAgendaViewAsync(new CalendarMonth(2026, 8), TimeZoneInfo.Utc.Id);

        var day = Assert.Single(agenda.Days);
        Assert.Equal(2, day.Events.Count);
        Assert.Equal(new[] { "事件一", "事件二" }, day.Events.Select(calendarEvent => calendarEvent.Title));
    }

    [Fact]
    public async Task AgendaView_ReturnsCleanEmptyResultForEmptyMonth()
    {
        var (service, _) = CreateService();

        var agenda = await service.GetAgendaViewAsync(new CalendarMonth(2026, 8), TimeZoneInfo.Utc.Id);

        Assert.Empty(agenda.Days);
    }

    [Fact]
    public async Task AgendaView_PreservesAllEventsInHighDensityMonth()
    {
        const int eventCount = 1_000;
        var (service, repository) = CreateService();
        var start = Utc(2026, 8, 15, 8);

        for (var index = eventCount - 1; index >= 0; index--)
        {
            var eventStart = start.AddMinutes(index);
            await repository.CreateAsync(CreateEvent($"事件 {index:D4}", eventStart, eventStart.AddMinutes(1)));
        }

        var agenda = await service.GetAgendaViewAsync(new CalendarMonth(2026, 8), TimeZoneInfo.Utc.Id);

        Assert.Equal(eventCount, agenda.Days.Sum(day => day.Events.Count));
        Assert.Equal(eventCount, agenda.Days.SelectMany(day => day.Events).Select(item => item.Id).Distinct().Count());
        Assert.Equal("事件 0000", agenda.Days[0].Events[0].Title);
    }

    [Fact]
    public async Task AgendaView_HandlesEventsAroundMidnightUsingEndExclusiveSemantics()
    {
        var (service, repository) = CreateService();
        var crossingMidnight = CreateEvent(
            "跨午夜",
            Utc(2026, 8, 10, 23, 30),
            Utc(2026, 8, 11, 0, 30));
        var endingAtMidnight = CreateEvent(
            "午夜结束",
            Utc(2026, 8, 11, 22),
            Utc(2026, 8, 12));
        await repository.CreateAsync(crossingMidnight);
        await repository.CreateAsync(endingAtMidnight);

        var agenda = await service.GetAgendaViewAsync(new CalendarMonth(2026, 8), TimeZoneInfo.Utc.Id);

        Assert.Equal(new[] { new DateOnly(2026, 8, 10), new DateOnly(2026, 8, 11) },
            agenda.Days.Select(day => day.Date));
        Assert.Equal("23:30", agenda.Days[0].Events.Single().TimeLabel);
        Assert.Equal(new[] { "跨午夜", "午夜结束" }, agenda.Days[1].Events.Select(item => item.Title));
        Assert.Equal("续", agenda.Days[1].Events[0].TimeLabel);
    }

    private static (CalendarEventService Service, InMemoryEventRepository Repository) CreateService()
    {
        var repository = new InMemoryEventRepository();
        var operationRepository = new InMemoryOperationRepository();
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("agenda-view-device"),
            new InMemoryEventChangeRepository(repository, operationRepository),
            TimeProvider.System);
        return (service, repository);
    }

    private static CalendarEvent CreateEvent(
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool isAllDay = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = startUtc,
        EndUtc = endUtc,
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = isAllDay,
        CreatedAtUtc = startUtc.AddDays(-1),
        UpdatedAtUtc = startUtc.AddDays(-1)
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
