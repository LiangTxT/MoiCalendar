using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbLocalDataSafety(IndexedDbConnection connection)
    : ILocalDataOperationLock, IRestoreSyncGuard
{
    public async Task<IAsyncDisposable> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string leaseId;
        try
        {
            // Do not cancel the JS invocation while it is queued: an abandoned Web Lock
            // request could otherwise acquire later without a corresponding release.
            leaseId = await connection.InvokeAsync<string>(
                "acquireExclusiveOperationLock",
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is JSException or NotSupportedException)
        {
            throw new SyncOperationException("无法获取安全的本地数据操作锁。", exception);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await ReleaseAsync(leaseId);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new Lease(this, leaseId);
    }

    public async Task<bool> IsSyncBlockedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<bool>(
                "isSyncBlockedAfterRestore",
                cancellationToken);
        }
        catch (Exception exception) when (exception is JSException or NotSupportedException)
        {
            throw new SyncOperationException("无法读取恢复后的同步保护状态。", exception);
        }
    }

    public async Task AllowSyncAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.InvokeAsync<object?>("allowSyncAfterRestore", cancellationToken);
        }
        catch (Exception exception) when (exception is JSException or NotSupportedException)
        {
            throw new SyncOperationException("无法解除恢复后的同步保护。", exception);
        }
    }

    private async Task ReleaseAsync(string leaseId)
    {
        try
        {
            await connection.InvokeAsync<object?>(
                "releaseExclusiveOperationLock",
                CancellationToken.None,
                leaseId);
        }
        catch (Exception exception) when (exception is JSException or NotSupportedException)
        {
            throw new SyncOperationException("无法释放本地数据操作锁。", exception);
        }
    }

    private sealed class Lease(IndexedDbLocalDataSafety owner, string leaseId) : IAsyncDisposable
    {
        private bool released;

        public async ValueTask DisposeAsync()
        {
            if (released)
            {
                return;
            }

            released = true;
            await owner.ReleaseAsync(leaseId);
        }
    }
}
