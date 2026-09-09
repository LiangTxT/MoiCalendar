namespace MoiCalendar.Sync.Cloud;

public interface ICloudDeviceService
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<CloudDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<CloudDevice> RenameAsync(
        Guid deviceId,
        string name,
        CancellationToken cancellationToken = default);

    Task<CloudDevice> RevokeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);
}

public sealed record CloudDevice(
    Guid DeviceId,
    string Name,
    string? Platform,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    bool IsCurrent,
    bool IsRevoked);

public class CloudDeviceServiceException : Exception
{
    public CloudDeviceServiceException(string message)
        : base(message)
    {
    }

    public CloudDeviceServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record CloudDeviceRegistration(
    Guid DeviceId,
    string Name,
    string Platform);

public sealed record CloudDeviceRecord(
    Guid DeviceId,
    string Name,
    string? Platform,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    bool IsRevoked);

public interface ICloudDeviceTransport
{
    bool IsAvailable { get; }

    Task<CloudDeviceRecord> RegisterAsync(
        CloudDeviceRegistration registration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudDeviceRecord>> GetDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<CloudDeviceRecord> RenameAsync(
        Guid deviceId,
        string name,
        CancellationToken cancellationToken = default);

    Task<CloudDeviceRecord> RevokeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default);

    Task AcknowledgeSuccessfulSyncAsync(
        Guid deviceId,
        long serverRevision,
        CancellationToken cancellationToken = default);
}
