using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class CloudSyncServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 5, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneDeviceCreate_PushesAndAdvancesCursor()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "新建");

        var result = await context.Sync.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.PushedCount);
        Assert.Empty(await context.Outbox.GetPendingAsync());
        Assert.Equal("新建", context.Transport.GetEvent(created.Id)?.Title);
        Assert.True(result.Cursor > 0);
    }

    [Fact]
    public async Task OneDeviceUpdate_UsesLastAcknowledgedServerRevision()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "原始");
        await context.Sync.SynchronizeAsync();
        var draft = context.Calendar.CreateDraft(created);
        draft.Title = "更新后";
        await context.Calendar.UpdateAsync(created.Id, draft);

        var pending = Assert.Single(await context.Outbox.GetPendingAsync());
        Assert.True(pending.BaseRevision > 0);
        var result = await context.Sync.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("更新后", context.Transport.GetEvent(created.Id)?.Title);
        Assert.Empty(await context.Outbox.GetPendingAsync());
    }

    [Fact]
    public async Task OneDeviceDelete_RemainsTombstonedAcrossLaterPulls()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "待删除");
        await context.Sync.SynchronizeAsync();
        await context.Calendar.DeleteAsync(created.Id);

        var deleted = await context.Sync.SynchronizeAsync();
        var resumed = await context.Sync.SynchronizeAsync();

        Assert.True(deleted.IsSuccess);
        Assert.True(resumed.IsSuccess);
        Assert.Null(await context.Events.GetByIdAsync(created.Id));
        Assert.NotNull((await context.Events.GetByIdIncludingDeletedAsync(created.Id))?.DeletedAtUtc);
        Assert.NotNull(context.Transport.GetEvent(created.Id)?.DeletedAtUtc);
    }

    [Fact]
    public async Task LegacyLocalDelete_MaterializesCloudTombstoneWithoutConflict()
    {
        var context = CreateContext();
        var legacy = CreateEvent(Guid.NewGuid(), "同步启用前的日程");
        await context.Events.CreateAsync(legacy);
        await context.Calendar.DeleteAsync(legacy.Id);

        var result = await context.Sync.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.PushedCount);
        Assert.Empty(await context.Outbox.GetPendingAsync());
        Assert.NotNull(context.Transport.GetEvent(legacy.Id)?.DeletedAtUtc);
        Assert.True(result.Cursor > 0);
    }

    [Fact]
    public async Task ExistingMissingEntityDeleteConflict_RetriesAfterServerUpgrade()
    {
        var context = CreateContext();
        var legacy = CreateEvent(Guid.NewGuid(), "已产生旧冲突的日程");
        await context.Events.CreateAsync(legacy);
        await context.Calendar.DeleteAsync(legacy.Id);
        context.Transport.MaterializeMissingDeletes = false;

        var conflicted = await context.Sync.SynchronizeAsync();
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());
        context.Transport.MaterializeMissingDeletes = true;
        var recovered = await context.Sync.SynchronizeAsync();

        Assert.Equal(CloudSyncOutcome.Conflict, conflicted.Outcome);
        Assert.Equal("entity_not_found", retained.ConflictCode);
        Assert.True(recovered.IsSuccess);
        Assert.Equal(1, recovered.PushedCount);
        Assert.Empty(await context.Outbox.GetPendingAsync());
        Assert.NotNull(context.Transport.GetEvent(legacy.Id)?.DeletedAtUtc);
    }

    [Fact]
    public async Task LegacyLocalUpdate_RemainsAConflict()
    {
        var context = CreateContext();
        var legacy = CreateEvent(Guid.NewGuid(), "同步启用前的日程");
        await context.Events.CreateAsync(legacy);
        var draft = context.Calendar.CreateDraft(legacy);
        draft.Title = "本地更新";
        await context.Calendar.UpdateAsync(legacy.Id, draft);

        var first = await context.Sync.SynchronizeAsync();
        var second = await context.Sync.SynchronizeAsync();

        Assert.Equal(CloudSyncOutcome.Conflict, first.Outcome);
        Assert.Equal("entity_not_found", first.Conflict?.Code);
        Assert.Equal(CloudSyncOutcome.Conflict, second.Outcome);
        Assert.Single(await context.Outbox.GetPendingAsync());
        Assert.Null(context.Transport.GetEvent(legacy.Id));
    }

    [Fact]
    public async Task ReconnectAfterOfflineEdit_RetriesWithoutLosingLocalChange()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "离线创建");
        context.Transport.FailBeforeApplyCount = 3;

        var offline = await context.Sync.SynchronizeAsync();
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());
        context.Clock.Advance(retained.NextAttemptAtUtc!.Value - context.Clock.GetUtcNow());
        var reconnected = await context.Sync.SynchronizeAsync();

        Assert.Equal(CloudSyncOutcome.Failed, offline.Outcome);
        Assert.Equal(CloudSyncStatus.Offline, offline.Status);
        Assert.Equal(3, retained.AttemptCount);
        Assert.Equal(created, await context.Events.GetByIdAsync(created.Id));
        Assert.True(reconnected.IsSuccess);
        Assert.Empty(await context.Outbox.GetPendingAsync());
    }

    [Fact]
    public async Task DuplicateMutationRetry_IsIdempotentAfterLostAcknowledgement()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "幂等创建");
        context.Transport.ThrowAfterApplyOnce = true;

        var result = await context.Sync.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, context.Transport.LogicalApplyCount);
        Assert.Equal("幂等创建", context.Transport.GetEvent(created.Id)?.Title);
        Assert.Empty(await context.Outbox.GetPendingAsync());
    }

    [Fact]
    public async Task RemoteCommitBeforeLocalAcknowledgement_CanBeSafelyReplayed()
    {
        var context = CreateContext();
        _ = await CreateLocalEventAsync(context, "确认中断");
        var interruptingOutbox = new InterruptingAcknowledgeOutboxRepository(context.Outbox);
        var sync = new CloudSyncService(
            context.Account,
            context.Transport,
            interruptingOutbox,
            context.State,
            new InMemoryCloudSyncBindingRepository(),
            new InMemoryCloudChangeApplyRepository(context.Events, context.State, interruptingOutbox),
            NoOpLocalDataOperationLock.Instance,
            new AllowSyncGuard(),
            context.Clock,
            new FakeRetryPolicy(),
            context.Delay);

        var interrupted = await sync.SynchronizeAsync();
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());
        var resumed = await sync.SynchronizeAsync();

        Assert.Equal(CloudSyncStatus.Error, interrupted.Status);
        Assert.NotEqual(Guid.Empty, retained.MutationId);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(1, context.Transport.LogicalApplyCount);
        Assert.Empty(await context.Outbox.GetPendingAsync());
    }

    [Fact]
    public async Task CursorResume_ContinuesAfterLastDurablyAppliedBatch()
    {
        var context = CreateContext();
        for (var index = 0; index < 101; index++)
        {
            context.Transport.SeedEvent(CreateEvent(Guid.NewGuid(), $"远端 {index}"));
        }
        context.Transport.FailPullAfterRevisionCount[100] = 3;

        var interrupted = await context.Sync.SynchronizeAsync();
        var persisted = await context.State.GetAsync("cloud:account-1");
        var resumed = await context.Sync.SynchronizeAsync();

        Assert.Equal(CloudSyncOutcome.Failed, interrupted.Outcome);
        Assert.Equal(100, persisted?.LastSuccessfulServerRevision);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(101, resumed.Cursor);
        Assert.Contains(100, context.Transport.PullAfterRevisions);
        Assert.Equal(101, (await context.Events.GetAllIncludingDeletedAsync()).Count);
    }

    [Fact]
    public async Task StaleWrite_ReturnsStructuredConflictAndKeepsOutboxEntry()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "本地版本");
        await context.Sync.SynchronizeAsync();
        var draft = context.Calendar.CreateDraft(created);
        draft.Title = "本地待上传";
        await context.Calendar.UpdateAsync(created.Id, draft);
        context.Transport.ApplyExternalUpdate(created.Id, "另一设备修改");

        var result = await context.Sync.SynchronizeAsync();

        Assert.Equal(CloudSyncOutcome.Conflict, result.Outcome);
        Assert.Equal("stale_revision", result.Conflict?.Code);
        Assert.NotNull(result.Conflict?.CurrentServerRevision);
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());
        Assert.Equal("stale_revision", retained.ConflictCode);
        Assert.Equal("另一设备修改", retained.ConflictRemoteEvent?.Title);
        Assert.Equal("本地待上传", (await context.Events.GetByIdAsync(created.Id))?.Title);
    }

    [Fact]
    public async Task Conflict_DoesNotBlockUnrelatedEntityMutation()
    {
        var context = CreateContext();
        var conflicted = await CreateLocalEventAsync(context, "冲突事件");
        var unrelated = await CreateLocalEventAsync(context, "独立事件");
        await context.Sync.SynchronizeAsync();

        context.Clock.Advance(TimeSpan.FromMinutes(1));
        var conflictedDraft = context.Calendar.CreateDraft(conflicted);
        conflictedDraft.Title = "本地冲突版本";
        await context.Calendar.UpdateAsync(conflicted.Id, conflictedDraft);
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        var unrelatedDraft = context.Calendar.CreateDraft(unrelated);
        unrelatedDraft.Title = "独立更新";
        await context.Calendar.UpdateAsync(unrelated.Id, unrelatedDraft);
        context.Transport.ApplyExternalUpdate(conflicted.Id, "远端冲突版本");

        var result = await context.Sync.SynchronizeAsync();

        Assert.Equal(CloudSyncStatus.Conflict, result.Status);
        Assert.Equal("独立更新", context.Transport.GetEvent(unrelated.Id)?.Title);
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());
        Assert.Equal(conflicted.Id, retained.EntityId);
        Assert.Equal("本地冲突版本", (await context.Events.GetByIdAsync(conflicted.Id))?.Title);
    }

    [Fact]
    public async Task RetryPolicy_IsBoundedAndPersistsSafeSchedule()
    {
        var context = CreateContext();
        _ = await CreateLocalEventAsync(context, "持续断网");
        context.Transport.FailBeforeApplyCount = 10;

        var result = await context.Sync.SynchronizeAsync();
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());

        Assert.Equal(CloudSyncStatus.Offline, result.Status);
        Assert.Equal(3, retained.AttemptCount);
        Assert.Equal(SyncOutboxErrorCategory.Transient, retained.LastErrorCategory);
        Assert.Equal("network_unavailable", retained.LastErrorCode);
        Assert.NotNull(retained.NextAttemptAtUtc);
        Assert.Equal(2, context.Delay.Delays.Count);
        Assert.All(context.Delay.Delays, delay => Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromSeconds(8)));
        Assert.DoesNotContain("Bearer", retained.LastError ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthorized_RefreshesSessionOnceAndReplaysOperationOnce()
    {
        var context = CreateContext();
        _ = await CreateLocalEventAsync(context, "会话恢复");
        context.Transport.AuthenticationFailureCount = 1;

        var result = await context.Sync.SynchronizeAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, context.Account.RefreshCount);
        Assert.Equal(1, context.Transport.LogicalApplyCount);
        Assert.Empty(await context.Outbox.GetPendingAsync());
    }

    [Fact]
    public async Task FailedSessionRefresh_PreservesOutboxAndRequiresAuthentication()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "等待重新登录");
        context.Transport.AuthenticationFailureCount = 1;
        context.Account.RefreshSucceeds = false;

        var result = await context.Sync.SynchronizeAsync();
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());

        Assert.Equal(CloudSyncStatus.AuthenticationRequired, result.Status);
        Assert.Equal(1, context.Account.RefreshCount);
        Assert.Equal(SyncOutboxErrorCategory.Authentication, retained.LastErrorCategory);
        Assert.Equal(created, await context.Events.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task UpdateAgainstRemoteTombstone_ReturnsDeterministicConflict()
    {
        var context = CreateContext();
        var created = await CreateLocalEventAsync(context, "离线设备副本");
        await context.Sync.SynchronizeAsync();
        var draft = context.Calendar.CreateDraft(created);
        draft.Title = "离线更新";
        await context.Calendar.UpdateAsync(created.Id, draft);
        context.Transport.ApplyExternalDelete(created.Id);

        var result = await context.Sync.SynchronizeAsync();

        Assert.Equal("entity_deleted", result.Conflict?.Code);
        Assert.NotNull(result.Conflict?.RemoteEvent?.DeletedAtUtc);
        Assert.Equal("离线更新", (await context.Events.GetByIdAsync(created.Id))?.Title);
        Assert.NotNull((await context.Outbox.GetPendingAsync()).Single().ConflictRemoteEvent?.DeletedAtUtc);
    }

    [Fact]
    public async Task ApplicationRestart_ResumesPersistedPendingMutation()
    {
        var context = CreateContext();
        _ = await CreateLocalEventAsync(context, "重启后继续");
        context.Transport.FailBeforeApplyCount = 3;
        _ = await context.Sync.SynchronizeAsync();
        var retained = Assert.Single(await context.Outbox.GetPendingAsync());
        context.Clock.Advance(retained.NextAttemptAtUtc!.Value - context.Clock.GetUtcNow());

        var restartedSync = new CloudSyncService(
            context.Account,
            context.Transport,
            context.Outbox,
            context.State,
            new InMemoryCloudSyncBindingRepository(),
            new InMemoryCloudChangeApplyRepository(context.Events, context.State, context.Outbox),
            NoOpLocalDataOperationLock.Instance,
            new AllowSyncGuard(),
            context.Clock,
            new FakeRetryPolicy(),
            context.Delay);
        var resumed = await restartedSync.SynchronizeAsync();

        Assert.True(resumed.IsSuccess);
        Assert.Empty(await context.Outbox.GetPendingAsync());
    }

    [Fact]
    public async Task PullApplyInterruption_DoesNotAdvanceCursorAndCanResume()
    {
        var context = CreateContext();
        var remote = CreateEvent(Guid.NewGuid(), "远端待拉取");
        context.Transport.SeedEvent(remote);
        var apply = new InterruptingApplyRepository(
            new InMemoryCloudChangeApplyRepository(context.Events, context.State, context.Outbox));
        var sync = new CloudSyncService(
            context.Account,
            context.Transport,
            context.Outbox,
            context.State,
            new InMemoryCloudSyncBindingRepository(),
            apply,
            NoOpLocalDataOperationLock.Instance,
            new AllowSyncGuard(),
            context.Clock,
            new FakeRetryPolicy(),
            context.Delay);

        var interrupted = await sync.SynchronizeAsync();
        var stateAfterFailure = await context.State.GetAsync("cloud:account-1");
        var resumed = await sync.SynchronizeAsync();

        Assert.Equal(CloudSyncStatus.Error, interrupted.Status);
        Assert.Null(stateAfterFailure);
        Assert.True(resumed.IsSuccess);
        Assert.Equal(remote, await context.Events.GetByIdAsync(remote.Id));
    }

    [Fact]
    public async Task DuplicatePullProcessing_IsIdempotent()
    {
        var context = CreateContext();
        var remote = CreateEvent(Guid.NewGuid(), "重复拉取");
        context.Transport.SeedEvent(remote);
        await context.Sync.SynchronizeAsync();
        await context.State.SaveAsync(new SyncState
        {
            Scope = "cloud:account-1",
            LastSuccessfulServerRevision = 0,
            LastSuccessfulSyncAtUtc = context.Clock.GetUtcNow()
        });

        var repeated = await context.Sync.SynchronizeAsync();

        Assert.True(repeated.IsSuccess);
        Assert.Single(await context.Events.GetAllIncludingDeletedAsync());
        Assert.Equal(remote, await context.Events.GetByIdAsync(remote.Id));
    }

    [Fact]
    public void DefaultRetryPolicy_UsesBoundedBackoff()
    {
        var policy = new CloudSyncRetryPolicy();

        var delays = Enumerable.Range(1, 50).Select(policy.GetDelay).ToArray();

        Assert.Equal(3, policy.MaximumAttemptsPerOperation);
        Assert.All(delays, delay => Assert.InRange(
            delay,
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromSeconds(30)));
    }

    private static TestContext CreateContext()
    {
        var events = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var outbox = new InMemorySyncOutboxRepository();
        var state = new InMemorySyncStateRepository();
        var clock = new MutableTimeProvider(Now);
        var calendar = new CalendarEventService(
            events,
            new InMemoryDeviceService("11111111-1111-4111-8111-111111111111"),
            new InMemoryEventChangeRepository(events, operations, outbox),
            clock);
        var transport = new FakeCloudServerTransport();
        var account = new FakeAccountService();
        var delay = new AdvancingDelay(clock);
        var sync = new CloudSyncService(
            account,
            transport,
            outbox,
            state,
            new InMemoryCloudSyncBindingRepository(),
            new InMemoryCloudChangeApplyRepository(events, state, outbox),
            NoOpLocalDataOperationLock.Instance,
            new AllowSyncGuard(),
            clock,
            new FakeRetryPolicy(),
            delay);
        return new TestContext(
            calendar, sync, events, outbox, state, transport, clock, account, delay);
    }

    private static async Task<CalendarEvent> CreateLocalEventAsync(TestContext context, string title)
    {
        var draft = context.Calendar.CreateDraft(new DateOnly(2026, 9, 5), TimeZoneInfo.Utc.Id);
        draft.Title = title;
        return await context.Calendar.CreateAsync(draft);
    }

    private static CalendarEvent CreateEvent(Guid id, string title) => new()
    {
        Id = id,
        Title = title,
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = Now,
        EndUtc = Now.AddHours(1),
        TimeZoneId = "UTC",
        IsAllDay = false,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now
    };

    private sealed record TestContext(
        CalendarEventService Calendar,
        CloudSyncService Sync,
        InMemoryEventRepository Events,
        InMemorySyncOutboxRepository Outbox,
        InMemorySyncStateRepository State,
        FakeCloudServerTransport Transport,
        MutableTimeProvider Clock,
        FakeAccountService Account,
        AdvancingDelay Delay);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class FakeRetryPolicy : ICloudSyncRetryPolicy
    {
        public int MaximumAttemptsPerOperation => 3;

        public TimeSpan GetDelay(int totalFailedAttemptCount) =>
            TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, totalFailedAttemptCount - 1)));
    }

    private sealed class AdvancingDelay(MutableTimeProvider clock) : ICloudSyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowSyncGuard : IRestoreSyncGuard
    {
        public Task<bool> IsSyncBlockedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AllowSyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InterruptingApplyRepository(ICloudChangeApplyRepository inner)
        : ICloudChangeApplyRepository
    {
        private bool interrupt = true;

        public Task ApplyAndAdvanceCursorAsync(
            string scope,
            IReadOnlyList<CloudRemoteCalendarChange> changes,
            long cursor,
            DateTimeOffset synchronizedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (interrupt)
            {
                interrupt = false;
                throw new SyncOperationException("模拟 IndexedDB 事务中断。");
            }

            return inner.ApplyAndAdvanceCursorAsync(
                scope,
                changes,
                cursor,
                synchronizedAtUtc,
                cancellationToken);
        }
    }

    private sealed class InterruptingAcknowledgeOutboxRepository(ISyncOutboxRepository inner)
        : ISyncOutboxRepository
    {
        private bool interrupt = true;

        public Task<SyncOutboxEntry?> GetByIdAsync(Guid mutationId, CancellationToken cancellationToken = default) =>
            inner.GetByIdAsync(mutationId, cancellationToken);

        public Task<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(
            int maximumCount = 100,
            CancellationToken cancellationToken = default) =>
            inner.GetPendingAsync(maximumCount, cancellationToken);

        public Task<IReadOnlyList<SyncOutboxEntry>> GetReadyAsync(
            DateTimeOffset readyAtUtc,
            int maximumCount = 100,
            CancellationToken cancellationToken = default) =>
            inner.GetReadyAsync(readyAtUtc, maximumCount, cancellationToken);

        public Task<SyncOutboxEntry> RecordAttemptAsync(
            Guid mutationId,
            DateTimeOffset attemptedAtUtc,
            SyncOutboxErrorCategory errorCategory,
            string errorCode,
            string safeErrorMessage,
            DateTimeOffset? nextAttemptAtUtc,
            CancellationToken cancellationToken = default) =>
            inner.RecordAttemptAsync(
                mutationId,
                attemptedAtUtc,
                errorCategory,
                errorCode,
                safeErrorMessage,
                nextAttemptAtUtc,
                cancellationToken);

        public Task<SyncOutboxEntry> RecordConflictAsync(
            Guid mutationId,
            DateTimeOffset detectedAtUtc,
            string conflictCode,
            long? currentServerRevision,
            CalendarEvent? remoteEvent,
            CancellationToken cancellationToken = default) =>
            inner.RecordConflictAsync(
                mutationId,
                detectedAtUtc,
                conflictCode,
                currentServerRevision,
                remoteEvent,
                cancellationToken);

        public Task RemoveAsync(Guid mutationId, CancellationToken cancellationToken = default) =>
            inner.RemoveAsync(mutationId, cancellationToken);

        public Task AcknowledgeAsync(
            Guid mutationId,
            long serverRevision,
            CancellationToken cancellationToken = default)
        {
            if (interrupt)
            {
                interrupt = false;
                throw new SyncOperationException("模拟确认事务中断。");
            }

            return inner.AcknowledgeAsync(mutationId, serverRevision, cancellationToken);
        }
    }

    private sealed class FakeAccountService : IAccountService
    {
        private static readonly CloudAccount Account = new("account-1", null, "user@example.com", true);

        public bool IsAvailable => true;
        public bool IsPasswordRecovery => false;
        public bool RefreshSucceeds { get; set; } = true;
        public int RefreshCount { get; private set; }
        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudAccount?>(Account);
        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudAccount?>(Account);
        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.FromResult<CloudAccount?>(RefreshSucceeds ? Account : null);
        }
        public Task<AccountRegistrationResult> RegisterAsync(string emailAddress, string password, string emailRedirectUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CloudAccount> LoginAsync(string emailAddress, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RequestPasswordResetAsync(string emailAddress, string redirectUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CloudAccount> CompletePasswordResetAsync(string newPassword, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCloudServerTransport : ICloudSyncTransport
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<Guid, (CalendarEvent Event, long Revision)> events = [];
        private readonly Dictionary<Guid, (CloudMutationRequest Request, CloudMutationResponse Response)> mutations = [];
        private long revision;

        public bool IsAvailable => true;
        public int FailBeforeApplyCount { get; set; }
        public int AuthenticationFailureCount { get; set; }
        public bool ThrowAfterApplyOnce { get; set; }
        public bool MaterializeMissingDeletes { get; set; } = true;
        public Dictionary<long, int> FailPullAfterRevisionCount { get; } = [];
        public int LogicalApplyCount { get; private set; }
        public List<long> PullAfterRevisions { get; } = [];

        public CalendarEvent? GetEvent(Guid id) => events.GetValueOrDefault(id).Event;

        public void SeedEvent(CalendarEvent calendarEvent)
        {
            revision++;
            events[calendarEvent.Id] = (calendarEvent, revision);
        }

        public void ApplyExternalUpdate(Guid id, string title)
        {
            var current = events[id];
            revision++;
            events[id] = (current.Event with { Title = title, UpdatedAtUtc = Now.AddMinutes(1) }, revision);
        }

        public void ApplyExternalDelete(Guid id)
        {
            var current = events[id];
            revision++;
            var deletedAt = Now.AddMinutes(2);
            events[id] = (current.Event with
            {
                DeletedAtUtc = deletedAt,
                UpdatedAtUtc = deletedAt
            }, revision);
        }

        public Task<CloudMutationResponse> PushAsync(CloudMutationRequest mutation, CancellationToken cancellationToken = default)
        {
            if (AuthenticationFailureCount > 0)
            {
                AuthenticationFailureCount--;
                throw new CloudSyncTransportException(
                    "unauthorized",
                    CloudSyncFailureKind.AuthenticationRequired,
                    "http_401",
                    401);
            }
            if (FailBeforeApplyCount > 0)
            {
                FailBeforeApplyCount--;
                throw new CloudSyncTransportException(
                    "offline",
                    CloudSyncFailureKind.Transient,
                    "network_unavailable");
            }
            if (mutations.TryGetValue(mutation.MutationId, out var duplicate))
            {
                if (duplicate.Request != mutation)
                {
                    return Task.FromResult(new CloudMutationResponse(
                        false, mutation.MutationId, mutation.EntityId, null, "mutation_id_reused"));
                }
                if (MaterializeMissingDeletes &&
                    mutation.Operation == SyncOperationType.Delete &&
                    string.Equals(
                        duplicate.Response.ConflictCode,
                        "entity_not_found",
                        StringComparison.Ordinal))
                {
                    var deletedEvent = JsonSerializer.Deserialize<CalendarEvent>(mutation.Payload, JsonOptions)!;
                    var upgraded = Apply(mutation, deletedEvent);
                    mutations[mutation.MutationId] = (mutation, upgraded);
                    return Task.FromResult(upgraded);
                }
                return Task.FromResult(duplicate.Response);
            }

            var calendarEvent = JsonSerializer.Deserialize<CalendarEvent>(mutation.Payload, JsonOptions)!;
            CloudMutationResponse response;
            if (mutation.Operation == SyncOperationType.Create)
            {
                response = events.ContainsKey(mutation.EntityId)
                    ? Conflict(mutation, "entity_exists", events[mutation.EntityId].Revision)
                    : Apply(mutation, calendarEvent);
            }
            else if (!events.TryGetValue(mutation.EntityId, out var existing))
            {
                response = MaterializeMissingDeletes && mutation.Operation == SyncOperationType.Delete
                    ? Apply(mutation, calendarEvent)
                    : Conflict(mutation, "entity_not_found", null);
            }
            else if (existing.Event.DeletedAtUtc is not null)
            {
                response = Conflict(mutation, "entity_deleted", existing.Revision);
            }
            else if (mutation.BaseRevision != existing.Revision)
            {
                response = Conflict(mutation, "stale_revision", existing.Revision);
            }
            else
            {
                response = Apply(mutation, calendarEvent);
            }
            mutations[mutation.MutationId] = (mutation, response);

            if (ThrowAfterApplyOnce && response.Applied)
            {
                ThrowAfterApplyOnce = false;
                throw new CloudSyncTransportException("acknowledgement lost");
            }
            return Task.FromResult(response);
        }

        public Task<CloudChangeBatch> PullAsync(long afterRevision, int maximumCount, CancellationToken cancellationToken = default)
        {
            PullAfterRevisions.Add(afterRevision);
            if (FailPullAfterRevisionCount.GetValueOrDefault(afterRevision) > 0)
            {
                FailPullAfterRevisionCount[afterRevision]--;
                throw new CloudSyncTransportException("pull interrupted");
            }

            var page = events.Values
                .Where(item => item.Revision > afterRevision)
                .OrderBy(item => item.Revision)
                .Take(maximumCount)
                .ToArray();
            var hasMore = page.Length > 0 && events.Values.Any(item => item.Revision > page[^1].Revision);
            var cursor = hasMore ? page[^1].Revision : revision;
            return Task.FromResult(new CloudChangeBatch(
                page.Select(item => new CloudRemoteCalendarChange(item.Event, item.Revision)).ToArray(),
                cursor,
                hasMore));
        }

        private CloudMutationResponse Apply(CloudMutationRequest mutation, CalendarEvent calendarEvent)
        {
            revision++;
            events[mutation.EntityId] = (calendarEvent, revision);
            LogicalApplyCount++;
            return new CloudMutationResponse(true, mutation.MutationId, mutation.EntityId, revision);
        }

        private CloudMutationResponse Conflict(CloudMutationRequest mutation, string code, long? currentRevision) =>
            new(
                false,
                mutation.MutationId,
                mutation.EntityId,
                null,
                code,
                currentRevision,
                events.GetValueOrDefault(mutation.EntityId).Event);
    }
}
