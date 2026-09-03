namespace MoiCalendar.Core;

public interface ILocalEventChangeRepository
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

    Task ApplyImportAsync(
        IReadOnlyList<CalendarImportChange> changes,
        CancellationToken cancellationToken = default);
}
