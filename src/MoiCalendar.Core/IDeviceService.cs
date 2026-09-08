namespace MoiCalendar.Core;

public interface IDeviceService
{
    Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default);

    async Task<DeviceIdentity> GetDeviceIdentityAsync(CancellationToken cancellationToken = default)
    {
        var deviceId = await GetDeviceIdAsync(cancellationToken);
        return new DeviceIdentity(DeviceIdentity.NormalizeDeviceId(deviceId), DateTimeOffset.UnixEpoch);
    }
}
