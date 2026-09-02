using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbBackupRestoreRepository(IndexedDbConnection connection)
    : IBackupRestoreRepository
{
    public async Task<BackupRestoreSafetySnapshot> ReplaceAllEventsAndResetSyncAsync(
        IReadOnlyList<CalendarEvent> calendarEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvents);
        try
        {
            return await connection.InvokeAsync<BackupRestoreSafetySnapshot>(
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

    public async Task<BackupRestoreSafetySnapshot?> GetSafetySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<BackupRestoreSafetySnapshot?>(
                "getRestoreSafetySnapshot",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or JSException)
        {
            throw new BackupRestoreRepositoryException(
                "读取本地恢复安全快照失败。",
                exception);
        }
    }

    public async Task<BackupRestoreResult> RestoreSafetySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<BackupRestoreResult>(
                "restoreLatestSafetySnapshot",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or JSException)
        {
            throw new BackupRestoreRepositoryException(
                "恢复本地安全快照失败：IndexedDB 事务未完成。",
                exception);
        }
    }
}
