namespace MoiCalendar.Core;

public sealed class CalendarWeekEventView
{
    internal CalendarWeekEventView(IReadOnlyList<CalendarWeekDayEvents> days)
    {
        Days = days;
    }

    public IReadOnlyList<CalendarWeekDayEvents> Days { get; }

    public static CalendarWeekEventView Empty { get; } = new(Array.Empty<CalendarWeekDayEvents>());
}

public sealed record CalendarWeekDayEvents(
    CalendarWeekDate Date,
    IReadOnlyList<CalendarWeekAllDayEvent> AllDayEvents,
    IReadOnlyList<CalendarWeekTimedEvent> TimedEvents);

public sealed record CalendarWeekAllDayEvent(
    Guid Id,
    string Title,
    bool IsRecurring = false);

public sealed record CalendarWeekTimedEvent(
    Guid Id,
    string Title,
    string TimeLabel,
    double TopPercentage,
    double HeightPercentage,
    int StartMinute,
    int DurationMinutes,
    bool IsRecurring = false);
