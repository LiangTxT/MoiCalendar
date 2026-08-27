using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemoryDeviceService(string? deviceId = null) : IDeviceService
{
    private readonly string stableDeviceId = deviceId ?? Guid.NewGuid().ToString("D");

    public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(stableDeviceId);
}
