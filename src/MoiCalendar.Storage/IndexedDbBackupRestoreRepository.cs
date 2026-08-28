using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbBackupRestoreRepository(IndexedDbConnection connection)
    : IBackupRestoreRepository
{
    public async Task ReplaceAllEventsAndResetSyncAsync(
        IReadOnlyList<CalendarEvent> calendarEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvents);
        try
        {
            await connection.InvokeAsync<object?>(
                "replaceAllEventsAndResetSync",
                cancellationToken,
                calendarEvents);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or JSException)
        {
            throw new BackupRestoreRepositoryException(
                "替换本地日历失败：IndexedDB 事务未完成。",
                exception);
        }
    }
}
