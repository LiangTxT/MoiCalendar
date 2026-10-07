namespace MoiCalendar.Core;

/// <summary>External operation-file sync is LWW; same-series deletions are grow-only.</summary>
public static class RecurrenceExclusionMerge
{
    public static CalendarEvent Merge(CalendarEvent preferred, CalendarEvent other)
    {
        if (preferred.DeletedAtUtc is not null || other.DeletedAtUtc is not null ||
            string.IsNullOrWhiteSpace(preferred.RecurrenceRule) ||
            preferred.StartUtc != other.StartUtc || preferred.TimeZoneId != other.TimeZoneId ||
            preferred.IsAllDay != other.IsAllDay || preferred.RecurrenceRule != other.RecurrenceRule)
            return preferred;
        return preferred with { ExcludedOccurrenceStartsUtc = [.. (preferred.ExcludedOccurrenceStartsUtc ?? [])
            .Union(other.ExcludedOccurrenceStartsUtc ?? []).Order()] };
    }
}
