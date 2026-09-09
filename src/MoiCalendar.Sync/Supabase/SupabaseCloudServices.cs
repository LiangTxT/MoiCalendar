using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal sealed class SupabaseCloudBackend(CloudBackendOptions options) : ICloudBackend
{
    public string ProviderId => "Supabase";

    public bool IsEnabled => options.Enabled;

    public CloudBackendCapabilities Capabilities => new(
        Accounts: options.Enabled,
        Realtime: options.Enabled,
        CalendarSynchronization: options.Enabled);
}

internal sealed class DisabledAccountService : IAccountService
{
    public bool IsAvailable => false;

    public bool IsPasswordRecovery => false;

    public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CloudAccount?>(null);

    public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CloudAccount?>(null);

    public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CloudAccount?>(null);

    public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<AccountRegistrationResult> RegisterAsync(
        string emailAddress,
        string password,
        string emailRedirectUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromException<AccountRegistrationResult>(NotEnabled());

    public Task<CloudAccount> LoginAsync(
        string emailAddress,
        string password,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudAccount>(NotEnabled());

    public Task RequestPasswordResetAsync(
        string emailAddress,
        string redirectUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromException(NotEnabled());

    public Task<CloudAccount> CompletePasswordResetAsync(
        string newPassword,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudAccount>(NotEnabled());

    private static AccountServiceException NotEnabled() =>
        new("云账户功能尚未启用；本地日历仍可继续使用。");
}

internal sealed class DisabledRealtimeNotifier : IRealtimeNotifier, IDisposable
{
    public bool IsAvailable => false;

    public RealtimeConnectionState ConnectionState => RealtimeConnectionState.Disabled;

    public event EventHandler<RealtimeWakeUpEventArgs>? WakeUp
    {
        add { }
        remove { }
    }

    public event EventHandler<RealtimeConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync(string accountId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}

internal sealed class DisabledCloudSyncTransport : ICloudSyncTransport
{
    public bool IsAvailable => false;

    public Task<CloudMutationResponse> PushAsync(
        CloudMutationRequest mutation,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudMutationResponse>(new CloudSyncTransportException("云同步尚未启用。"));

    public Task<CloudChangeBatch> PullAsync(
        Guid deviceId,
        long afterRevision,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudChangeBatch>(new CloudSyncTransportException("云同步尚未启用。"));
}

internal sealed class DisabledCloudDeviceTransport : ICloudDeviceTransport
{
    public bool IsAvailable => false;

    public Task<CloudDeviceRecord> RegisterAsync(
        CloudDeviceRegistration registration,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDeviceRecord>(NotEnabled());

    public Task<IReadOnlyList<CloudDeviceRecord>> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<CloudDeviceRecord>>(NotEnabled());

    public Task<CloudDeviceRecord> RenameAsync(
        Guid deviceId,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDeviceRecord>(NotEnabled());

    public Task<CloudDeviceRecord> RevokeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDeviceRecord>(NotEnabled());

    public Task AcknowledgeSuccessfulSyncAsync(
        Guid deviceId,
        long serverRevision,
        CancellationToken cancellationToken = default) =>
        Task.FromException(NotEnabled());

    private static CloudSyncTransportException NotEnabled() =>
        new("云设备管理尚未启用。", CloudSyncFailureKind.Permanent, "not_enabled");
}
