namespace MoiCalendar.Core;

public sealed record CalendarReminder(string Key, Guid EventId, string Title, DateTimeOffset OccurrenceStartUtc,
    DateTimeOffset DueUtc, bool IsAllDay, DateOnly Date);

public static class ReminderPolicy
{
    public static readonly int[] Minutes = [0, 5, 15, 30, 60, 1440];
    public static void Validate(int? minutes)
    {
        if (minutes is { } value && !Minutes.Contains(value)) throw new ArgumentException("请选择内置的提醒时间。");
    }
    public static string Label(int? minutes) => minutes switch
    {
        null => "不提醒", 0 => "开始时", 60 => "提前 1 小时", 1440 => "提前 1 天", _ => $"提前 {minutes} 分钟"
    };
    public static void ValidateAllDayTime(int minute)
    {
        if (minute is < 0 or > 1439) throw new ArgumentException("全天提醒时间必须在 00:00 到 23:59 之间。");
    }
    public static string? NormalizeTimeZone(CalendarEventDraft draft)
    {
        if (draft.ReminderMinutesBeforeStart is null) return null;
        var id = draft.IsAllDay ? draft.ReminderTimeZoneId ?? draft.TimeZoneId : draft.TimeZoneId;
        _ = CalendarTimeZone.Resolve(id);
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }
    public static DateTimeOffset DueAt(CalendarEvent occurrence)
    {
        var minutes = occurrence.ReminderMinutesBeforeStart ?? throw new ArgumentException("日程未设置提醒。");
        Validate(minutes);
        if (!occurrence.IsAllDay) return occurrence.StartUtc.AddMinutes(-minutes);
        var date = TimeZoneInfo.ConvertTime(occurrence.StartUtc, CalendarTimeZone.Resolve(occurrence.TimeZoneId)).Date;
        ValidateAllDayTime(occurrence.AllDayReminderMinuteOfDay);
        return CalendarTimeZone.ToUtc(date.AddMinutes(occurrence.AllDayReminderMinuteOfDay - minutes),
            CalendarTimeZone.Resolve(occurrence.ReminderTimeZoneId ?? "UTC"));
    }
    public static string Key(CalendarEvent occurrence) =>
        $"{occurrence.Id:D}:{occurrence.StartUtc.ToUnixTimeMilliseconds()}:{occurrence.ReminderMinutesBeforeStart}:{DueAt(occurrence).ToUnixTimeMilliseconds()}";
}

public sealed class CalendarReminderService(IEventRepository events, IRecurrenceExpansionService expansion)
{
    public async Task<IReadOnlyList<CalendarReminder>> GetDueAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (to <= from) return [];
        // Includes next-day advance alerts and all-day dates across UTC offsets.
        var start = from.AddDays(-2);
        var end = to.AddDays(2);
        var candidates = (await events.GetByRangeAsync(start, end, cancellationToken))
            .Concat(await events.GetRecurringMastersAsync(cancellationToken)).DistinctBy(e => e.Id);
        var result = new List<CalendarReminder>();
        foreach (var master in candidates.Where(e => e.DeletedAtUtc is null && e.ReminderMinutesBeforeStart is not null))
        {
            try
            {
                foreach (var occurrence in expansion.Expand([master], start, end))
                {
                    var due = ReminderPolicy.DueAt(occurrence);
                    if (due > from && due <= to)
                        result.Add(new(ReminderPolicy.Key(occurrence), master.Id, master.Title, occurrence.StartUtc, due, master.IsAllDay,
                            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurrence.StartUtc,
                                CalendarTimeZone.Resolve(occurrence.TimeZoneId)).Date)));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RecurrenceRuleException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { /* Isolate damaged records; normal calendar diagnostics expose these separately. */ }
        }
        return result.OrderBy(r => r.DueUtc).ToArray();
    }
}

public interface IReminderStateStore
{
    Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<bool> ClaimAsync(string key, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string key, CancellationToken cancellationToken = default);
    Task<string?> GetPushOwnerAsync(CancellationToken cancellationToken = default);
    Task SetPushOwnerAsync(string? owner, CancellationToken cancellationToken = default);
}
