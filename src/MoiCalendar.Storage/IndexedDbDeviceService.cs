using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbDeviceService(IndexedDbConnection connection) : IDeviceService
{
    public async Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) =>
        (await GetDeviceIdentityAsync(cancellationToken)).DeviceId.ToString("D");

    public async Task<DeviceIdentity> GetDeviceIdentityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<DeviceIdentity>("getOrCreateDeviceIdentity", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JSException exception)
        {
            throw new SyncOperationException("读取设备标识失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}
