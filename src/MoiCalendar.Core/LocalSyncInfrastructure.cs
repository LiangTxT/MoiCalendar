using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoiCalendar.Core;

public sealed record DeviceIdentity(Guid DeviceId, DateTimeOffset CreatedAtUtc)
{
    public static Guid NormalizeDeviceId(string value)
    {
        if (Guid.TryParse(value, out var deviceId))
        {
            return deviceId;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"MoiCalendar/device/{value}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed record SyncState
{
    public const string DefaultScope = "cloud";

    public required string Scope { get; init; }

    public long? LastSuccessfulServerRevision { get; init; }

    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; init; }
}

public sealed record CloudSyncBinding(string AccountId, DateTimeOffset BoundAtUtc);

public sealed record CloudRemoteCalendarChange(
    CalendarEvent CalendarEvent,
    long ServerRevision);

public enum SyncEntityType
{
    CalendarEvent
}

public enum SyncOutboxErrorCategory
{
    Transient,
    Authentication,
    Conflict,
    Permanent
}

public sealed record SyncOutboxEntry
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new(JsonSerializerDefaults.Web);

    public required Guid MutationId { get; init; }

    public required Guid DeviceId { get; init; }

    public required SyncEntityType EntityType { get; init; }

    public required Guid EntityId { get; init; }

    public required SyncOperationType Operation { get; init; }

    public long? BaseRevision { get; init; }

    public required string Payload { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    public string? LastError { get; init; }

    public SyncOutboxErrorCategory? LastErrorCategory { get; init; }

    public string? LastErrorCode { get; init; }

    public DateTimeOffset? NextAttemptAtUtc { get; init; }

    public string? ConflictCode { get; init; }

    public long? ConflictServerRevision { get; init; }

    public CalendarEvent? ConflictRemoteEvent { get; init; }

    public DateTimeOffset? ConflictDetectedAtUtc { get; init; }

    public static SyncOutboxEntry FromLocalCalendarOperation(SyncOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var calendarEvent = JsonSerializer.Deserialize<CalendarEvent>(
            operation.Payload,
            PayloadSerializerOptions);
        if (calendarEvent is null || calendarEvent.Id != operation.EntityId)
        {
            throw new SyncOperationException("无法创建本地同步发件箱记录：事件负载无效。");
        }

        return new SyncOutboxEntry
        {
            MutationId = operation.OperationId,
            DeviceId = DeviceIdentity.NormalizeDeviceId(operation.DeviceId),
            EntityType = SyncEntityType.CalendarEvent,
            EntityId = operation.EntityId,
            Operation = operation.OperationType,
            BaseRevision = null,
            Payload = JsonSerializer.Serialize(calendarEvent, PayloadSerializerOptions),
            CreatedAtUtc = operation.TimestampUtc.ToUniversalTime(),
            AttemptCount = 0
        };
    }
}

public interface ISyncOutboxRepository
{
    Task<SyncOutboxEntry?> GetByIdAsync(
        Guid mutationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SyncOutboxEntry>> GetReadyAsync(
        DateTimeOffset readyAtUtc,
        int maximumCount = 100,
        CancellationToken cancellationToken = default);

    Task<SyncOutboxEntry> RecordAttemptAsync(
        Guid mutationId,
        DateTimeOffset attemptedAtUtc,
        SyncOutboxErrorCategory errorCategory,
        string errorCode,
        string safeErrorMessage,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken = default);

    Task<SyncOutboxEntry> RecordConflictAsync(
        Guid mutationId,
        DateTimeOffset detectedAtUtc,
        string conflictCode,
        long? currentServerRevision,
        CalendarEvent? remoteEvent,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid mutationId, CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(
        Guid mutationId,
        long serverRevision,
        CancellationToken cancellationToken = default);
}

public interface ICloudConflictRepository
{
    Task<IReadOnlyList<SyncOutboxEntry>> GetConflictsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default);

    Task<SyncOutboxEntry> KeepLocalAsync(
        Guid conflictMutationId,
        Guid replacementMutationId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> KeepCloudAsync(
        Guid conflictMutationId,
        CancellationToken cancellationToken = default);
}

public interface ISyncStateRepository
{
    Task<SyncState?> GetAsync(
        string scope = SyncState.DefaultScope,
        CancellationToken cancellationToken = default);

    Task SaveAsync(SyncState state, CancellationToken cancellationToken = default);
}

public interface ICloudSyncBindingRepository
{
    Task<CloudSyncBinding?> GetAsync(CancellationToken cancellationToken = default);

    Task<CloudSyncBinding> BindAsync(
        string accountId,
        DateTimeOffset boundAtUtc,
        CancellationToken cancellationToken = default);
}

public interface ICloudChangeApplyRepository
{
    Task ApplyAndAdvanceCursorAsync(
        string scope,
        IReadOnlyList<CloudRemoteCalendarChange> changes,
        long cursor,
        DateTimeOffset synchronizedAtUtc,
        CancellationToken cancellationToken = default);
}
