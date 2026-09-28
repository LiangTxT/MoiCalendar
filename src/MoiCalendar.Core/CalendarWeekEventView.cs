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
    bool IsRecurring = false,
    CalendarEventSegmentPosition SegmentPosition = CalendarEventSegmentPosition.Single,
    DateTimeOffset OriginalStartUtc = default,
    DateTimeOffset OriginalEndUtc = default,
    string InteractionTimeZoneId = "UTC",
    int CalendarColorIndex = 1,
    bool IsReadOnly = false);

public sealed record CalendarWeekTimedEvent(
    Guid Id,
    string Title,
    string TimeLabel,
    double TopPercentage,
    double HeightPercentage,
    int StartMinute,
    int DurationMinutes,
    bool IsRecurring = false,
    CalendarEventSegmentPosition SegmentPosition = CalendarEventSegmentPosition.Single,
    string Location = "",
    DateTimeOffset OriginalStartUtc = default,
    DateTimeOffset OriginalEndUtc = default,
    string InteractionTimeZoneId = "UTC",
    int CalendarColorIndex = 1,
    bool IsReadOnly = false);

/// <summary>
/// 周视图和日视图共用的纯展示投影。它只选择可见日期并调用共享时间网格布局，
/// 不读取 DOM，也不访问持久化或同步服务。
/// </summary>
public static class CalendarTimeGridProjection
{
    public static IReadOnlyList<CalendarWeekDayEvents> SelectDays(
        IReadOnlyList<CalendarWeekDayEvents> days,
        DateOnly? visibleDate) =>
        visibleDate is DateOnly date
            ? days.Where(day => day.Date.Date == date).ToArray()
            : days;

    public static IReadOnlyDictionary<Guid, TimeGridLayoutItem> LayoutTimedEvents(
        CalendarWeekDayEvents day,
        TimeGridMetrics metrics) =>
        TimeGridLayoutEngine.Layout(
                day.TimedEvents.Select(item => new TimedLayoutInput(
                    item.Id,
                    item.StartMinute,
                    item.StartMinute + item.DurationMinutes)).ToArray(),
                metrics)
            .ToDictionary(item => item.SegmentId);
}
