namespace MoiCalendar.Core;

/// <summary>
/// 以日期为边界的半开区间；结束日期不包含在可见范围内。
/// </summary>
public readonly record struct CalendarVisibleRange
{
    public CalendarVisibleRange(DateOnly startDate, DateOnly endDateExclusive)
    {
        if (endDateExclusive <= startDate)
        {
            throw new ArgumentException("可见范围结束日期必须晚于开始日期。", nameof(endDateExclusive));
        }

        StartDate = startDate;
        EndDateExclusive = endDateExclusive;
    }

    public DateOnly StartDate { get; }

    public DateOnly EndDateExclusive { get; }

    public int DayCount => EndDateExclusive.DayNumber - StartDate.DayNumber;

    public bool Contains(DateOnly date) => date >= StartDate && date < EndDateExclusive;
}

public static class VisibleRangeLayoutEngine
{
    public static CalendarVisibleRange Calculate(
        CalendarViewMode viewMode,
        DateOnly anchorDate,
        DayOfWeek firstDayOfWeek = DayOfWeek.Monday,
        int monthWeekCount = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monthWeekCount);

        return viewMode switch
        {
            CalendarViewMode.Month => CalculateMonth(anchorDate, firstDayOfWeek, monthWeekCount),
            CalendarViewMode.Week => CalculateWeek(anchorDate, firstDayOfWeek),
            CalendarViewMode.Day => new CalendarVisibleRange(anchorDate, anchorDate.AddDays(1)),
            CalendarViewMode.Agenda => CalculateAgendaMonth(anchorDate),
            _ => throw new ArgumentOutOfRangeException(nameof(viewMode))
        };
    }

    private static CalendarVisibleRange CalculateMonth(
        DateOnly anchorDate,
        DayOfWeek firstDayOfWeek,
        int weekCount)
    {
        var firstOfMonth = new DateOnly(anchorDate.Year, anchorDate.Month, 1);
        var start = StartOfWeek(firstOfMonth, firstDayOfWeek);
        return new CalendarVisibleRange(start, start.AddDays(weekCount * CalendarWeek.DayCount));
    }

    private static CalendarVisibleRange CalculateWeek(DateOnly anchorDate, DayOfWeek firstDayOfWeek)
    {
        var start = StartOfWeek(anchorDate, firstDayOfWeek);
        return new CalendarVisibleRange(start, start.AddDays(CalendarWeek.DayCount));
    }

    private static CalendarVisibleRange CalculateAgendaMonth(DateOnly anchorDate)
    {
        var start = new DateOnly(anchorDate.Year, anchorDate.Month, 1);
        return new CalendarVisibleRange(start, start.AddMonths(1));
    }

    private static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDayOfWeek)
    {
        var offset = ((int)date.DayOfWeek - (int)firstDayOfWeek + CalendarWeek.DayCount) %
            CalendarWeek.DayCount;
        return date.AddDays(-offset);
    }
}

/// <summary>
/// 已转换到显示时区的渲染输入。它不是持久化实体，也不包含 DOM/CSS 信息。
/// LocalEnd 采用不包含结束时刻的半开区间语义。
/// </summary>
public sealed record CalendarRenderEvent(
    Guid Id,
    string Title,
    DateTime LocalStart,
    DateTime LocalEnd,
    bool IsAllDay,
    int SourceOrder = 0);

public sealed record CalendarRenderSegment(
    Guid EventId,
    string Title,
    DateOnly Date,
    int StartMinute,
    int EndMinute,
    bool IsAllDay,
    bool StartsBeforeVisibleRange,
    bool EndsAfterVisibleRange,
    bool ContinuesFromPreviousDay,
    bool ContinuesToNextDay,
    CalendarEventSegmentPosition Position,
    int SourceOrder);

public static partial class MultiDayLayoutEngine
{
    public static IReadOnlyList<CalendarRenderSegment> Segment(
        IReadOnlyList<CalendarRenderEvent> events,
        CalendarVisibleRange visibleRange)
    {
        ArgumentNullException.ThrowIfNull(events);

        var result = new List<CalendarRenderSegment>();
        foreach (var calendarEvent in events
                     .OrderBy(item => item.LocalStart)
                     .ThenByDescending(item => item.LocalEnd - item.LocalStart)
                     .ThenBy(item => item.SourceOrder)
                     .ThenBy(item => item.Id))
        {
            Validate(calendarEvent);

            var eventFirstDate = DateOnly.FromDateTime(calendarEvent.LocalStart);
            var eventLastDate = DateOnly.FromDateTime(calendarEvent.LocalEnd.AddTicks(-1));
            var firstDate = eventFirstDate < visibleRange.StartDate
                ? visibleRange.StartDate
                : eventFirstDate;
            var visibleLastDate = visibleRange.EndDateExclusive.AddDays(-1);
            var lastDate = eventLastDate > visibleLastDate ? visibleLastDate : eventLastDate;
            if (lastDate < firstDate)
            {
                continue;
            }

            var startsBeforeVisibleRange = eventFirstDate < visibleRange.StartDate;
            var rangeEnd = visibleRange.EndDateExclusive.ToDateTime(TimeOnly.MinValue);
            var endsAfterVisibleRange = calendarEvent.LocalEnd > rangeEnd;

            for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
            {
                var startMinute = calendarEvent.IsAllDay || date > eventFirstDate
                    ? 0
                    : MinuteFloor(calendarEvent.LocalStart.TimeOfDay);
                var endMinute = calendarEvent.IsAllDay || date < eventLastDate
                    ? CalendarMetrics.MinutesPerDay
                    : MinuteCeiling(calendarEvent.LocalEnd.TimeOfDay);
                endMinute = Math.Clamp(endMinute, startMinute + 1, CalendarMetrics.MinutesPerDay);

                result.Add(new CalendarRenderSegment(
                    calendarEvent.Id,
                    calendarEvent.Title,
                    date,
                    startMinute,
                    endMinute,
                    calendarEvent.IsAllDay,
                    startsBeforeVisibleRange,
                    endsAfterVisibleRange,
                    date > eventFirstDate,
                    date < eventLastDate,
                    GetSegmentPosition(date, eventFirstDate, eventLastDate),
                    calendarEvent.SourceOrder));
            }
        }

        return result;
    }

    private static void Validate(CalendarRenderEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        if (calendarEvent.LocalEnd <= calendarEvent.LocalStart)
        {
            throw new ArgumentException("事件结束时间必须晚于开始时间。", nameof(calendarEvent));
        }
    }

    private static int MinuteFloor(TimeSpan value) => (int)Math.Floor(value.TotalMinutes);

    private static int MinuteCeiling(TimeSpan value) => (int)Math.Ceiling(value.TotalMinutes);
}

public sealed record MonthDaySegmentLayout(
    IReadOnlyList<CalendarRenderSegment> VisibleEvents,
    int OverflowCount);

public sealed class MonthLayoutResult(
    CalendarVisibleRange visibleRange,
    IReadOnlyDictionary<DateOnly, MonthDaySegmentLayout> days)
{
    public CalendarVisibleRange VisibleRange { get; } = visibleRange;

    public IReadOnlyDictionary<DateOnly, MonthDaySegmentLayout> Days { get; } = days;

    public MonthDaySegmentLayout GetDay(DateOnly date) =>
        Days.TryGetValue(date, out var day)
            ? day
            : new MonthDaySegmentLayout(Array.Empty<CalendarRenderSegment>(), 0);
}

public static partial class MonthLayoutEngine
{
    public static MonthLayoutResult Layout(
        IReadOnlyList<CalendarRenderEvent> events,
        CalendarVisibleRange visibleRange,
        int availableEventRows = CalendarMetrics.DefaultMonthVisibleEventLimit)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(availableEventRows);

        var segments = MultiDayLayoutEngine.Segment(events, visibleRange);
        var days = Enumerable.Range(0, visibleRange.DayCount)
            .Select(visibleRange.StartDate.AddDays)
            .ToDictionary(
                date => date,
                date =>
                {
                    var dayEvents = segments
                        .Where(item => item.Date == date)
                        .OrderBy(item => item.IsAllDay ? 0 : 1)
                        .ThenBy(item => item.StartMinute)
                        .ThenBy(item => item.SourceOrder)
                        .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                        .ThenBy(item => item.EventId)
                        .ToArray();
                    var visibleCount = Math.Min(dayEvents.Length, availableEventRows);
                    return new MonthDaySegmentLayout(
                        dayEvents.Take(visibleCount).ToArray(),
                        dayEvents.Length - visibleCount);
                });

        return new MonthLayoutResult(visibleRange, days);
    }
}

public sealed record AllDayLayoutItem(
    Guid EventId,
    string Title,
    DateOnly StartDate,
    DateOnly EndDateInclusive,
    int Row,
    bool StartsBeforeVisibleRange,
    bool EndsAfterVisibleRange,
    bool ContinuesFromPreviousDay,
    bool ContinuesToNextDay,
    int SourceOrder);

public static class AllDayLayoutEngine
{
    public static IReadOnlyList<AllDayLayoutItem> Layout(
        IReadOnlyList<CalendarRenderEvent> events,
        CalendarVisibleRange visibleRange,
        int daysPerRow = CalendarWeek.DayCount)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(daysPerRow);

        var candidates = new List<AllDayCandidate>();
        foreach (var calendarEvent in events.Where(item => item.IsAllDay))
        {
            if (calendarEvent.LocalEnd <= calendarEvent.LocalStart)
            {
                throw new ArgumentException("事件结束时间必须晚于开始时间。", nameof(events));
            }

            var eventStart = DateOnly.FromDateTime(calendarEvent.LocalStart);
            var eventEnd = DateOnly.FromDateTime(calendarEvent.LocalEnd.AddTicks(-1));
            var clippedStart = eventStart < visibleRange.StartDate ? visibleRange.StartDate : eventStart;
            var visibleEnd = visibleRange.EndDateExclusive.AddDays(-1);
            var clippedEnd = eventEnd > visibleEnd ? visibleEnd : eventEnd;
            if (clippedEnd < clippedStart)
            {
                continue;
            }

            var segmentStart = clippedStart;
            while (segmentStart <= clippedEnd)
            {
                var offset = segmentStart.DayNumber - visibleRange.StartDate.DayNumber;
                var gridRowEnd = visibleRange.StartDate
                    .AddDays(((offset / daysPerRow) + 1) * daysPerRow - 1);
                var segmentEnd = clippedEnd < gridRowEnd ? clippedEnd : gridRowEnd;
                candidates.Add(new AllDayCandidate(
                    calendarEvent,
                    eventStart,
                    eventEnd,
                    segmentStart,
                    segmentEnd));
                segmentStart = segmentEnd.AddDays(1);
            }
        }

        var result = new List<AllDayLayoutItem>();
        foreach (var gridRow in candidates
                     .GroupBy(item =>
                         (item.StartDate.DayNumber - visibleRange.StartDate.DayNumber) / daysPerRow)
                     .OrderBy(group => group.Key))
        {
            var rowEnds = new List<DateOnly>();
            foreach (var candidate in gridRow
                         .OrderBy(item => item.StartDate)
                         .ThenByDescending(item => item.EndDate.DayNumber - item.StartDate.DayNumber)
                         .ThenBy(item => item.Event.SourceOrder)
                         .ThenBy(item => item.Event.Id))
            {
                var row = rowEnds.FindIndex(end => end < candidate.StartDate);
                if (row < 0)
                {
                    row = rowEnds.Count;
                    rowEnds.Add(candidate.EndDate);
                }
                else
                {
                    rowEnds[row] = candidate.EndDate;
                }

                result.Add(new AllDayLayoutItem(
                    candidate.Event.Id,
                    candidate.Event.Title,
                    candidate.StartDate,
                    candidate.EndDate,
                    row,
                    candidate.EventStart < visibleRange.StartDate,
                    candidate.EventEnd >= visibleRange.EndDateExclusive,
                    candidate.StartDate > candidate.EventStart,
                    candidate.EndDate < candidate.EventEnd,
                    candidate.Event.SourceOrder));
            }
        }

        return result;
    }

    private sealed record AllDayCandidate(
        CalendarRenderEvent Event,
        DateOnly EventStart,
        DateOnly EventEnd,
        DateOnly StartDate,
        DateOnly EndDate);
}

public sealed record TimeGridMetrics
{
    public TimeGridMetrics(
        int visibleStartMinute = 0,
        int visibleEndMinute = CalendarMetrics.MinutesPerDay,
        int minimumDisplayMinutes = CalendarMetrics.DefaultMinimumTimedDisplayMinutes)
    {
        if (visibleStartMinute < 0 || visibleStartMinute >= CalendarMetrics.MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleStartMinute));
        }

        if (visibleEndMinute <= visibleStartMinute || visibleEndMinute > CalendarMetrics.MinutesPerDay)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleEndMinute));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumDisplayMinutes);
        VisibleStartMinute = visibleStartMinute;
        VisibleEndMinute = visibleEndMinute;
        MinimumDisplayMinutes = minimumDisplayMinutes;
    }

    public int VisibleStartMinute { get; }

    public int VisibleEndMinute { get; }

    public int MinimumDisplayMinutes { get; }

    public int VisibleDurationMinutes => VisibleEndMinute - VisibleStartMinute;

    public static TimeGridMetrics FullDay { get; } = new();
}

public static class TimeGridCoordinateConverter
{
    public static double MinuteToPercentage(double minute, TimeGridMetrics? metrics = null)
    {
        metrics ??= TimeGridMetrics.FullDay;
        var clipped = Math.Clamp(minute, metrics.VisibleStartMinute, metrics.VisibleEndMinute);
        return (clipped - metrics.VisibleStartMinute) * 100d / metrics.VisibleDurationMinutes;
    }

    public static double DurationToPercentage(double durationMinutes, TimeGridMetrics? metrics = null)
    {
        metrics ??= TimeGridMetrics.FullDay;
        ArgumentOutOfRangeException.ThrowIfNegative(durationMinutes);
        return durationMinutes * 100d / metrics.VisibleDurationMinutes;
    }

    public static double PercentageToMinute(double percentage, TimeGridMetrics? metrics = null)
    {
        metrics ??= TimeGridMetrics.FullDay;
        var clipped = Math.Clamp(percentage, 0, 100);
        return metrics.VisibleStartMinute + clipped * metrics.VisibleDurationMinutes / 100d;
    }
}

public static class CurrentTimeIndicatorLayout
{
    public static double? CalculateTopPercentage(
        DateOnly columnDate,
        DateTime localNow,
        TimeGridMetrics? metrics = null)
    {
        metrics ??= TimeGridMetrics.FullDay;
        if (columnDate != DateOnly.FromDateTime(localNow))
        {
            return null;
        }

        var minute = localNow.TimeOfDay.TotalMinutes;
        if (minute < metrics.VisibleStartMinute || minute >= metrics.VisibleEndMinute)
        {
            return null;
        }

        return TimeGridCoordinateConverter.MinuteToPercentage(minute, metrics);
    }
}

public readonly record struct TimedLayoutInput(
    Guid SegmentId,
    int StartMinute,
    int EndMinute,
    int SourceOrder = 0);

public sealed record TimeGridLayoutItem(
    Guid SegmentId,
    TimedEventPosition Position,
    TimedEventLayout Overlap);

public static partial class TimeGridLayoutEngine
{
    public static IReadOnlyList<TimeGridLayoutItem> Layout(
        IReadOnlyList<TimedLayoutInput> events,
        TimeGridMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        metrics ??= TimeGridMetrics.FullDay;

        var visibleEvents = events
            .Where(item => item.EndMinute > metrics.VisibleStartMinute &&
                item.StartMinute < metrics.VisibleEndMinute)
            .ToArray();
        var overlaps = OverlapLayoutEngine.Layout(visibleEvents, metrics);

        return visibleEvents
            .OrderBy(item => item.StartMinute)
            .ThenByDescending(item => item.EndMinute - item.StartMinute)
            .ThenBy(item => item.SourceOrder)
            .ThenBy(item => item.SegmentId)
            .Select(item => new TimeGridLayoutItem(
                item.SegmentId,
                CalculateClippedPosition(item.StartMinute, item.EndMinute, metrics),
                overlaps[item.SegmentId]))
            .ToArray();
    }

    public static TimedEventPosition CalculateClippedPosition(
        int startMinute,
        int endMinute,
        TimeGridMetrics? metrics = null)
    {
        metrics ??= TimeGridMetrics.FullDay;
        if (endMinute <= startMinute)
        {
            throw new ArgumentException("结束分钟必须晚于开始分钟。", nameof(endMinute));
        }

        var clippedStart = Math.Max(startMinute, metrics.VisibleStartMinute);
        var clippedEnd = Math.Min(endMinute, metrics.VisibleEndMinute);
        if (clippedEnd <= clippedStart)
        {
            throw new ArgumentOutOfRangeException(nameof(startMinute), "事件不在时间网格可见范围内。");
        }

        var displayEnd = Math.Min(
            metrics.VisibleEndMinute,
            Math.Max(clippedEnd, clippedStart + metrics.MinimumDisplayMinutes));
        return new TimedEventPosition(
            TimeGridCoordinateConverter.MinuteToPercentage(clippedStart, metrics),
            TimeGridCoordinateConverter.DurationToPercentage(displayEnd - clippedStart, metrics));
    }
}

public static partial class OverlapLayoutEngine
{
    public static IReadOnlyDictionary<Guid, TimedEventLayout> Layout(
        IReadOnlyList<TimedLayoutInput> events,
        TimeGridMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        metrics ??= TimeGridMetrics.FullDay;

        var result = new Dictionary<Guid, TimedEventLayout>();
        var group = new List<(Guid Id, int Column)>();
        var columnEnds = new List<int>();
        var groupEnd = int.MinValue;

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

        foreach (var item in events
                     .OrderBy(value => value.StartMinute)
                     .ThenByDescending(value => value.EndMinute - value.StartMinute)
                     .ThenBy(value => value.SourceOrder)
                     .ThenBy(value => value.SegmentId))
        {
            if (item.EndMinute <= item.StartMinute)
            {
                throw new ArgumentException("结束分钟必须晚于开始分钟。", nameof(events));
            }

            var start = Math.Max(item.StartMinute, metrics.VisibleStartMinute);
            var actualEnd = Math.Min(item.EndMinute, metrics.VisibleEndMinute);
            if (actualEnd <= start)
            {
                continue;
            }

            var visualEnd = Math.Min(
                metrics.VisibleEndMinute,
                Math.Max(actualEnd, start + metrics.MinimumDisplayMinutes));
            if (group.Count > 0 && start >= groupEnd)
            {
                CompleteGroup();
                groupEnd = int.MinValue;
            }

            var column = columnEnds.FindIndex(end => end <= start);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(visualEnd);
            }
            else
            {
                columnEnds[column] = visualEnd;
            }

            group.Add((item.SegmentId, column));
            groupEnd = Math.Max(groupEnd, visualEnd);
        }

        CompleteGroup();
        return result;
    }
}
