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

    /// <summary>
    /// RFC 5545 RRULE 属性值；为空表示事件不重复。
    /// </summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>
    /// 从外部日历导入时保留的原始 UID；本地创建的事件为空。
    /// </summary>
    public string? ExternalUid { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? DeletedAtUtc { get; init; }
}
