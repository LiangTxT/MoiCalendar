using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbAccountDeletionLocalRepository(IndexedDbConnection connection)
    : IAccountDeletionLocalRepository
{
    public async Task ResetAfterAccountDeletionAsync(
        string accountId,
        bool removeLocalData,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        try
        {
            await connection.InvokeAsync<object?>(
                "resetAfterCloudAccountDeletion",
                cancellationToken,
                accountId,
                removeLocalData);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException(
                "云账户删除后的本地清理事务未完成。",
                exception);
        }
    }
}
