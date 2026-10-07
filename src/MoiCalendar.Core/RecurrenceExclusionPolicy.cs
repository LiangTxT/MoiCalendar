namespace MoiCalendar.Core;

public static class RecurrenceExclusionPolicy
{
    // Preserve deleted calendar dates when moving a series. Validation must use
    // an unexcluded master, otherwise every previously deleted occurrence is filtered out.
    public static DateTimeOffset[] Remap(CalendarEvent previous, CalendarEvent updated,
        IRecurrenceExpansionService expansion)
    {
        var exclusions = previous.ExcludedOccurrenceStartsUtc ?? [];
        if (exclusions.Length == 0) return [];
        if (previous.StartUtc == updated.StartUtc && previous.TimeZoneId == updated.TimeZoneId &&
            previous.IsAllDay == updated.IsAllDay && previous.RecurrenceRule == updated.RecurrenceRule)
            return [.. exclusions.Distinct().Order()];
        if (string.IsNullOrWhiteSpace(updated.RecurrenceRule)) return [];
        var oldZone = CalendarTimeZone.Resolve(previous.TimeZoneId);
        var newZone = CalendarTimeZone.Resolve(updated.TimeZoneId);
        var newTime = TimeZoneInfo.ConvertTime(updated.StartUtc, newZone).TimeOfDay;
        var master = updated with { ExcludedOccurrenceStartsUtc = [] };
        var result = new HashSet<DateTimeOffset>();
        foreach (var excluded in exclusions)
        {
            var date = TimeZoneInfo.ConvertTime(excluded, oldZone).Date;
            var local = DateTime.SpecifyKind(date + newTime, DateTimeKind.Unspecified);
            if (newZone.IsInvalidTime(local)) continue;
            var offset = newZone.IsAmbiguousTime(local)
                ? newZone.GetAmbiguousTimeOffsets(local).Max() : newZone.GetUtcOffset(local);
            var candidate = new DateTimeOffset(local, offset).ToUniversalTime();
            if (candidate < DateTimeOffset.MaxValue &&
                expansion.Expand([master], candidate, candidate.AddTicks(1)).Any(x => x.StartUtc == candidate))
                result.Add(candidate);
        }
        return [.. result.Order()];
    }
}
