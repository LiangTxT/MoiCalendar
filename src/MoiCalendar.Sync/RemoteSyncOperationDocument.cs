using System.Text.Json;
using MoiCalendar.Core;

namespace MoiCalendar.Sync;

public sealed record RemoteSyncOperationDocument
{
    public required int FormatVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required string DeviceId { get; init; }

    public required Guid EntityId { get; init; }

    public required SyncOperationType OperationType { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public required JsonElement Payload { get; init; }
}
