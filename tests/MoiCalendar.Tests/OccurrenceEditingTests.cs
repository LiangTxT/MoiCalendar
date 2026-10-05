using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class OccurrenceEditingTests
{
    [Fact]
    public async Task TransactionFailure_PreservesOriginalAndDraft_RecordsBothChangesTogether()
    {
        var repository = new InMemoryEventRepository();
        var changes = new FailingBatch();
        var service = new CalendarEventService(repository, new InMemoryDeviceService("tests"), changes, TimeProvider.System);
        var master = new CalendarEvent {
            Id = Guid.NewGuid(), Title = "系列", Description = "", Location = "",
            StartUtc = new(2026, 10, 31, 13, 0, 0, TimeSpan.Zero), EndUtc = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero),
            TimeZoneId = "America/New_York", IsAllDay = false, RecurrenceRule = "FREQ=DAILY;COUNT=3",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await repository.CreateAsync(master);
        var occurrence = new RecurrenceExpansionService().Expand([master], master.StartUtc.AddDays(1), master.StartUtc.AddDays(2)).Single();
        Assert.Equal(14, occurrence.StartUtc.Hour);
        var draft = service.CreateDraft(occurrence with { RecurrenceRule = null });
        draft.Title = "保留我的输入";
        draft.StartLocal = draft.StartLocal.AddMinutes(1);
        await Assert.ThrowsAsync<SyncOperationException>(() => service.UpdateOccurrenceAsync(master.Id, occurrence.StartUtc, draft));
        Assert.Equal(master, await repository.GetByIdAsync(master.Id));
        Assert.Equal("保留我的输入", draft.Title);
        Assert.Equal(2, changes.Batch!.Count);
        Assert.Equal(master.Id, changes.Batch[0].ExpectedExistingEventId);
        Assert.Equal(master.UpdatedAtUtc, changes.Batch[0].ExpectedExistingUpdatedAtUtc);
        Assert.Equal(SyncOperationType.Update, changes.Batch[0].Operation.OperationType);
        Assert.Equal(SyncOperationType.Create, changes.Batch[1].Operation.OperationType);
        Assert.Equal(occurrence.StartUtc.AddMinutes(1), changes.Batch[1].CalendarEvent.StartUtc);
    }

    private sealed class FailingBatch : ILocalEventChangeRepository
    {
        public IReadOnlyList<CalendarImportChange>? Batch;
        public Task ApplyImportAsync(IReadOnlyList<CalendarImportChange> changes, CancellationToken cancellationToken = default)
        {
            Batch = changes;
            throw new SyncOperationException("测试事务失败");
        }
        public Task<CalendarEvent> CreateEventAsync(CalendarEvent e, SyncOperation o, CancellationToken c = default) => throw new InvalidOperationException("必须批量提交");
        public Task<CalendarEvent> UpdateEventAsync(CalendarEvent e, SyncOperation o, CancellationToken c = default) => throw new InvalidOperationException("必须批量提交");
        public Task<bool> DeleteEventAsync(CalendarEvent e, SyncOperation o, CancellationToken c = default) => throw new InvalidOperationException("必须批量提交");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleEdit_IsAtomicBatch_PreservesOtherDatesAndCount(bool allDay)
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var service = new CalendarEventService(repository, new InMemoryDeviceService("tests"),
            new InMemoryEventChangeRepository(repository, operations), TimeProvider.System);
        var draft = service.CreateDraft(new DateOnly(2026, 10, 5), TimeZoneInfo.Utc.Id);
        draft.Title = "原系列";
        draft.SetAllDay(allDay);
        draft.Recurrence.RepeatOption = CalendarEventRepeatOption.Daily;
        var master = await service.CreateAsync(draft);
        var start = master.StartUtc.AddDays(1);
        var edit = service.CreateDraft(master with { StartUtc = start, EndUtc = master.EndUtc.AddDays(1), RecurrenceRule = null });
        edit.Title = "独立日程 🎈";
        var saved = await service.UpdateOccurrenceAsync(master.Id, start.ToOffset(TimeSpan.FromHours(8)), edit);
        Assert.NotEqual(master.Id, saved.Id);
        Assert.Null(saved.RecurrenceRule);
        Assert.Equal(allDay, saved.IsAllDay);
        var updated = (await service.GetByIdAsync(master.Id))!;
        Assert.Equal(master.Title, updated.Title);
        Assert.Equal(master.StartUtc, updated.StartUtc);
        Assert.Equal(master.RecurrenceRule, updated.RecurrenceRule);
        var expanded = new RecurrenceExpansionService().Expand([updated, saved], master.StartUtc, master.StartUtc.AddDays(3));
        Assert.Equal(3, expanded.Count);
        Assert.Single(expanded, item => item.Title == saved.Title);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOccurrenceAsync(master.Id, start, edit));
        var month = await service.GetMonthViewAsync(new CalendarMonth(2026, 10).CreateView(new(2026, 10, 5)), TimeZoneInfo.Utc.Id);
        Assert.Equal(saved.Id, Assert.Single(month.GetEvents(new(2026, 10, 6))).Id);
    }

    [Fact]
    public async Task FailedValidation_LeavesSeriesUnchanged()
    {
        var repository = new InMemoryEventRepository();
        var service = new CalendarEventService(repository, new InMemoryDeviceService("tests"),
            new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), TimeProvider.System);
        var draft = service.CreateDraft(new(2026, 10, 5), TimeZoneInfo.Utc.Id);
        draft.Title = "测试";
        draft.Recurrence.RepeatOption = CalendarEventRepeatOption.Daily;
        var master = await service.CreateAsync(draft);
        var edit = service.CreateDraft(master with { RecurrenceRule = null });
        edit.Title = " ";
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOccurrenceAsync(master.Id, master.StartUtc, edit));
        edit.Title = "有效";
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOccurrenceAsync(master.Id, master.StartUtc.AddMinutes(1), edit));
        edit.Recurrence.RepeatOption = CalendarEventRepeatOption.Weekly;
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOccurrenceAsync(master.Id, master.StartUtc, edit));
        Assert.Equal(master, await service.GetByIdAsync(master.Id));
    }
}
