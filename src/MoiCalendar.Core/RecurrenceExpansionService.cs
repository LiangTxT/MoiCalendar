namespace MoiCalendar.Core;

public interface IRecurrenceExpansionService
{
    IReadOnlyList<CalendarEvent> Expand(
        IEnumerable<CalendarEvent> calendarEvents,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc);
}

public sealed class RecurrenceExpansionService : IRecurrenceExpansionService
{
    private const int MaximumPeriodIterations = 1_000_000;

    public IReadOnlyList<CalendarEvent> Expand(
        IEnumerable<CalendarEvent> calendarEvents,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        ArgumentNullException.ThrowIfNull(calendarEvents);
        if (rangeEndUtc <= rangeStartUtc)
        {
            throw new ArgumentException("查询结束时间必须晚于开始时间。", nameof(rangeEndUtc));
        }

        var expanded = new List<CalendarEvent>();
        foreach (var calendarEvent in calendarEvents.Where(item => item.DeletedAtUtc is null))
        {
            if (string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule))
            {
                if (Overlaps(calendarEvent.StartUtc, calendarEvent.EndUtc, rangeStartUtc, rangeEndUtc))
                {
                    expanded.Add(calendarEvent);
                }

                continue;
            }

            ExpandRecurring(calendarEvent, rangeStartUtc, rangeEndUtc, expanded);
        }

        return expanded
            .OrderBy(item => item.StartUtc)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static void ExpandRecurring(
        CalendarEvent master,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        ICollection<CalendarEvent> destination)
    {
        if (master.EndUtc <= master.StartUtc)
        {
            throw new RecurrenceRuleException("重复主事件的结束时间必须晚于开始时间。");
        }

        var rule = RecurrenceRuleParser.Parse(master.RecurrenceRule!);
        var timeZone = ResolveTimeZone(master.TimeZoneId);
        var masterLocalStart = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(master.StartUtc, timeZone).DateTime,
            DateTimeKind.Unspecified);
        var masterLocalEnd = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTime(master.EndUtc, timeZone).DateTime,
            DateTimeKind.Unspecified);
        var wallClockDuration = masterLocalEnd - masterLocalStart;
        if (wallClockDuration <= TimeSpan.Zero)
        {
            wallClockDuration = master.EndUtc - master.StartUtc;
        }

        var localRangeStart = TimeZoneInfo.ConvertTime(rangeStartUtc, timeZone).DateTime;
        var localRangeEnd = TimeZoneInfo.ConvertTime(rangeEndUtc, timeZone).DateTime;
        var searchStart = AddClamped(
            AddClamped(localRangeStart, -wallClockDuration),
            -TimeSpan.FromDays(1));
        var searchEnd = AddClamped(localRangeEnd, TimeSpan.FromDays(1));

        var occurrenceNumber = 0;
        var enumerationStart = rule.Count is null ? searchStart : masterLocalStart;
        foreach (var localStart in EnumerateStarts(masterLocalStart, enumerationStart, searchEnd, rule))
        {
            if (!TryConvertLocalToUtc(localStart, timeZone, out var occurrenceStartUtc))
            {
                continue;
            }

            if (rule.Until is { } until && !until.Includes(localStart, occurrenceStartUtc))
            {
                break;
            }

            occurrenceNumber++;
            if (rule.Count is { } count && occurrenceNumber > count)
            {
                break;
            }

            var localEnd = AddClamped(localStart, wallClockDuration);
            if (!TryConvertLocalToUtc(localEnd, timeZone, out var occurrenceEndUtc))
            {
                continue;
            }

            if (Overlaps(occurrenceStartUtc, occurrenceEndUtc, rangeStartUtc, rangeEndUtc))
            {
                destination.Add(master with
                {
                    StartUtc = occurrenceStartUtc,
                    EndUtc = occurrenceEndUtc
                });
            }
        }
    }

    private static IEnumerable<DateTime> EnumerateStarts(
        DateTime masterStart,
        DateTime searchStart,
        DateTime searchEnd,
        ParsedRecurrenceRule rule) => rule.Frequency switch
        {
            RecurrenceFrequency.Daily => EnumerateDaily(masterStart, searchStart, searchEnd, rule),
            RecurrenceFrequency.Weekly => EnumerateWeekly(masterStart, searchStart, searchEnd, rule),
            RecurrenceFrequency.Monthly => EnumerateMonthly(masterStart, searchStart, searchEnd, rule),
            RecurrenceFrequency.Yearly => EnumerateYearly(masterStart, searchStart, searchEnd, rule),
            _ => throw new RecurrenceRuleException("重复频率不受支持。")
        };

    private static IEnumerable<DateTime> EnumerateDaily(
        DateTime masterStart,
        DateTime searchStart,
        DateTime searchEnd,
        ParsedRecurrenceRule rule)
    {
        var index = rule.Count is null
            ? Math.Max(0L, (long)Math.Floor((searchStart - masterStart).TotalDays / rule.Interval) - 1)
            : 0L;
        var iterations = 0;
        while (iterations++ < MaximumPeriodIterations)
        {
            DateTime candidate;
            try
            {
                candidate = masterStart.AddDays(checked(index * rule.Interval));
            }
            catch (ArgumentOutOfRangeException)
            {
                yield break;
            }

            if (candidate >= searchEnd)
            {
                yield break;
            }

            if (candidate >= searchStart)
            {
                yield return candidate;
            }

            index++;
        }

        throw SafetyLimitExceeded();
    }

    private static IEnumerable<DateTime> EnumerateWeekly(
        DateTime masterStart,
        DateTime searchStart,
        DateTime searchEnd,
        ParsedRecurrenceRule rule)
    {
        var masterWeekStart = masterStart.Date.AddDays(-RecurrenceRuleParser.ToMondayBasedIndex(masterStart.DayOfWeek));
        var weekIndex = rule.Count is null
            ? Math.Max(0L, (long)Math.Floor((searchStart.Date - masterWeekStart).TotalDays / (7 * rule.Interval)) - 1)
            : 0L;
        var weekdays = rule.ByDay.Count == 0 ? [masterStart.DayOfWeek] : rule.ByDay;
        var iterations = 0;

        while (iterations++ < MaximumPeriodIterations)
        {
            DateTime weekStart;
            try
            {
                weekStart = masterWeekStart.AddDays(checked(weekIndex * 7 * rule.Interval));
            }
            catch (ArgumentOutOfRangeException)
            {
                yield break;
            }

            if (weekStart >= searchEnd)
            {
                yield break;
            }

            var candidates = weekdays
                .Select(day => weekStart.AddDays(RecurrenceRuleParser.ToMondayBasedIndex(day)) + masterStart.TimeOfDay)
                .Where(candidate => candidate >= masterStart)
                .Append(weekIndex == 0 ? masterStart : DateTime.MinValue)
                .Where(candidate => candidate != DateTime.MinValue)
                .Distinct()
                .OrderBy(candidate => candidate);

            foreach (var candidate in candidates)
            {
                if (candidate >= searchEnd)
                {
                    yield break;
                }

                if (candidate >= searchStart)
                {
                    yield return candidate;
                }
            }

            weekIndex++;
        }

        throw SafetyLimitExceeded();
    }

    private static IEnumerable<DateTime> EnumerateMonthly(
        DateTime masterStart,
        DateTime searchStart,
        DateTime searchEnd,
        ParsedRecurrenceRule rule)
    {
        var monthDifference = (searchStart.Year - masterStart.Year) * 12L + searchStart.Month - masterStart.Month;
        var index = rule.Count is null ? Math.Max(0L, monthDifference / rule.Interval - 1) : 0L;
        var iterations = 0;

        while (iterations++ < MaximumPeriodIterations)
        {
            if (!TryCreateMonthlyCandidate(masterStart, index, rule.Interval, out var candidate, out var exhausted))
            {
                if (exhausted)
                {
                    yield break;
                }

                index++;
                continue;
            }

            if (candidate >= searchEnd)
            {
                yield break;
            }

            if (candidate >= searchStart)
            {
                yield return candidate;
            }

            index++;
        }

        throw SafetyLimitExceeded();
    }

    private static IEnumerable<DateTime> EnumerateYearly(
        DateTime masterStart,
        DateTime searchStart,
        DateTime searchEnd,
        ParsedRecurrenceRule rule)
    {
        var yearDifference = (long)searchStart.Year - masterStart.Year;
        var index = rule.Count is null ? Math.Max(0L, yearDifference / rule.Interval - 1) : 0L;
        var iterations = 0;

        while (iterations++ < MaximumPeriodIterations)
        {
            if (!TryCreateYearlyCandidate(masterStart, index, rule.Interval, out var candidate, out var exhausted))
            {
                if (exhausted)
                {
                    yield break;
                }

                index++;
                continue;
            }

            if (candidate >= searchEnd)
            {
                yield break;
            }

            if (candidate >= searchStart)
            {
                yield return candidate;
            }

            index++;
        }

        throw SafetyLimitExceeded();
    }

    private static bool TryCreateMonthlyCandidate(
        DateTime masterStart,
        long index,
        int interval,
        out DateTime candidate,
        out bool exhausted)
    {
        var absoluteMonth = (masterStart.Year - 1) * 12L + masterStart.Month - 1 + index * interval;
        var year = absoluteMonth / 12 + 1;
        var month = absoluteMonth % 12 + 1;
        exhausted = year is < 1 or > 9999;
        if (exhausted || masterStart.Day > DateTime.DaysInMonth((int)year, (int)month))
        {
            candidate = default;
            return false;
        }

        candidate = new DateTime((int)year, (int)month, masterStart.Day) + masterStart.TimeOfDay;
        return true;
    }

    private static bool TryCreateYearlyCandidate(
        DateTime masterStart,
        long index,
        int interval,
        out DateTime candidate,
        out bool exhausted)
    {
        var year = masterStart.Year + index * interval;
        exhausted = year is < 1 or > 9999;
        if (exhausted || masterStart.Day > DateTime.DaysInMonth((int)year, masterStart.Month))
        {
            candidate = default;
            return false;
        }

        candidate = new DateTime((int)year, masterStart.Month, masterStart.Day) + masterStart.TimeOfDay;
        return true;
    }

    private static bool TryConvertLocalToUtc(
        DateTime localDateTime,
        TimeZoneInfo timeZone,
        out DateTimeOffset utc)
    {
        localDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(localDateTime))
        {
            utc = default;
            return false;
        }

        try
        {
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone), TimeSpan.Zero);
            return true;
        }
        catch (ArgumentException)
        {
            utc = default;
            return false;
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new RecurrenceRuleException("重复主事件缺少时区。");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new RecurrenceRuleException("重复主事件的时区无效。");
        }
    }

    private static bool Overlaps(
        DateTimeOffset eventStart,
        DateTimeOffset eventEnd,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd) => eventStart < rangeEnd && eventEnd > rangeStart;

    private static DateTime AddClamped(DateTime value, TimeSpan offset)
    {
        try
        {
            return new DateTime(checked(value.Ticks + offset.Ticks), DateTimeKind.Unspecified);
        }
        catch (OverflowException)
        {
            return offset < TimeSpan.Zero ? DateTime.MinValue : DateTime.MaxValue;
        }
        catch (ArgumentOutOfRangeException)
        {
            return offset < TimeSpan.Zero ? DateTime.MinValue : DateTime.MaxValue;
        }
    }

    private static RecurrenceRuleException SafetyLimitExceeded() =>
        new("重复规则展开超过安全上限，已停止以避免无限循环。");
}
