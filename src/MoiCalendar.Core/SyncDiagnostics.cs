namespace MoiCalendar.Core;

public sealed record SyncStatus
{
    public required string ActiveProvider { get; init; }

    public required bool IsSyncing { get; init; }

    public DateTimeOffset? LastSyncStartedAtUtc { get; init; }

    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; init; }

    public DateTimeOffset? LastFailedSyncAtUtc { get; init; }

    public required int PendingOperationCount { get; init; }

    public required int FailedOperationCount { get; init; }

    public string? LastErrorSummary { get; init; }
}

public sealed record SyncStatusState
{
    public DateTimeOffset? LastSyncStartedAtUtc { get; init; }

    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; init; }

    public DateTimeOffset? LastFailedSyncAtUtc { get; init; }

    public string? LastErrorSummary { get; init; }
}

public enum SyncLogSeverity
{
    Information,
    Warning,
    Error
}

public enum SyncLogStage
{
    Synchronize,
    Push,
    Pull,
    Retry
}

public sealed record SyncLogEntry
{
    public required Guid Id { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public required SyncLogSeverity Severity { get; init; }

    public required SyncLogStage Stage { get; init; }

    public required string Provider { get; init; }

    public Guid? OperationId { get; init; }

    public required string Message { get; init; }

    public string? ErrorCode { get; init; }
}

public interface ISyncDiagnosticsService
{
    Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<SyncResult> RetryFailedAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncLogEntry>> GetLogEntriesAsync(CancellationToken cancellationToken = default);

    Task ClearLogAsync(CancellationToken cancellationToken = default);
}

public interface ISyncLogRepository
{
    Task AddAsync(SyncLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncLogEntry>> GetRecentAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ISyncStatusRepository
{
    Task<SyncStatusState> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SyncStatusState state, CancellationToken cancellationToken = default);
}
