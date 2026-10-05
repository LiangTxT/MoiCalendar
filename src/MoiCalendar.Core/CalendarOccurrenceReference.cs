namespace MoiCalendar.Core;

public sealed record CalendarOccurrenceReference(Guid Id, DateTimeOffset? StartUtc = null, DateTimeOffset? EndUtc = null);
