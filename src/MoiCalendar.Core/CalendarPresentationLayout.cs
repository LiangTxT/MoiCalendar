namespace MoiCalendar.Core;

public static class CalendarPresentationLayout
{
    public static MonthDayLayout GetMonthDayLayout(
        IReadOnlyList<CalendarEventListItem> events,
        int visibleLimit) =>
        MonthLayoutEngine.LayoutDay(events, visibleLimit);

    public static TimedEventPosition CalculateTimedPosition(
        int startMinute,
        int durationMinutes,
        int minimumDisplayMinutes = CalendarMetrics.DefaultMinimumTimedDisplayMinutes) =>
        TimeGridLayoutEngine.CalculatePosition(startMinute, durationMinutes, minimumDisplayMinutes);

    public static IReadOnlyDictionary<Guid, TimedEventLayout> LayoutOverlappingEvents(
        IReadOnlyList<CalendarWeekTimedEvent> events) =>
        OverlapLayoutEngine.Layout(events);

    public static CalendarEventSegmentPosition GetSegmentPosition(
        DateOnly date,
        DateOnly eventFirstDate,
        DateOnly eventLastDate) =>
        MultiDayLayoutEngine.GetSegmentPosition(date, eventFirstDate, eventLastDate);

    public static bool IsCurrentDate(DateOnly date, DateOnly today) => date == today;
}

public static class CalendarMetrics
{
    public const int MinutesPerDay = 24 * 60;
    public const int DefaultMinimumTimedDisplayMinutes = 30;
    public const int DefaultMonthVisibleEventLimit = 3;
}

public static partial class MonthLayoutEngine
{
    public static MonthDayLayout LayoutDay(
        IReadOnlyList<CalendarEventListItem> events,
        int visibleLimit)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(visibleLimit);

        var visibleCount = Math.Min(events.Count, visibleLimit);
        return new MonthDayLayout(
            events.Take(visibleCount).ToArray(),
            events.Count - visibleCount);
    }
}

public static partial class TimeGridLayoutEngine
{
    public static TimedEventPosition CalculatePosition(
        int startMinute,
        int durationMinutes,
        int minimumDisplayMinutes = CalendarMetrics.DefaultMinimumTimedDisplayMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startMinute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumDisplayMinutes);

        var metrics = new TimeGridMetrics(minimumDisplayMinutes: minimumDisplayMinutes);
        return CalculateClippedPosition(startMinute, startMinute + durationMinutes, metrics);
    }
}

public static partial class OverlapLayoutEngine
{
    public static IReadOnlyDictionary<Guid, TimedEventLayout> Layout(
        IReadOnlyList<CalendarWeekTimedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Layout(events
            .Select(item => new TimedLayoutInput(
                item.Id,
                item.StartMinute,
                item.StartMinute + item.DurationMinutes))
            .ToArray());
    }
}

public static partial class MultiDayLayoutEngine
{
    public static CalendarEventSegmentPosition GetSegmentPosition(
        DateOnly date,
        DateOnly eventFirstDate,
        DateOnly eventLastDate)
    {
        if (eventLastDate < eventFirstDate)
        {
            throw new ArgumentException("事件结束日期不能早于开始日期。", nameof(eventLastDate));
        }

        if (date < eventFirstDate || date > eventLastDate)
        {
            throw new ArgumentOutOfRangeException(nameof(date));
        }

        if (eventFirstDate == eventLastDate)
        {
            return CalendarEventSegmentPosition.Single;
        }

        if (date == eventFirstDate)
        {
            return CalendarEventSegmentPosition.Start;
        }

        return date == eventLastDate
            ? CalendarEventSegmentPosition.End
            : CalendarEventSegmentPosition.Middle;
    }
}

public sealed record MonthDayLayout(
    IReadOnlyList<CalendarEventListItem> VisibleEvents,
    int OverflowCount);

public readonly record struct TimedEventPosition(
    double TopPercentage,
    double HeightPercentage);

public readonly record struct TimedEventLayout(
    int Column,
    int ColumnCount)
{
    public double LeftPercentage => Column * WidthPercentage;

    public double WidthPercentage => 100d / ColumnCount;
}

public enum CalendarEventSegmentPosition
{
    Single,
    Start,
    Middle,
    End
}
