using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbDeviceService(IndexedDbConnection connection) : IDeviceService
{
    public async Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<string>("getOrCreateDeviceId", cancellationToken);
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
