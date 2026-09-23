namespace MoiCalendar.Core;

public static class CalendarPresentationLayout
{
    private const int MinutesPerDay = 24 * 60;

    public static MonthDayLayout GetMonthDayLayout(
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

    public static TimedEventPosition CalculateTimedPosition(
        int startMinute,
        int durationMinutes,
        int minimumDisplayMinutes = 30)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startMinute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumDisplayMinutes);

        if (startMinute >= MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(startMinute));
        }

        var clippedDuration = Math.Min(durationMinutes, MinutesPerDay - startMinute);
        var displayDuration = Math.Min(
            Math.Max(clippedDuration, minimumDisplayMinutes),
            MinutesPerDay - startMinute);
        return new TimedEventPosition(
            startMinute * 100d / MinutesPerDay,
            displayDuration * 100d / MinutesPerDay);
    }

    public static IReadOnlyDictionary<Guid, TimedEventLayout> LayoutOverlappingEvents(
        IReadOnlyList<CalendarWeekTimedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var result = new Dictionary<Guid, TimedEventLayout>();
        var group = new List<(Guid Id, int Column)>();
        var columnEnds = new List<int>();
        var groupEnd = -1;

        void CompleteGroup()
        {
            var columnCount = Math.Max(1, columnEnds.Count);
            foreach (var item in group)
            {
                result[item.Id] = new TimedEventLayout(item.Column, columnCount);
            }

            group.Clear();
            columnEnds.Clear();
        }

        foreach (var calendarEvent in events
                     .OrderBy(item => item.StartMinute)
                     .ThenByDescending(item => item.DurationMinutes)
                     .ThenBy(item => item.Id))
        {
            if (group.Count > 0 && calendarEvent.StartMinute >= groupEnd)
            {
                CompleteGroup();
                groupEnd = -1;
            }

            var visualDuration = Math.Max(
                calendarEvent.DurationMinutes,
                (int)Math.Ceiling(calendarEvent.HeightPercentage * MinutesPerDay / 100d));
            var eventEnd = Math.Min(MinutesPerDay, calendarEvent.StartMinute + visualDuration);
            var column = columnEnds.FindIndex(endMinute => endMinute <= calendarEvent.StartMinute);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(eventEnd);
            }
            else
            {
                columnEnds[column] = eventEnd;
            }

            group.Add((calendarEvent.Id, column));
            groupEnd = Math.Max(groupEnd, eventEnd);
        }

        CompleteGroup();
        return result;
    }

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

    public static bool IsCurrentDate(DateOnly date, DateOnly today) => date == today;
}

public sealed record MonthDayLayout(
    IReadOnlyList<CalendarEventListItem> VisibleEvents,
    int OverflowCount);

public readonly record struct TimedEventPosition(
    double TopPercentage,
    double HeightPercentage);

public readonly record struct TimedEventLayout(
    int Column,
    int ColumnCount);

public enum CalendarEventSegmentPosition
{
    Single,
    Start,
    Middle,
    End
}
