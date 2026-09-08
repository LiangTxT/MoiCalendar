using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class LocalSyncInfrastructureTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeviceIdentity_IsStableUuid()
    {
        var service = new InMemoryDeviceService();

        var first = await service.GetDeviceIdentityAsync();
        var second = await service.GetDeviceIdentityAsync();

        Assert.NotEqual(Guid.Empty, first.DeviceId);
        Assert.Equal(first, second);
        Assert.Equal(first.DeviceId.ToString("D"), await service.GetDeviceIdAsync());
    }

    [Fact]
    public async Task CreateUpdateDelete_CreateDurableOrderedOutboxEntries()
    {
        var events = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var outbox = new InMemorySyncOutboxRepository();
        var clock = new MutableTimeProvider(Now);
        var service = new CalendarEventService(
            events,
            new InMemoryDeviceService(Guid.NewGuid().ToString("D")),
            new InMemoryEventChangeRepository(events, operations, outbox),
            clock);
        var draft = service.CreateDraft(new DateOnly(2026, 9, 4), TimeZoneInfo.Utc.Id);
        draft.Title = "本地优先";

        var created = await service.CreateAsync(draft);
        clock.Now = clock.Now.AddMinutes(1);
        draft.Title = "本地优先（已更新）";
        await service.UpdateAsync(created.Id, draft);
        clock.Now = clock.Now.AddMinutes(1);
        await service.DeleteAsync(created.Id);

        var entries = await outbox.GetPendingAsync();
        Assert.Equal(
            [SyncOperationType.Create, SyncOperationType.Update, SyncOperationType.Delete],
            entries.Select(entry => entry.Operation));
        Assert.All(entries, entry =>
        {
            Assert.Equal(SyncEntityType.CalendarEvent, entry.EntityType);
            Assert.Equal(created.Id, entry.EntityId);
            Assert.Null(entry.BaseRevision);
            Assert.Equal(0, entry.AttemptCount);
            Assert.DoesNotContain("access_token", entry.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("refresh_token", entry.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", entry.Payload, StringComparison.OrdinalIgnoreCase);
        });
        Assert.NotNull((await events.GetByIdIncludingDeletedAsync(created.Id))?.DeletedAtUtc);
    }

    [Fact]
    public async Task FailedAttempt_UpdatesOutboxMetadataWithoutChangingLocalEvent()
    {
        var events = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var outbox = new InMemorySyncOutboxRepository();
        var service = new CalendarEventService(
            events,
            new InMemoryDeviceService(Guid.NewGuid().ToString("D")),
            new InMemoryEventChangeRepository(events, operations, outbox),
            new MutableTimeProvider(Now));
        var draft = service.CreateDraft(new DateOnly(2026, 9, 4), TimeZoneInfo.Utc.Id);
        draft.Title = "离线仍保留";
        var created = await service.CreateAsync(draft);
        var entry = Assert.Single(await outbox.GetPendingAsync());

        var attempted = await outbox.RecordAttemptAsync(
            entry.MutationId,
            Now.AddMinutes(5),
            SyncOutboxErrorCategory.Transient,
            "network_unavailable",
            "Authorization: Bearer secret-access-token",
            Now.AddMinutes(6));

        Assert.Equal(1, attempted.AttemptCount);
        Assert.Equal(Now.AddMinutes(5), attempted.LastAttemptAtUtc);
        Assert.Equal(Now.AddMinutes(6), attempted.NextAttemptAtUtc);
        Assert.Equal(SyncOutboxErrorCategory.Transient, attempted.LastErrorCategory);
        Assert.DoesNotContain("secret-access-token", attempted.LastError, StringComparison.Ordinal);
        Assert.Equal(created, await events.GetByIdAsync(created.Id));
        Assert.Single(await outbox.GetPendingAsync());
    }

    [Fact]
    public async Task IndexedDbOutboxAndSyncState_AreReadableAfterRepositoryRecreation()
    {
        var entry = CreateOutboxEntry();
        var state = new SyncState
        {
            Scope = SyncState.DefaultScope,
            LastSuccessfulServerRevision = 42,
            LastSuccessfulSyncAtUtc = Now
        };
        var module = new PersistentFakeJsModule(entry, state);

        await using (var firstConnection = new IndexedDbConnection(new FakeJsRuntime(module)))
        {
            var firstOutbox = new IndexedDbSyncOutboxRepository(firstConnection);
            Assert.Equal(entry, Assert.Single(await firstOutbox.GetPendingAsync()));
        }

        await using (var reloadedConnection = new IndexedDbConnection(new FakeJsRuntime(module)))
        {
            var reloadedOutbox = new IndexedDbSyncOutboxRepository(reloadedConnection);
            var syncState = new IndexedDbSyncStateRepository(reloadedConnection);

            Assert.Equal(entry, await reloadedOutbox.GetByIdAsync(entry.MutationId));
            Assert.Equal(state, await syncState.GetAsync());
        }
    }

    private static SyncOutboxEntry CreateOutboxEntry() => new()
    {
        MutationId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        EntityType = SyncEntityType.CalendarEvent,
        EntityId = Guid.NewGuid(),
        Operation = SyncOperationType.Create,
        Payload = "{}",
        CreatedAtUtc = Now,
        AttemptCount = 0
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ValueTask.FromResult((TValue)module);
    }

    private sealed class PersistentFakeJsModule(SyncOutboxEntry entry, SyncState state)
        : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? result = identifier switch
            {
                "initialize" => null,
                "getPendingSyncOutboxEntries" => new[] { entry },
                "getSyncOutboxEntryById" => entry,
                "getCloudSyncState" => state,
                _ => default(TValue)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
