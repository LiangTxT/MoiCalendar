using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarMonthViewLayoutTests
{
    private static readonly DateOnly Today = new(2026, 9, 23);

    [Fact]
    public async Task OneEvent_ProducesSixAlignedWeeksAndOneVisibleEvent()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(Event(1, "单个事件", Utc(2026, 9, 8, 9), Utc(2026, 9, 8, 10)));

        var layout = await GetLayoutAsync(service);
        var day = FindDay(layout, new DateOnly(2026, 9, 8));

        Assert.Equal(6, layout.Weeks.Count);
        Assert.All(layout.Weeks, week => Assert.Equal(7, week.Days.Count));
        Assert.Equal("单个事件", Assert.Single(day.Regular.VisibleEvents).Title);
        Assert.Equal(0, day.Regular.OverflowCount);
    }

    [Fact]
    public async Task ManyEvents_ProducesDensitySpecificMoreCounts()
    {
        var (service, repository) = CreateService();
        for (var index = 1; index <= 5; index++)
        {
            await repository.CreateAsync(Event(
                index,
                $"事件 {index}",
                Utc(2026, 9, 8, 7 + index),
                Utc(2026, 9, 8, 8 + index)));
        }

        var day = FindDay(await GetLayoutAsync(service), new DateOnly(2026, 9, 8));

        Assert.Equal(3, day.Regular.VisibleEvents.Count);
        Assert.Equal(2, day.Regular.OverflowCount);
        Assert.Equal(2, day.Compact.VisibleEvents.Count);
        Assert.Equal(3, day.Compact.OverflowCount);
    }

    [Fact]
    public async Task CrossWeekMultiDayEvent_UsesSegmentsWithoutDuplicatingPersistedIdentity()
    {
        var (service, repository) = CreateService();
        var calendarEvent = Event(
            1,
            "跨周事件",
            Utc(2026, 9, 5, 20),
            Utc(2026, 9, 8, 10));
        await repository.CreateAsync(calendarEvent);

        var layout = await GetLayoutAsync(service);
        var segments = layout.Weeks
            .SelectMany(week => week.Days)
            .SelectMany(day => day.AllEvents)
            .Where(item => item.Id == calendarEvent.Id)
            .ToArray();

        Assert.Equal(4, segments.Length);
        Assert.All(segments, segment => Assert.Equal(calendarEvent.Id, segment.Id));
        Assert.Equal(
            [CalendarEventSegmentPosition.Start, CalendarEventSegmentPosition.Middle,
             CalendarEventSegmentPosition.Middle, CalendarEventSegmentPosition.End],
            segments.Select(segment => segment.SegmentPosition));
        Assert.Contains(layout.Weeks[0].Days.SelectMany(day => day.AllEvents), item => item.Id == calendarEvent.Id);
        Assert.Contains(layout.Weeks[1].Days.SelectMany(day => day.AllEvents), item => item.Id == calendarEvent.Id);
    }

    [Fact]
    public async Task AllDayEvent_IsPresentedBeforeTimedEvents()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(Event(
            1,
            "定时事件",
            Utc(2026, 9, 8, 8),
            Utc(2026, 9, 8, 9)));
        await repository.CreateAsync(Event(
            2,
            "全天事件",
            Utc(2026, 9, 8),
            Utc(2026, 9, 9),
            isAllDay: true));

        var events = FindDay(await GetLayoutAsync(service), new DateOnly(2026, 9, 8)).AllEvents;

        Assert.True(events[0].IsAllDay);
        Assert.Equal("全天", events[0].TimeLabel);
        Assert.False(events[1].IsAllDay);
    }

    [Fact]
    public async Task AdjacentMonthAndTodayMetadataRemainAlignedWithCells()
    {
        var (service, _) = CreateService();

        var layout = await GetLayoutAsync(service);
        var cells = layout.Weeks.SelectMany(week => week.Days).ToArray();

        Assert.Equal(new DateOnly(2026, 8, 31), cells[0].Date.Date);
        Assert.False(cells[0].Date.IsInActiveMonth);
        Assert.True(FindDay(layout, Today).Date.IsToday);
    }

    [Fact]
    public async Task HiddenCalendar_KeepsGridAndRemovesEvents()
    {
        var (service, repository) = CreateService();
        await repository.CreateAsync(Event(1, "应隐藏", Utc(2026, 9, 8, 9), Utc(2026, 9, 8, 10)));
        var month = new CalendarMonth(2026, 9).CreateView(Today);
        var events = await service.GetMonthViewAsync(month, TimeZoneInfo.Utc.Id);

        var layout = events.CreateLayout(month, includeEvents: false);

        Assert.Equal(42, layout.Weeks.Sum(week => week.Days.Count));
        Assert.Empty(layout.Weeks.SelectMany(week => week.Days).SelectMany(day => day.AllEvents));
    }

    [Fact]
    public void DifferentCalendarColors_ArePreservedByOverflowLayout()
    {
        var first = Item(1, calendarColorIndex: 1);
        var second = Item(2, calendarColorIndex: 6);

        var layout = MonthLayoutEngine.LayoutDay([first, second], visibleLimit: 2);

        Assert.Equal([1, 6], layout.VisibleEvents.Select(item => item.CalendarColorIndex));
    }

    private static async Task<CalendarMonthLayoutView> GetLayoutAsync(CalendarEventService service)
    {
        var month = new CalendarMonth(2026, 9).CreateView(Today);
        var events = await service.GetMonthViewAsync(month, TimeZoneInfo.Utc.Id);
        return events.CreateLayout(month);
    }

    private static CalendarMonthDayLayout FindDay(CalendarMonthLayoutView layout, DateOnly date) =>
        layout.Weeks.SelectMany(week => week.Days).Single(day => day.Date.Date == date);

    private static (CalendarEventService Service, InMemoryEventRepository Repository) CreateService()
    {
        var repository = new InMemoryEventRepository();
        return (
            new CalendarEventService(
                repository,
                new InMemoryDeviceService("month-layout-device"),
                new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()),
                TimeProvider.System),
            repository);
    }

    private static CalendarEvent Event(
        int id,
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        bool isAllDay = false) => new()
    {
        Id = StableId(id),
        Title = title,
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = start,
        EndUtc = end,
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = isAllDay,
        CreatedAtUtc = start.AddDays(-1),
        UpdatedAtUtc = start.AddDays(-1)
    };

    private static CalendarEventListItem Item(int id, int calendarColorIndex) => new(
        StableId(id),
        $"事件 {id}",
        "09:00",
        false,
        TimeSpan.FromHours(9),
        CalendarColorIndex: calendarColorIndex);

    private static Guid StableId(int value)
    {
        var bytes = new byte[16];
        bytes[0] = (byte)value;
        return new Guid(bytes);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
