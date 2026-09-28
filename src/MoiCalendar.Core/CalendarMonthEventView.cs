namespace MoiCalendar.Core;

public sealed class CalendarMonthEventView
{
    private static readonly IReadOnlyList<CalendarEventListItem> NoEvents =
        Array.Empty<CalendarEventListItem>();

    private readonly IReadOnlyDictionary<DateOnly, IReadOnlyList<CalendarEventListItem>> eventsByDate;

    internal CalendarMonthEventView(
        IReadOnlyDictionary<DateOnly, IReadOnlyList<CalendarEventListItem>> eventsByDate)
    {
        this.eventsByDate = eventsByDate;
    }

    public IReadOnlyList<CalendarEventListItem> GetEvents(DateOnly date) =>
        eventsByDate.TryGetValue(date, out var calendarEvents) ? calendarEvents : NoEvents;

    public CalendarMonthLayoutView CreateLayout(
        CalendarMonthView monthView,
        int regularEventRows = CalendarMetrics.DefaultMonthVisibleEventLimit,
        int compactEventRows = 2,
        bool includeEvents = true)
    {
        ArgumentNullException.ThrowIfNull(monthView);
        ArgumentOutOfRangeException.ThrowIfNegative(regularEventRows);
        ArgumentOutOfRangeException.ThrowIfNegative(compactEventRows);

        var weeks = monthView.Dates
            .Chunk(CalendarMonthView.DaysPerWeek)
            .Select(dates => new CalendarMonthWeekLayout(
                dates.Select(date =>
                {
                    var events = includeEvents ? GetEvents(date.Date) : NoEvents;
                    return new CalendarMonthDayLayout(
                        date,
                        MonthLayoutEngine.LayoutDay(events, regularEventRows),
                        MonthLayoutEngine.LayoutDay(events, compactEventRows),
                        events);
                }).ToArray()))
            .ToArray();

        return new CalendarMonthLayoutView(monthView, weeks);
    }

    public static CalendarMonthEventView Empty { get; } =
        new(new Dictionary<DateOnly, IReadOnlyList<CalendarEventListItem>>());
}

public sealed record CalendarEventListItem(
    Guid Id,
    string Title,
    string TimeLabel,
    bool IsAllDay,
    TimeSpan SortTime,
    bool IsRecurring = false,
    CalendarEventSegmentPosition SegmentPosition = CalendarEventSegmentPosition.Single,
    DateTimeOffset OriginalStartUtc = default,
    DateTimeOffset OriginalEndUtc = default,
    string InteractionTimeZoneId = "UTC",
    int CalendarColorIndex = 1,
    bool IsReadOnly = false);

public sealed record CalendarMonthLayoutView(
    CalendarMonthView Month,
    IReadOnlyList<CalendarMonthWeekLayout> Weeks);

public sealed record CalendarMonthWeekLayout(
    IReadOnlyList<CalendarMonthDayLayout> Days);

public sealed record CalendarMonthDayLayout(
    CalendarDateCell Date,
    MonthDayLayout Regular,
    MonthDayLayout Compact,
    IReadOnlyList<CalendarEventListItem> AllEvents);
