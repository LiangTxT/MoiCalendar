namespace MoiCalendar.Core;

public interface ISyncService
{
    Task<SyncResult> PushAsync(CancellationToken cancellationToken = default);

    Task<SyncResult> PullAsync(CancellationToken cancellationToken = default);

    Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
}

public sealed record SyncResult(int PushedCount, int DownloadedCount, int AppliedCount)
{
    public static SyncResult Empty { get; } = new(0, 0, 0);
}
