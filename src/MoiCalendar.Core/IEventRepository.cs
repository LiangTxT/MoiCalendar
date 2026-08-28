namespace MoiCalendar.Core;

public interface IEventRepository
{
    Task<CalendarEvent> CreateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    Task<CalendarEvent> UpdateAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid id,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CalendarEvent?> GetByIdIncludingDeletedAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> UpsertAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarEvent>> GetAllIncludingDeletedAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarEvent>> GetByRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default);
}
