using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

public enum CloudSyncOutcome
{
    Succeeded,
    NotAuthenticated,
    Conflict,
    Failed,
    AccountMismatch
}

public enum CloudSyncStatus
{
    Idle,
    Syncing,
    Offline,
    AuthenticationRequired,
    Conflict,
    RetryScheduled,
    Error
}

public enum CloudSyncFailureKind
{
    Transient,
    AuthenticationRequired,
    Permanent
}

public sealed record CloudSyncConflict(
    Guid MutationId,
    Guid EntityId,
    string Code,
    long? CurrentServerRevision,
    long? BaseRevision = null,
    string? LocalPayload = null,
    CalendarEvent? RemoteEvent = null);

public sealed record CloudSyncResult(
    CloudSyncOutcome Outcome,
    CloudSyncStatus Status,
    int PushedCount,
    int PulledCount,
    long Cursor,
    IReadOnlyList<CloudSyncConflict>? Conflicts = null,
    DateTimeOffset? NextRetryAtUtc = null,
    string? Message = null)
{
    public bool IsSuccess => Outcome == CloudSyncOutcome.Succeeded;

    public CloudSyncConflict? Conflict => Conflicts?.FirstOrDefault();
}

public interface ICloudSyncService
{
    bool IsAvailable { get; }

    CloudSyncStatus CurrentStatus { get; }

    Task<CloudSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
}

public sealed record CloudMutationRequest(
    Guid MutationId,
    Guid DeviceId,
    Guid EntityId,
    SyncOperationType Operation,
    long? BaseRevision,
    string Payload);

public sealed record CloudMutationResponse(
    bool Applied,
    Guid MutationId,
    Guid EntityId,
    long? ServerRevision,
    string? ConflictCode = null,
    long? CurrentServerRevision = null,
    CalendarEvent? CurrentEntity = null);

public sealed record CloudChangeBatch(
    IReadOnlyList<CloudRemoteCalendarChange> Changes,
    long Cursor,
    bool HasMore);

public interface ICloudSyncTransport
{
    bool IsAvailable { get; }

    Task<CloudMutationResponse> PushAsync(
        CloudMutationRequest mutation,
        CancellationToken cancellationToken = default);

    Task<CloudChangeBatch> PullAsync(
        Guid deviceId,
        long afterRevision,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public interface ICloudSyncRetryPolicy
{
    int MaximumAttemptsPerOperation { get; }

    TimeSpan GetDelay(int totalFailedAttemptCount);
}

public interface ICloudSyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class CloudSyncTransportException : Exception
{
    public CloudSyncTransportException(
        string message,
        CloudSyncFailureKind failureKind = CloudSyncFailureKind.Transient,
        string errorCode = "transport_error",
        int? statusCode = null)
        : base(message)
    {
        FailureKind = failureKind;
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public CloudSyncTransportException(
        string message,
        Exception innerException,
        CloudSyncFailureKind failureKind = CloudSyncFailureKind.Transient,
        string errorCode = "transport_error",
        int? statusCode = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public CloudSyncFailureKind FailureKind { get; }

    public string ErrorCode { get; }

    public int? StatusCode { get; }
}
