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

    public static CalendarMonthEventView Empty { get; } =
        new(new Dictionary<DateOnly, IReadOnlyList<CalendarEventListItem>>());
}

public sealed record CalendarEventListItem(
    Guid Id,
    string Title,
    string TimeLabel,
    bool IsAllDay,
    TimeSpan SortTime);
