using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

public interface ICloudDeviceSyncService
{
    Task<CloudDevice> RegisterCurrentDeviceAsync(CancellationToken cancellationToken = default);

    Task AcknowledgeSuccessfulSyncAsync(
        Guid deviceId,
        long serverRevision,
        CancellationToken cancellationToken = default);
}

internal sealed class CloudDeviceService(
    IAccountService accountService,
    IDeviceService localDeviceService,
    ICloudDeviceTransport transport) : ICloudDeviceService, ICloudDeviceSyncService
{
    private const string DefaultDeviceName = "MoiCalendar PWA";
    private const string DefaultPlatform = "PWA";

    public bool IsAvailable => accountService.IsAvailable && transport.IsAvailable;

    public async Task<IReadOnlyList<CloudDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        await RequireAccountAsync(cancellationToken);
        var current = await GetCurrentIdentityAsync(cancellationToken);
        try
        {
            var records = await transport.GetDevicesAsync(cancellationToken);
            return records
                .Select(record => ToCloudDevice(record, current.DeviceId))
                .OrderByDescending(device => device.IsCurrent)
                .ThenBy(device => device.IsRevoked)
                .ThenByDescending(device => device.LastSuccessfulSyncAtUtc ?? device.RegisteredAtUtc)
                .ToArray();
        }
        catch (CloudSyncTransportException exception)
        {
            throw ToManagementException(exception);
        }
    }

    public async Task<CloudDevice> RenameAsync(
        Guid deviceId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        var normalizedName = ValidateName(name);
        await RequireAccountAsync(cancellationToken);
        var current = await GetCurrentIdentityAsync(cancellationToken);
        try
        {
            var record = await transport.RenameAsync(deviceId, normalizedName, cancellationToken);
            return ToCloudDevice(record, current.DeviceId);
        }
        catch (CloudSyncTransportException exception)
        {
            throw ToManagementException(exception);
        }
    }

    public async Task<CloudDevice> RevokeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        await RequireAccountAsync(cancellationToken);
        var current = await GetCurrentIdentityAsync(cancellationToken);
        if (deviceId == current.DeviceId)
        {
            throw new CloudDeviceServiceException("不能在当前设备上移除当前设备。请改用其他已登录设备操作。");
        }

        try
        {
            var record = await transport.RevokeAsync(deviceId, cancellationToken);
            return ToCloudDevice(record, current.DeviceId);
        }
        catch (CloudSyncTransportException exception)
        {
            throw ToManagementException(exception);
        }
    }

    public async Task<CloudDevice> RegisterCurrentDeviceAsync(
        CancellationToken cancellationToken = default)
    {
        await RequireAccountAsync(cancellationToken);
        var current = await GetCurrentIdentityAsync(cancellationToken);
        var record = await transport.RegisterAsync(
            new CloudDeviceRegistration(current.DeviceId, DefaultDeviceName, DefaultPlatform),
            cancellationToken);
        if (record.DeviceId != current.DeviceId || record.IsRevoked)
        {
            throw new CloudSyncTransportException(
                "云设备登记返回了不一致的结果。",
                CloudSyncFailureKind.Permanent,
                record.IsRevoked ? "device_revoked" : "invalid_device_registration");
        }
        return ToCloudDevice(record, current.DeviceId);
    }

    public Task AcknowledgeSuccessfulSyncAsync(
        Guid deviceId,
        long serverRevision,
        CancellationToken cancellationToken = default) =>
        transport.AcknowledgeSuccessfulSyncAsync(deviceId, serverRevision, cancellationToken);

    private async Task RequireAccountAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new CloudDeviceServiceException("云设备管理尚未启用；本地日历仍可继续使用。");
        }

        CloudAccount? account;
        try
        {
            account = await accountService.GetCurrentAccountAsync(cancellationToken);
        }
        catch (AccountServiceException exception)
        {
            throw new CloudDeviceServiceException("无法恢复云账户会话，请重新登录后再试。", exception);
        }
        if (account is null)
        {
            throw new CloudDeviceServiceException("请先登录云账户。");
        }
    }

    private async Task<DeviceIdentity> GetCurrentIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await localDeviceService.GetDeviceIdentityAsync(cancellationToken);
        }
        catch (SyncOperationException exception)
        {
            throw new CloudDeviceServiceException("无法读取当前设备标识；本地日历未受影响。", exception);
        }
    }

    private static CloudDevice ToCloudDevice(CloudDeviceRecord record, Guid currentDeviceId) =>
        new(
            record.DeviceId,
            record.Name,
            record.Platform,
            record.RegisteredAtUtc,
            record.LastSuccessfulSyncAtUtc,
            record.DeviceId == currentDeviceId,
            record.IsRevoked);

    private static void ValidateDeviceId(Guid deviceId)
    {
        if (deviceId == Guid.Empty)
        {
            throw new CloudDeviceServiceException("设备标识无效。");
        }
    }

    private static string ValidateName(string name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200)
        {
            throw new CloudDeviceServiceException("设备名称必须包含 1 到 200 个字符。");
        }
        return normalized;
    }

    private static CloudDeviceServiceException ToManagementException(
        CloudSyncTransportException exception) =>
        exception.ErrorCode switch
        {
            "device_revoked" => new("该设备已被移除，不能继续管理或同步。", exception),
            "device_not_found" => new("找不到该设备，或者它不属于当前账户。", exception),
            "session_unavailable" or "http_401" => new("云账户会话已失效，请重新登录。", exception),
            _ => new("云设备操作未完成，请稍后重试。", exception)
        };
}
