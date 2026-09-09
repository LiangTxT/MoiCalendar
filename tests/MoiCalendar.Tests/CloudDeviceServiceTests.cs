using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class CloudDeviceServiceTests
{
    private static readonly Guid CurrentDeviceId =
        Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly DateTimeOffset RegisteredAt =
        new(2026, 9, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Registration_IsIdempotentAndUsesStableCurrentDeviceId()
    {
        var account = new MutableAccountService { Current = TestAccount };
        var transport = new InMemoryCloudDeviceTransport();
        var service = CreateService(account, transport);

        var first = await ((ICloudDeviceSyncService)service).RegisterCurrentDeviceAsync();
        var second = await ((ICloudDeviceSyncService)service).RegisterCurrentDeviceAsync();

        Assert.Equal(CurrentDeviceId, first.DeviceId);
        Assert.True(first.IsCurrent);
        Assert.Equal(first, second);
        Assert.Single(transport.Devices);
        Assert.Equal(2, transport.RegistrationAttempts);
    }

    [Fact]
    public async Task Listing_RecognizesOnlyTheCurrentDevice()
    {
        var account = new MutableAccountService { Current = TestAccount };
        var transport = new InMemoryCloudDeviceTransport();
        transport.Seed(CurrentDeviceId, "这台设备");
        var otherId = Guid.NewGuid();
        transport.Seed(otherId, "平板电脑");
        var service = CreateService(account, transport);

        var devices = await service.GetDevicesAsync();

        Assert.True(devices.Single(device => device.DeviceId == CurrentDeviceId).IsCurrent);
        Assert.False(devices.Single(device => device.DeviceId == otherId).IsCurrent);
    }

    [Fact]
    public async Task Rename_TrimsAndUpdatesOwnedDevice()
    {
        var account = new MutableAccountService { Current = TestAccount };
        var transport = new InMemoryCloudDeviceTransport();
        var otherId = Guid.NewGuid();
        transport.Seed(otherId, "旧名称");
        var service = CreateService(account, transport);

        var renamed = await service.RenameAsync(otherId, "  客厅平板  ");

        Assert.Equal("客厅平板", renamed.Name);
        Assert.Equal("客厅平板", transport.Devices[otherId].Name);
    }

    [Fact]
    public async Task Revoke_RejectsCurrentDeviceAndMarksAnotherDevice()
    {
        var account = new MutableAccountService { Current = TestAccount };
        var transport = new InMemoryCloudDeviceTransport();
        transport.Seed(CurrentDeviceId, "当前设备");
        var otherId = Guid.NewGuid();
        transport.Seed(otherId, "旧手机");
        var service = CreateService(account, transport);

        await Assert.ThrowsAsync<CloudDeviceServiceException>(() =>
            service.RevokeAsync(CurrentDeviceId));
        var revoked = await service.RevokeAsync(otherId);

        Assert.True(revoked.IsRevoked);
        Assert.False(transport.Devices[CurrentDeviceId].IsRevoked);
    }

    [Fact]
    public async Task LogoutAndLoginLifecycle_DoesNotExposeDevicesWithoutAccount()
    {
        var account = new MutableAccountService { Current = TestAccount };
        var transport = new InMemoryCloudDeviceTransport();
        transport.Seed(CurrentDeviceId, "当前设备");
        var service = CreateService(account, transport);

        Assert.Single(await service.GetDevicesAsync());
        account.Current = null;
        await Assert.ThrowsAsync<CloudDeviceServiceException>(() => service.GetDevicesAsync());
        account.Current = TestAccount with { Id = "account-2" };
        Assert.Single(await service.GetDevicesAsync());
    }

    [Fact]
    public async Task SuccessfulSyncAcknowledgement_IsTransportOnlyAndNotAvailableOnUiContract()
    {
        var account = new MutableAccountService { Current = TestAccount };
        var transport = new InMemoryCloudDeviceTransport();
        transport.Seed(CurrentDeviceId, "当前设备");
        var service = CreateService(account, transport);

        await ((ICloudDeviceSyncService)service).AcknowledgeSuccessfulSyncAsync(CurrentDeviceId, 42);

        Assert.Equal(42, transport.LastAcknowledgedRevision);
        Assert.DoesNotContain(
            typeof(ICloudDeviceService).GetMethods(),
            method => method.Name.Contains("Acknowledge", StringComparison.Ordinal));
    }

    private static CloudDeviceService CreateService(
        MutableAccountService account,
        InMemoryCloudDeviceTransport transport) =>
        new(account, new InMemoryDeviceService(CurrentDeviceId.ToString("D")), transport);

    private static readonly CloudAccount TestAccount =
        new("account-1", "测试用户", "test@example.com", true);

    private sealed class MutableAccountService : IAccountService
    {
        public CloudAccount? Current { get; set; }

        public bool IsAvailable => true;

        public bool IsPasswordRecovery => false;

        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<AccountRegistrationResult> RegisterAsync(string emailAddress, string password, string emailRedirectUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudAccount> LoginAsync(string emailAddress, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            Current = null;
            return Task.CompletedTask;
        }

        public Task RequestPasswordResetAsync(string emailAddress, string redirectUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CloudAccount> CompletePasswordResetAsync(string newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryCloudDeviceTransport : ICloudDeviceTransport
    {
        public Dictionary<Guid, CloudDeviceRecord> Devices { get; } = [];

        public int RegistrationAttempts { get; private set; }

        public long? LastAcknowledgedRevision { get; private set; }

        public bool IsAvailable => true;

        public Task<CloudDeviceRecord> RegisterAsync(CloudDeviceRegistration registration, CancellationToken cancellationToken = default)
        {
            RegistrationAttempts++;
            if (!Devices.TryGetValue(registration.DeviceId, out var device))
            {
                device = new CloudDeviceRecord(
                    registration.DeviceId,
                    registration.Name,
                    registration.Platform,
                    RegisteredAt,
                    null,
                    false);
                Devices.Add(device.DeviceId, device);
            }
            return Task.FromResult(device);
        }

        public Task<IReadOnlyList<CloudDeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudDeviceRecord>>(Devices.Values.ToArray());

        public Task<CloudDeviceRecord> RenameAsync(Guid deviceId, string name, CancellationToken cancellationToken = default)
        {
            var updated = Devices[deviceId] with { Name = name };
            Devices[deviceId] = updated;
            return Task.FromResult(updated);
        }

        public Task<CloudDeviceRecord> RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
        {
            var updated = Devices[deviceId] with { IsRevoked = true };
            Devices[deviceId] = updated;
            return Task.FromResult(updated);
        }

        public Task AcknowledgeSuccessfulSyncAsync(Guid deviceId, long serverRevision, CancellationToken cancellationToken = default)
        {
            LastAcknowledgedRevision = serverRevision;
            return Task.CompletedTask;
        }

        public void Seed(Guid deviceId, string name) =>
            Devices[deviceId] = new CloudDeviceRecord(
                deviceId,
                name,
                "PWA",
                RegisteredAt,
                null,
                false);
    }
}
