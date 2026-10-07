namespace MoiCalendar.Core;

public sealed record CalendarEvent
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Location { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset EndUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required bool IsAllDay { get; init; }

    /// <summary>主题事件配色编号；旧记录默认使用雾蓝。</summary>
    public int ColorIndex { get; init; } = 1;
    public int? ReminderMinutesBeforeStart { get; init; }
    public string? ReminderTimeZoneId { get; init; }
    public int AllDayReminderMinuteOfDay { get; init; } = 420;

    /// <summary>
    /// RFC 5545 RRULE 属性值；为空表示事件不重复。
    /// </summary>
    public string? RecurrenceRule { get; init; }
    /// <summary>排除的出现起点。保存为 UTC 瞬间，跨日事件的所有片段共用同一起点。</summary>
    public DateTimeOffset[] ExcludedOccurrenceStartsUtc { get; init; } = [];

    /// <summary>
    /// 从外部日历导入时保留的原始 UID；本地创建的事件为空。
    /// </summary>
    public string? ExternalUid { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? DeletedAtUtc { get; init; }

    // 排除记录按值比较，避免 JSON/IndexedDB 往返后相同事件被误判为不同。
    public bool Equals(CalendarEvent? other) => other is not null &&
        Id == other.Id && Title == other.Title && Description == other.Description &&
        Location == other.Location && StartUtc == other.StartUtc && EndUtc == other.EndUtc &&
        TimeZoneId == other.TimeZoneId && IsAllDay == other.IsAllDay && ColorIndex == other.ColorIndex &&
        ReminderMinutesBeforeStart == other.ReminderMinutesBeforeStart && ReminderTimeZoneId == other.ReminderTimeZoneId &&
        AllDayReminderMinuteOfDay == other.AllDayReminderMinuteOfDay &&
        RecurrenceRule == other.RecurrenceRule && ExternalUid == other.ExternalUid &&
        CreatedAtUtc == other.CreatedAtUtc && UpdatedAtUtc == other.UpdatedAtUtc &&
        DeletedAtUtc == other.DeletedAtUtc &&
        (ExcludedOccurrenceStartsUtc ?? []).ToHashSet().SetEquals(other.ExcludedOccurrenceStartsUtc ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Title);
        hash.Add(Description);
        hash.Add(Location);
        hash.Add(StartUtc);
        hash.Add(EndUtc);
        hash.Add(TimeZoneId);
        hash.Add(IsAllDay);
        hash.Add(ColorIndex);
        hash.Add(ReminderMinutesBeforeStart);
        hash.Add(ReminderTimeZoneId);
        hash.Add(AllDayReminderMinuteOfDay);
        hash.Add(RecurrenceRule);
        hash.Add(ExternalUid);
        hash.Add(CreatedAtUtc);
        hash.Add(UpdatedAtUtc);
        hash.Add(DeletedAtUtc);
        foreach (var start in (ExcludedOccurrenceStartsUtc ?? []).Distinct().Order()) hash.Add(start);
        return hash.ToHashCode();
    }
}
