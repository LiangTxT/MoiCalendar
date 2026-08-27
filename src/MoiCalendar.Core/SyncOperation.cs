namespace MoiCalendar.Core;

public sealed record SyncOperation
{
    public required Guid OperationId { get; init; }

    public required string DeviceId { get; init; }

    public required Guid EntityId { get; init; }

    public required SyncOperationType OperationType { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public required string Payload { get; init; }

    public required SyncOperationStatus Status { get; init; }
}
