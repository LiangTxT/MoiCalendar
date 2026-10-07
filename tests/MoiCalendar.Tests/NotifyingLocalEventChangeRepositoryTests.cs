using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class NotifyingLocalEventChangeRepositoryTests
{
    [Fact]
    public async Task CreateUpdateDelete_NotifyOnlyAfterEventAndOutboxAreCommitted()
    {
        var events = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var outbox = new InMemorySyncOutboxRepository();
        var signals = new AutoSyncSignal();
        var notifications = 0;
        var committedOutboxCounts = new List<int>();
        signals.Committed += (_, _) =>
        {
            notifications++;
            committedOutboxCounts.Add(outbox.GetPendingAsync().GetAwaiter().GetResult().Count);
        };
        var repository = new NotifyingLocalEventChangeRepository(
            new InMemoryEventChangeRepository(events, operations, outbox), signals);
        var service = new CalendarEventService(events, new InMemoryDeviceService("test"), repository,
            TimeProvider.System);
        var draft = service.CreateDraft(new DateOnly(2026, 10, 6), "UTC");
        draft.Title = "自动上传";
        var created = await service.CreateAsync(draft);
        draft.Title = "修改";
        await service.UpdateAsync(created.Id, draft);
        await service.DeleteAsync(created.Id);
        Assert.False(await service.DeleteAsync(Guid.NewGuid()));
        await repository.ApplyImportAsync([]);
        Assert.Equal(3, notifications);
        Assert.Equal([1, 2, 3], committedOutboxCounts);
        Assert.NotNull((await events.GetAllIncludingDeletedAsync()).Single(e => e.Id == created.Id).DeletedAtUtc);
    }

    [Fact]
    public async Task InvalidSave_DoesNotNotifyOrCreateOutboxEntry()
    {
        var events = new InMemoryEventRepository();
        var signals = new AutoSyncSignal();
        var count = 0;
        signals.Committed += (_, _) => count++;
        var service = new CalendarEventService(events, new InMemoryDeviceService("test"),
            new NotifyingLocalEventChangeRepository(new InMemoryEventChangeRepository(events,
                new InMemoryOperationRepository()), signals), TimeProvider.System);
        var draft = service.CreateDraft(new DateOnly(2026, 10, 6), "UTC");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(draft));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ThrowingBackgroundSubscriber_CannotFailLocalSave()
    {
        var events = new InMemoryEventRepository();
        var signals = new AutoSyncSignal();
        signals.Committed += (_, _) => throw new InvalidOperationException("offline");
        var service = new CalendarEventService(events, new InMemoryDeviceService("test"),
            new NotifyingLocalEventChangeRepository(new InMemoryEventChangeRepository(events,
                new InMemoryOperationRepository()), signals), TimeProvider.System);
        var draft = service.CreateDraft(new DateOnly(2026, 10, 6), "UTC");
        draft.Title = "离线保存";
        var result = await service.CreateAsync(draft);
        Assert.Equal("离线保存", (await events.GetByIdAsync(result.Id))!.Title);
    }
}
