namespace MoiCalendar.Core;

public interface ISyncService
{
    Task<CalendarEvent> CreateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> UpdateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteEventAsync(
        CalendarEvent deletedEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default);
}
