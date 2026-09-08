namespace MoiCalendar.Core;

public interface IRemoteSyncApplyRepository
{
    Task ApplyAsync(
        CalendarEvent? calendarEvent,
        SyncOperation operation,
        bool operationAlreadyExists,
        CancellationToken cancellationToken = default);
}
