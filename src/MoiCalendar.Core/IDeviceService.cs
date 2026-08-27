namespace MoiCalendar.Core;

public interface IDeviceService
{
    Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default);
}
