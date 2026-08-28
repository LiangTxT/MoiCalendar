using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbSyncLogRepository(IndexedDbConnection connection) : ISyncLogRepository
{
    private const int RetentionLimit = 200;

    public Task AddAsync(SyncLogEntry entry, CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            "写入同步日志",
            "addSyncLogEntry",
            cancellationToken,
            SyncLogSanitizer.Sanitize(entry),
            RetentionLimit);

    public async Task<IReadOnlyList<SyncLogEntry>> GetRecentAsync(
        CancellationToken cancellationToken = default) =>
        await InvokeAsync<SyncLogEntry[]>("读取同步日志", "getSyncLogEntries", cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>("清除同步日志", "clearSyncLogEntries", cancellationToken);

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            return await connection.InvokeAsync<T>(identifier, cancellationToken, arguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}

public sealed class IndexedDbSyncStatusRepository(IndexedDbConnection connection) : ISyncStatusRepository
{
    public async Task<SyncStatusState> GetAsync(CancellationToken cancellationToken = default) =>
        await InvokeAsync<SyncStatusState?>("读取同步状态", "getSyncStatusState", cancellationToken)
            ?? new SyncStatusState();

    public Task SaveAsync(SyncStatusState state, CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>("保存同步状态", "saveSyncStatusState", cancellationToken, state);

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            return await connection.InvokeAsync<T>(identifier, cancellationToken, arguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}
