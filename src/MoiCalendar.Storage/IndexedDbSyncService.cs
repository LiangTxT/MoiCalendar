using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbEventChangeRepository(IndexedDbConnection connection) : ILocalEventChangeRepository
{
    public async Task ApplyImportAsync(
        IReadOnlyList<CalendarImportChange> changes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.InvokeAsync<object?>("applyCalendarImport", cancellationToken, changes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException("导入事件及同步操作失败：本地事务未完成。", exception);
        }
    }

    public Task<CalendarEvent> CreateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default) =>
        SaveAsync("创建事件及同步操作", "createEventWithSyncOperation", calendarEvent, operation, cancellationToken);

    public Task<CalendarEvent> UpdateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default) =>
        SaveAsync("更新事件及同步操作", "updateEventWithSyncOperation", calendarEvent, operation, cancellationToken);

    public async Task<bool> DeleteEventAsync(
        CalendarEvent deletedEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<bool>(
                "deleteEventWithSyncOperation",
                cancellationToken,
                deletedEvent,
                operation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException("删除事件及同步操作失败：本地事务未完成。", exception);
        }
    }

    private async Task<CalendarEvent> SaveAsync(
        string operationName,
        string identifier,
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await connection.InvokeAsync<CalendarEvent>(identifier, cancellationToken, calendarEvent, operation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException($"{operationName}失败：本地事务未完成。", exception);
        }
    }
}
