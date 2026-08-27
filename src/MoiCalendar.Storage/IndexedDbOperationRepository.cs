using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbOperationRepository(IndexedDbConnection connection) : IOperationRepository
{
    public Task<SyncOperation> AddAsync(SyncOperation operation, CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOperation>("保存同步操作", "addSyncOperation", cancellationToken, operation);

    public Task<SyncOperation?> GetByIdAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOperation?>("读取同步操作", "getSyncOperationById", cancellationToken, operationId);

    public async Task<IReadOnlyList<SyncOperation>> GetByStatusAsync(
        SyncOperationStatus status,
        CancellationToken cancellationToken = default) =>
        await InvokeAsync<SyncOperation[]>("查询同步操作", "getSyncOperationsByStatus", cancellationToken, status);

    public Task<SyncOperation> UpdateStatusAsync(
        Guid operationId,
        SyncOperationStatus status,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOperation>("更新同步操作状态", "updateSyncOperationStatus", cancellationToken, operationId, status);

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
