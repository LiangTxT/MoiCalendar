using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class AccountDataServiceTests
{
    [Fact]
    public async Task Export_ProducesWhitelistedPortableDocumentWithoutSecrets()
    {
        var account = new FakeAccountService();
        var transport = new FakeTransport { ExportDocument = CreateExportDocument() };
        var service = CreateService(account, transport, new FakeLocalRepository());

        var export = await service.ExportAsync();

        Assert.Equal(1, export.CalendarCount);
        Assert.Equal(1, export.EventCount);
        Assert.Matches("^mycalendar-cloud-export-\\d{4}-\\d{2}-\\d{2}\\.json$", export.FileName);
        using var document = JsonDocument.Parse(export.Json);
        Assert.Equal("moicalendar-cloud-export", document.RootElement.GetProperty("format").GetString());
        Assert.Equal("日历", document.RootElement.GetProperty("calendars")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("password", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serviceRole", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webDav", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("microsoft", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", export.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportAndDeletion_RequireAuthenticatedAccount()
    {
        var account = new FakeAccountService { Account = null };
        var transport = new FakeTransport { ExportDocument = CreateExportDocument() };
        var service = CreateService(account, transport, new FakeLocalRepository());

        var exportError = await Assert.ThrowsAsync<AccountDataException>(() => service.ExportAsync());
        var deleteError = await Assert.ThrowsAsync<AccountDataException>(() => service.DeleteAccountAsync(
            IAccountDataService.RequiredDeletionConfirmation,
            false));

        Assert.Equal(AccountDataFailureKind.AuthenticationRequired, exportError.FailureKind);
        Assert.Equal(AccountDataFailureKind.AuthenticationRequired, deleteError.FailureKind);
        Assert.Equal(0, transport.DeleteCalls);
    }

    [Fact]
    public async Task Deletion_RequiresExactExplicitConfirmation()
    {
        var transport = new FakeTransport();
        var service = CreateService(new FakeAccountService(), transport, new FakeLocalRepository());

        var exception = await Assert.ThrowsAsync<AccountDataException>(() =>
            service.DeleteAccountAsync("删除", false));

        Assert.Equal(AccountDataFailureKind.ConfirmationRequired, exception.FailureKind);
        Assert.Equal(0, transport.DeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_ResetsAccountScopedLocalStateAndLogsOut(bool removeLocalData)
    {
        var account = new FakeAccountService();
        var local = new FakeLocalRepository { StaleOutboxCount = 3 };
        var transport = new FakeTransport();
        var service = CreateService(account, transport, local);

        var result = await service.DeleteAccountAsync(
            IAccountDataService.RequiredDeletionConfirmation,
            removeLocalData);

        Assert.Equal(removeLocalData, result.LocalDataRemoved);
        Assert.Equal(1, transport.DeleteCalls);
        Assert.Equal("account-a", local.AccountId);
        Assert.Equal(removeLocalData, local.RemoveLocalData);
        Assert.Equal(0, local.StaleOutboxCount);
        Assert.Equal(1, account.LogoutCalls);
    }

    [Fact]
    public async Task Deletion_TransientFailureCanBeRetriedWithoutPrematureLocalCleanup()
    {
        var account = new FakeAccountService();
        var local = new FakeLocalRepository { StaleOutboxCount = 2 };
        var transport = new FakeTransport { DeleteFailuresRemaining = 1 };
        var service = CreateService(account, transport, local);

        var first = await Assert.ThrowsAsync<AccountDataException>(() => service.DeleteAccountAsync(
            IAccountDataService.RequiredDeletionConfirmation,
            false));

        Assert.Equal(AccountDataFailureKind.Transient, first.FailureKind);
        Assert.Equal(2, local.StaleOutboxCount);
        Assert.Equal(0, account.LogoutCalls);

        await service.DeleteAccountAsync(IAccountDataService.RequiredDeletionConfirmation, false);

        Assert.Equal(2, transport.DeleteCalls);
        Assert.Equal(0, local.StaleOutboxCount);
        Assert.Equal(1, account.LogoutCalls);
    }

    private static AccountDataService CreateService(
        IAccountService account,
        ICloudAccountDataTransport transport,
        IAccountDeletionLocalRepository local) =>
        new(account, transport, local, NoOpLocalDataOperationLock.Instance);

    private static CloudAccountExportDocument CreateExportDocument()
    {
        var now = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var calendarId = Guid.NewGuid();
        return new CloudAccountExportDocument
        {
            Format = CloudAccountExportDocument.CurrentFormat,
            SchemaVersion = CloudAccountExportDocument.CurrentSchemaVersion,
            ExportedAtUtc = now,
            Preferences = new CloudAccountPreferences("测试账户", "Asia/Hong_Kong"),
            Calendars =
            [
                new CloudCalendarExport(calendarId, "日历", "#425A48", true, now.AddDays(-1), now, null)
            ],
            CalendarEvents =
            [
                new CloudCalendarEventExport(calendarId, new CalendarEvent
                {
                    Id = Guid.NewGuid(),
                    Title = "日程",
                    Description = "说明",
                    Location = "地点",
                    StartUtc = now,
                    EndUtc = now.AddHours(1),
                    TimeZoneId = "Asia/Hong_Kong",
                    IsAllDay = false,
                    CreatedAtUtc = now.AddDays(-1),
                    UpdatedAtUtc = now
                })
            ]
        };
    }

    private sealed class FakeTransport : ICloudAccountDataTransport
    {
        public bool IsAvailable => true;
        public CloudAccountExportDocument? ExportDocument { get; init; }
        public int DeleteCalls { get; private set; }
        public int DeleteFailuresRemaining { get; set; }

        public Task<CloudAccountExportDocument> ExportAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ExportDocument ?? CreateExportDocument());

        public Task DeleteCurrentAccountAsync(CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (DeleteFailuresRemaining-- > 0)
            {
                throw new AccountDataException("暂时不可用。", AccountDataFailureKind.Transient);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLocalRepository : IAccountDeletionLocalRepository
    {
        public string? AccountId { get; private set; }
        public bool RemoveLocalData { get; private set; }
        public int StaleOutboxCount { get; set; }

        public Task ResetAfterAccountDeletionAsync(
            string accountId,
            bool removeLocalData,
            CancellationToken cancellationToken = default)
        {
            AccountId = accountId;
            RemoveLocalData = removeLocalData;
            StaleOutboxCount = 0;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountService : IAccountService
    {
        public bool IsAvailable => true;
        public bool IsPasswordRecovery => false;
        public CloudAccount? Account { get; init; } = new("account-a", "账户 A", "a@example.test", true);
        public int LogoutCalls { get; private set; }

        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            LogoutCalls++;
            return Task.CompletedTask;
        }

        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);
        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);
        public Task<AccountRegistrationResult> RegisterAsync(string emailAddress, string password, string emailRedirectUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CloudAccount> LoginAsync(string emailAddress, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RequestPasswordResetAsync(string emailAddress, string redirectUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CloudAccount> CompletePasswordResetAsync(string newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
