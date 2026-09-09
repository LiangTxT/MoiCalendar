using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class CloudSyncStatusServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 9, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Status_RepresentsLocalOnlySignedOutAndSyncedStates()
    {
        var disabled = CreateContext(signedIn: false, cloudAvailable: false, accountAvailable: false);
        Assert.Equal(CloudSyncDisplayState.LocalOnly, (await disabled.Service.GetStatusAsync()).State);

        var signedOut = CreateContext(signedIn: false);
        Assert.Equal(CloudSyncDisplayState.SignedOut, (await signedOut.Service.GetStatusAsync()).State);

        var signedIn = CreateContext();
        await signedIn.State.SaveAsync(new SyncState
        {
            Scope = "cloud:account-1",
            LastSuccessfulServerRevision = 42,
            LastSuccessfulSyncAtUtc = Now
        });
        var snapshot = await signedIn.Service.GetStatusAsync();
        Assert.Equal(CloudSyncDisplayState.Synced, snapshot.State);
        Assert.Equal(42, snapshot.ServerCursor);
        Assert.Equal(Now, snapshot.LastSuccessfulSyncAtUtc);
    }

    [Fact]
    public async Task Status_ReportsPendingRetryConflictAndErrorWithoutRawMessages()
    {
        var context = CreateContext();
        var local = CreateEvent("本地版本");
        var entry = CreateOutboxEntry(local);
        await context.Outbox.AddAsync(entry);
        var pending = await context.Service.GetStatusAsync();
        Assert.Equal(CloudSyncDisplayState.PendingChanges, pending.State);
        Assert.Equal(1, pending.PendingChangeCount);

        await context.Outbox.RecordAttemptAsync(
            entry.MutationId,
            Now,
            SyncOutboxErrorCategory.Transient,
            "network_unavailable",
            "Authorization: Bearer sb_secret_must_not_leak",
            Now.AddMinutes(2));
        var retry = await context.Service.GetStatusAsync();
        Assert.Equal(CloudSyncDisplayState.RetryScheduled, retry.State);
        Assert.Equal(Now.AddMinutes(2), retry.NextRetryAtUtc);
        Assert.DoesNotContain("sb_secret", retry.ToString(), StringComparison.OrdinalIgnoreCase);

        await context.Outbox.RecordConflictAsync(
            entry.MutationId,
            Now.AddSeconds(1),
            "stale_revision",
            8,
            local with { Title = "云端版本" });
        var conflict = await context.Service.GetStatusAsync();
        Assert.Equal(CloudSyncDisplayState.Conflict, conflict.State);
        Assert.Equal(1, conflict.ConflictCount);

        var errorContext = CreateContext();
        errorContext.Cloud.Status = CloudSyncStatus.Error;
        Assert.Equal(CloudSyncDisplayState.Error, (await errorContext.Service.GetStatusAsync()).State);
    }

    [Fact]
    public async Task Status_ReportsOfflineAndAuthenticationRequired()
    {
        var offline = CreateContext();
        offline.Cloud.Status = CloudSyncStatus.Offline;
        Assert.Equal(CloudSyncDisplayState.Offline, (await offline.Service.GetStatusAsync()).State);

        var authentication = CreateContext();
        authentication.Cloud.Status = CloudSyncStatus.AuthenticationRequired;
        Assert.Equal(
            CloudSyncDisplayState.AuthenticationRequired,
            (await authentication.Service.GetStatusAsync()).State);

        var failedRestore = CreateContext();
        failedRestore.Account.FailureMessage = "refresh_token=secret-value";
        var safeSnapshot = await failedRestore.Service.GetStatusAsync();
        Assert.Equal(CloudSyncDisplayState.Offline, safeSnapshot.State);
        Assert.DoesNotContain("secret-value", safeSnapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualSync_DoesNotStartParallelRuns()
    {
        var context = CreateContext();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Cloud.Handler = async cancellationToken =>
        {
            context.Cloud.Status = CloudSyncStatus.Syncing;
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            context.Cloud.Status = CloudSyncStatus.Idle;
            return SuccessResult();
        };

        var first = context.Service.SynchronizeNowAsync();
        await started.Task;
        var second = await context.Service.SynchronizeNowAsync();

        Assert.Equal(1, context.Cloud.CallCount);
        Assert.Equal(CloudSyncDisplayState.Syncing, second.State);
        release.TrySetResult();
        await first;
        Assert.Equal(1, context.Cloud.CallCount);
    }

    [Fact]
    public async Task Conflict_SurvivesStatusServiceRecreation()
    {
        var events = new InMemoryEventRepository();
        var local = CreateEvent("本地版本");
        await events.UpsertAsync(local);
        var outbox = new InMemorySyncOutboxRepository(events);
        var entry = CreateOutboxEntry(local) with
        {
            ConflictCode = "stale_revision",
            ConflictServerRevision = 5,
            ConflictRemoteEvent = local with { Title = "云端版本" },
            ConflictDetectedAtUtc = Now
        };
        await outbox.AddAsync(entry);

        var first = CreateContext(events, outbox);
        Assert.Single(await first.Service.GetConflictsAsync());
        first.Service.Dispose();

        var reloaded = CreateContext(events, outbox);
        var persisted = Assert.Single(await reloaded.Service.GetConflictsAsync());
        Assert.Equal(entry.MutationId, persisted.MutationId);
        Assert.Equal("本地版本", persisted.LocalVersion?.Title);
        Assert.Equal("云端版本", persisted.CloudVersion?.Title);
    }

    [Fact]
    public async Task KeepLocal_CollapsesEntityOutboxAndUsesCurrentServerRevision()
    {
        var events = new InMemoryEventRepository();
        var local = CreateEvent("保留我的修改");
        await events.UpsertAsync(local);
        var outbox = new InMemorySyncOutboxRepository(events);
        var conflict = CreateOutboxEntry(local) with
        {
            ConflictCode = "stale_revision",
            ConflictServerRevision = 12,
            ConflictRemoteEvent = local with { Title = "云端修改" },
            ConflictDetectedAtUtc = Now
        };
        await outbox.AddAsync(conflict);
        await outbox.AddAsync(CreateOutboxEntry(local with { UpdatedAtUtc = Now.AddMinutes(1) }));
        var context = CreateContext(events, outbox);

        await context.Service.ResolveConflictAsync(
            conflict.MutationId,
            CloudConflictResolution.KeepLocal);

        var replacement = Assert.Single(await outbox.GetPendingAsync());
        Assert.NotEqual(conflict.MutationId, replacement.MutationId);
        Assert.Equal(12, replacement.BaseRevision);
        Assert.Equal(SyncOperationType.Update, replacement.Operation);
        Assert.Null(replacement.ConflictCode);
        Assert.Equal("保留我的修改", Deserialize(replacement.Payload).Title);
    }

    [Fact]
    public async Task KeepCloud_AppliesRemoteVersionAndDiscardsEntityMutations()
    {
        var events = new InMemoryEventRepository();
        var local = CreateEvent("本地修改");
        var remote = local with { Title = "采用云端", UpdatedAtUtc = Now.AddMinutes(3) };
        await events.UpsertAsync(local);
        var outbox = new InMemorySyncOutboxRepository(events);
        var conflict = CreateOutboxEntry(local) with
        {
            ConflictCode = "stale_revision",
            ConflictServerRevision = 21,
            ConflictRemoteEvent = remote,
            ConflictDetectedAtUtc = Now
        };
        await outbox.AddAsync(conflict);
        var context = CreateContext(events, outbox);

        await context.Service.ResolveConflictAsync(
            conflict.MutationId,
            CloudConflictResolution.KeepCloud);

        Assert.Empty(await outbox.GetPendingAsync());
        Assert.Equal(remote, await events.GetByIdIncludingDeletedAsync(local.Id));
    }

    [Fact]
    public async Task UnsafeConflictResolution_RemainsPersisted()
    {
        var events = new InMemoryEventRepository();
        var local = CreateEvent("本地修改");
        await events.UpsertAsync(local);
        var outbox = new InMemorySyncOutboxRepository(events);
        var conflict = CreateOutboxEntry(local) with
        {
            ConflictCode = "entity_deleted",
            ConflictServerRevision = 4,
            ConflictRemoteEvent = local with { DeletedAtUtc = Now, UpdatedAtUtc = Now },
            ConflictDetectedAtUtc = Now
        };
        await outbox.AddAsync(conflict);
        var context = CreateContext(events, outbox);

        await Assert.ThrowsAsync<CloudConflictResolutionException>(() =>
            context.Service.ResolveConflictAsync(
                conflict.MutationId,
                CloudConflictResolution.KeepLocal));

        Assert.Equal(conflict, await outbox.GetByIdAsync(conflict.MutationId));
    }

    [Fact]
    public async Task RetryableLegacyDelete_IsPendingInsteadOfUserConflict()
    {
        var events = new InMemoryEventRepository();
        var deletedAt = Now.AddMinutes(1);
        var deleted = CreateEvent("旧本地日程") with
        {
            DeletedAtUtc = deletedAt,
            UpdatedAtUtc = deletedAt
        };
        await events.UpsertAsync(deleted);
        var outbox = new InMemorySyncOutboxRepository(events);
        await outbox.AddAsync(CreateOutboxEntry(deleted) with
        {
            Operation = SyncOperationType.Delete,
            ConflictCode = "entity_not_found",
            ConflictDetectedAtUtc = Now
        });
        var context = CreateContext(events, outbox);

        var snapshot = await context.Service.GetStatusAsync();

        Assert.Equal(CloudSyncDisplayState.PendingChanges, snapshot.State);
        Assert.Equal(0, snapshot.ConflictCount);
        Assert.Empty(await context.Service.GetConflictsAsync());
    }

    private static TestContext CreateContext(
        InMemoryEventRepository? events = null,
        InMemorySyncOutboxRepository? outbox = null,
        bool signedIn = true,
        bool cloudAvailable = true,
        bool accountAvailable = true)
    {
        events ??= new InMemoryEventRepository();
        outbox ??= new InMemorySyncOutboxRepository(events);
        var accountService = new MutableAccountService
        {
            IsAvailable = accountAvailable,
            Account = accountAvailable && signedIn
                ? new CloudAccount("account-1", null, "user@example.com", true)
                : null
        };
        var cloud = new MutableCloudSyncService { IsAvailable = cloudAvailable };
        var state = new InMemorySyncStateRepository();
        var service = new CloudSyncStatusService(
            cloud,
            accountService,
            new FakeRealtimeNotifier(),
            outbox,
            outbox,
            state,
            new InMemoryCloudSyncBindingRepository(),
            events,
            new FixedTimeProvider(Now));
        return new TestContext(service, cloud, accountService, outbox, state);
    }

    private static CalendarEvent CreateEvent(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = Now.AddHours(1),
        EndUtc = Now.AddHours(2),
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = false,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now
    };

    private static SyncOutboxEntry CreateOutboxEntry(CalendarEvent calendarEvent) => new()
    {
        MutationId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        EntityType = SyncEntityType.CalendarEvent,
        EntityId = calendarEvent.Id,
        Operation = SyncOperationType.Update,
        BaseRevision = 1,
        Payload = JsonSerializer.Serialize(calendarEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        CreatedAtUtc = Now,
        AttemptCount = 0
    };

    private static CalendarEvent Deserialize(string payload) =>
        JsonSerializer.Deserialize<CalendarEvent>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static CloudSyncResult SuccessResult() =>
        new(CloudSyncOutcome.Succeeded, CloudSyncStatus.Idle, 0, 0, 0);

    private sealed record TestContext(
        CloudSyncStatusService Service,
        MutableCloudSyncService Cloud,
        MutableAccountService Account,
        InMemorySyncOutboxRepository Outbox,
        InMemorySyncStateRepository State);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableCloudSyncService : ICloudSyncService
    {
        public bool IsAvailable { get; set; } = true;
        public CloudSyncStatus Status { get; set; }
        public CloudSyncStatus CurrentStatus => Status;
        public int CallCount { get; private set; }
        public Func<CancellationToken, Task<CloudSyncResult>>? Handler { get; set; }

        public Task<CloudSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(SuccessResult());
        }
    }

    private sealed class MutableAccountService : IAccountService
    {
        public bool IsAvailable { get; set; } = true;
        public bool IsPasswordRecovery => false;
        public CloudAccount? Account { get; set; }
        public string? FailureMessage { get; set; }

        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            FailureMessage is null
                ? Task.FromResult(Account)
                : Task.FromException<CloudAccount?>(new AccountServiceException(FailureMessage));
        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);
        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);
        public Task<AccountRegistrationResult> RegisterAsync(string emailAddress, string password, string emailRedirectUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudAccount> LoginAsync(string emailAddress, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequestPasswordResetAsync(string emailAddress, string redirectUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudAccount> CompletePasswordResetAsync(string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRealtimeNotifier : IRealtimeNotifier
    {
        public bool IsAvailable => true;
        public RealtimeConnectionState ConnectionState => RealtimeConnectionState.Connected;
        public event EventHandler<RealtimeWakeUpEventArgs>? WakeUp { add { } remove { } }
        public event EventHandler<RealtimeConnectionStateChangedEventArgs>? ConnectionStateChanged { add { } remove { } }
        public Task StartAsync(string accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
