namespace MoiCalendar.Sync.Cloud;

public interface ICloudReminderTransport
{
    bool IsAvailable { get; }
    Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default);
    Task RegisterAsync(Guid deviceId, string subscriptionJson, CancellationToken cancellationToken = default);
    Task UnregisterAsync(Guid deviceId, CancellationToken cancellationToken = default);
}

internal sealed class DisabledCloudReminderTransport : ICloudReminderTransport
{
    public bool IsAvailable => false;
    public Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("云后端未配置，后台提醒不可用。");
    public Task RegisterAsync(Guid deviceId, string subscriptionJson, CancellationToken cancellationToken = default) => throw new InvalidOperationException("云后端未配置。");
    public Task UnregisterAsync(Guid deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
