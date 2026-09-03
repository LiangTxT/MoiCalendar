using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarWeekEventViewTests
{
    [Fact]
    public async Task WeekView_SplitsEventCrossingMidnightAcrossTwoDays()
    {
        var (service, repository) = CreateService();
        var calendarEvent = CreateEvent(
            "跨午夜",
            Utc(2026, 8, 26, 23, 30),
            Utc(2026, 8, 27, 0, 30));
        await repository.CreateAsync(calendarEvent);

        var view = await GetWeekAsync(service, new DateOnly(2026, 8, 26));

        var first = Assert.Single(view.Days.Single(day => day.Date.Date == new DateOnly(2026, 8, 26)).TimedEvents);
        var second = Assert.Single(view.Days.Single(day => day.Date.Date == new DateOnly(2026, 8, 27)).TimedEvents);
        Assert.Equal("23:30–24:00", first.TimeLabel);
        Assert.Equal("00:00–00:30", second.TimeLabel);
        Assert.Equal(calendarEvent.Id, first.Id);
        Assert.Equal(calendarEvent.Id, second.Id);
    }

    [Fact]
    public async Task WeekView_PutsAllDayEventInSeparateAllDayGroups()
    {
        var (service, repository) = CreateService();
        var calendarEvent = CreateEvent(
            "三天全天",
            Utc(2026, 8, 25),
            Utc(2026, 8, 28),
            isAllDay: true);
        await repository.CreateAsync(calendarEvent);

        var view = await GetWeekAsync(service, new DateOnly(2026, 8, 26));

        Assert.Equal(3, view.Days.Sum(day => day.AllDayEvents.Count));
        Assert.All(view.Days.SelectMany(day => day.AllDayEvents), item => Assert.Equal(calendarEvent.Id, item.Id));
        Assert.Empty(view.Days.SelectMany(day => day.TimedEvents));
    }

    [Fact]
    public async Task WeekView_ClipsMultiDayTimedEventToVisibleWeek()
    {
        var (service, repository) = CreateService();
        var calendarEvent = CreateEvent(
            "跨周多日",
            Utc(2026, 8, 23, 20),
            Utc(2026, 8, 25, 10));
        await repository.CreateAsync(calendarEvent);

        var view = await GetWeekAsync(service, new DateOnly(2026, 8, 24));

        var monday = Assert.Single(view.Days[0].TimedEvents);
        var tuesday = Assert.Single(view.Days[1].TimedEvents);
        Assert.Equal("00:00–24:00", monday.TimeLabel);
        Assert.Equal(0, monday.StartMinute);
        Assert.Equal(24 * 60, monday.DurationMinutes);
        Assert.Equal("00:00–10:00", tuesday.TimeLabel);
        Assert.Empty(view.Days.Skip(2).SelectMany(day => day.TimedEvents));
    }

    [Fact]
    public async Task WeekView_QueriesOnlyEventsIntersectingVisibleWeek()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(CreateEvent("周内", Utc(2026, 8, 26, 9), Utc(2026, 8, 26, 10)));
        await repository.CreateAsync(CreateEvent("周外", Utc(2026, 8, 31, 9), Utc(2026, 8, 31, 10)));

        var view = await GetWeekAsync(service, new DateOnly(2026, 8, 26));

        Assert.Equal("周内", Assert.Single(view.Days.SelectMany(day => day.TimedEvents)).Title);
    }

    [Fact]
    public async Task WeekView_ComputesStableVisualPositionAndMinimumHeight()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(CreateEvent("短事件", Utc(2026, 8, 26, 6), Utc(2026, 8, 26, 6, 5)));

        var view = await GetWeekAsync(service, new DateOnly(2026, 8, 26));

        var calendarEvent = Assert.Single(view.Days.SelectMany(day => day.TimedEvents));
        Assert.Equal(25d, calendarEvent.TopPercentage, 6);
        Assert.Equal(30d * 100d / (24 * 60), calendarEvent.HeightPercentage, 6);
        Assert.Equal(5, calendarEvent.DurationMinutes);
    }

    private static Task<CalendarWeekEventView> GetWeekAsync(
        CalendarEventService service,
        DateOnly date)
    {
        var week = CalendarWeek.FromDate(date).CreateView(date);
        return service.GetWeekViewAsync(week, TimeZoneInfo.Utc.Id);
    }

    private static (CalendarEventService Service, InMemoryEventRepository Repository) CreateService()
    {
        var repository = new InMemoryEventRepository();
        var operationRepository = new InMemoryOperationRepository();
        return (
            new CalendarEventService(
                repository,
                new InMemoryDeviceService("week-view-device"),
                new InMemoryEventChangeRepository(repository, operationRepository),
                TimeProvider.System),
            repository);
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

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
