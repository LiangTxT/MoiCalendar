namespace MoiCalendar.Core;

public interface ILocalDataOperationLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}

public interface IRestoreSyncGuard
{
    Task<bool> IsSyncBlockedAsync(CancellationToken cancellationToken = default);

    Task AllowSyncAsync(CancellationToken cancellationToken = default);
}

public sealed class NoOpLocalDataOperationLock : ILocalDataOperationLock
{
    public static NoOpLocalDataOperationLock Instance { get; } = new();

    private NoOpLocalDataOperationLock()
    {
    }

    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAsyncDisposable>(NoOpLease.Instance);
    }

    private sealed class NoOpLease : IAsyncDisposable
    {
        public static NoOpLease Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class RestoreSyncBlockedException : Exception
{
    public RestoreSyncBlockedException()
        : base("备份恢复后同步仍处于保护状态。请先在设置的备份区域确认允许同步。")
    {
    }
}
