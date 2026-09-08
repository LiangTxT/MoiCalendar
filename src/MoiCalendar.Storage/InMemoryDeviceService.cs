using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemoryDeviceService(string? deviceId = null) : IDeviceService
{
    private readonly string stableDeviceId = deviceId ?? Guid.NewGuid().ToString("D");
    private readonly DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;

    public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(stableDeviceId);

    public Task<DeviceIdentity> GetDeviceIdentityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DeviceIdentity(
            DeviceIdentity.NormalizeDeviceId(stableDeviceId),
            createdAtUtc));
}
