using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbOperationalDiagnosticRepository(IndexedDbConnection connection)
    : IOperationalDiagnosticRepository
{
    public Task AddAsync(
        OperationalDiagnosticEvent diagnosticEvent,
        int retentionLimit,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            "写入运行诊断事件",
            "addOperationalDiagnosticEvent",
            cancellationToken,
            OperationalDiagnosticSanitizer.Sanitize(diagnosticEvent),
            retentionLimit);

    public async Task<IReadOnlyList<OperationalDiagnosticEvent>> GetRecentAsync(
        CancellationToken cancellationToken = default) =>
        await InvokeAsync<OperationalDiagnosticEvent[]>(
            "读取运行诊断事件",
            "getOperationalDiagnosticEvents",
            cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            "清除运行诊断事件",
            "clearOperationalDiagnosticEvents",
            cancellationToken);

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
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException(
                $"{operation}失败：浏览器本地数据库操作未完成。",
                exception);
        }
    }
}

public sealed class IndexedDbLocalStorageHealthService(IndexedDbConnection connection)
    : ILocalStorageHealthService
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<bool>(
                "checkIndexedDbHealth",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or JSException)
        {
            return false;
        }
    }
}
