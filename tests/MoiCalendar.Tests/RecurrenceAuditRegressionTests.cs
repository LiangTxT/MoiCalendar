using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class RecurrenceAuditRegressionTests
{
    private static CalendarEvent Master() => new()
    {
        Id = Guid.NewGuid(), Title = "重复测试", Description = "", Location = "", ColorIndex = 6,
        StartUtc = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), EndUtc = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
        TimeZoneId = "UTC", IsAllDay = false, RecurrenceRule = "FREQ=DAILY;COUNT=5", ExternalUid = "test;uid,1",
        CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportImport_RoundTripsExclusionsAndEscapedUid(bool allDay)
    {
        var repository = new InMemoryEventRepository();
        var master = Master() with { IsAllDay = allDay };
        if (allDay) master = master with { StartUtc = new(master.StartUtc.Date, TimeSpan.Zero), EndUtc = new(master.StartUtc.Date.AddDays(1), TimeSpan.Zero) };
        master = master with { ExcludedOccurrenceStartsUtc = [master.StartUtc.AddDays(1)] };
        await repository.CreateAsync(master);
        var exported = await new CalendarExportService(repository, TimeProvider.System).CreateExportAsync();
        var parsed = new CalendarImportParser().Parse(exported.Content);
        var candidate = Assert.Single(parsed.CandidateEvents);
        Assert.Equal(master.ExternalUid, candidate.ExternalUid);
        Assert.Equal(master.ExcludedOccurrenceStartsUtc, candidate.ExcludedOccurrenceStartsUtc!);
        Assert.DoesNotContain(parsed.Messages, m => m.Severity == ICalendarImportMessageSeverity.Error);
    }

    [Fact]
    public async Task ImportUpdate_PreservesColorAndMissingExclusions_AndSkipsTombstones()
    {
        var repository = new InMemoryEventRepository();
        var master = Master() with { ExcludedOccurrenceStartsUtc = [new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)] };
        await repository.CreateAsync(master);
        var service = new CalendarImportService(new CalendarImportParser(), repository,
            new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), new InMemoryDeviceService("audit"), TimeProvider.System);
        var export = await new CalendarExportService(repository, TimeProvider.System).CreateExportAsync();
        var withoutExdates = string.Join("\r\n", export.Content.Split("\r\n").Where(line => !line.StartsWith("EXDATE")));
        var preview = await service.PrepareAsync(withoutExdates);
        await service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction> { [1] = CalendarImportDuplicateAction.Update });
        var updated = (await repository.GetByIdAsync(master.Id))!;
        Assert.Equal(6, updated.ColorIndex);
        Assert.Equal(master.ExcludedOccurrenceStartsUtc, updated.ExcludedOccurrenceStartsUtc);
        await repository.UpdateAsync(updated with { DeletedAtUtc = DateTimeOffset.UtcNow });
        preview = await service.PrepareAsync(withoutExdates);
        Assert.True(Assert.Single(preview.Items).IsDeletedDuplicate);
        var result = await service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction> { [1] = CalendarImportDuplicateAction.Update });
        Assert.Equal(1, result.SkippedCount);
        Assert.NotNull((await repository.GetByIdIncludingDeletedAsync(master.Id))!.DeletedAtUtc);
    }

    [Fact]
    public void EditingInterval_PreservesUtcUntilInstant()
    {
        var service = new CalendarEventService(new InMemoryEventRepository(), new InMemoryDeviceService("audit"),
            new InMemoryEventChangeRepository(new InMemoryEventRepository(), new InMemoryOperationRepository()), TimeProvider.System);
        var draft = service.CreateDraft(Master() with { TimeZoneId = "Asia/Hong_Kong", RecurrenceRule = "FREQ=DAILY;UNTIL=20261003T160000Z" });
        draft.Recurrence.Interval = 2;
        Assert.Contains("UNTIL=20261003T160000Z", draft.Recurrence.ToRecurrenceRule(draft.StartLocal, CalendarTimeZone.Resolve(draft.TimeZoneId), false));
    }

    [Fact]
    public void EndInSpringGap_DoesNotRemoveOccurrence()
    {
        var master = Master() with { TimeZoneId = "America/New_York", StartUtc = new(2026, 3, 7, 5, 30, 0, TimeSpan.Zero), EndUtc = new(2026, 3, 7, 7, 30, 0, TimeSpan.Zero), RecurrenceRule = "FREQ=DAILY;COUNT=3" };
        var occurrences = new RecurrenceExpansionService().Expand([master], master.StartUtc, master.StartUtc.AddDays(4));
        Assert.Equal(3, occurrences.Count);
        var springDay = occurrences[1];
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), springDay.EndUtc);
        Assert.True(springDay.EndUtc > springDay.StartUtc);
    }

    [Fact]
    public void AmbiguousTime_UsesFirstOccurrence()
    {
        var utc = CalendarTimeZone.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0), CalendarTimeZone.Resolve("America/New_York"));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public async Task BrokenRecurrence_DoesNotBreakHealthyEvents()
    {
        var repository = new InMemoryEventRepository();
        await repository.CreateAsync(Master());
        await repository.CreateAsync(Master() with { RecurrenceRule = "FREQ=INVALID" });
        var service = new CalendarEventService(repository, new InMemoryDeviceService("audit"), new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), TimeProvider.System);
        var view = await service.GetMonthViewAsync(new CalendarMonth(2026, 10).CreateView(new(2026, 10, 1)), "UTC");
        Assert.Single(view.GetEvents(new(2026, 10, 1)));
        Assert.Equal(1, service.DisplayIssueCount);
    }

    [Fact]
    public async Task PendingEntity_HoldsCursorAndReplaysAfterOutboxClears()
    {
        var repository = new InMemoryEventRepository();
        var states = new InMemorySyncStateRepository();
        var outbox = new InMemorySyncOutboxRepository();
        var master = Master();
        await repository.CreateAsync(master);
        var operation = new SyncOperation { OperationId = Guid.NewGuid(), DeviceId = "audit", EntityId = master.Id, OperationType = SyncOperationType.Update,
            TimestampUtc = master.UpdatedAtUtc, Status = SyncOperationStatus.Pending, Payload = JsonSerializer.Serialize(master, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        await outbox.AddLocalOperationAsync(operation);
        var remote = master with { Title = "远端更新" };
        var apply = new InMemoryCloudChangeApplyRepository(repository, states, outbox);
        await apply.ApplyAndAdvanceCursorAsync("cloud", [new(remote, 12)], 15, DateTimeOffset.UtcNow);
        Assert.Equal(11, (await states.GetAsync())!.LastSuccessfulServerRevision);
        Assert.Equal(master.Title, (await repository.GetByIdAsync(master.Id))!.Title);
        await outbox.RemoveAsync(operation.OperationId);
        await apply.ApplyAndAdvanceCursorAsync("cloud", [new(remote, 12)], 15, DateTimeOffset.UtcNow);
        Assert.Equal(15, (await states.GetAsync())!.LastSuccessfulServerRevision);
        Assert.Empty((await states.GetAsync())!.DeferredEntityRevisions!);
        Assert.Equal(remote.Title, (await repository.GetByIdAsync(master.Id))!.Title);
    }

    [Fact]
    public void CloudProjection_CoversEveryCalendarEventField()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MoiCalendar.slnx"))) root = root.Parent;
        var sql = File.ReadAllText(Path.Combine(root!.FullName, "supabase/migrations/20261006020000_calendar_reminders.sql"));
        var projection = sql[sql.IndexOf("function public.moicalendar_calendar_event_json", StringComparison.Ordinal)..];
        projection = projection[..projection.IndexOf("$$;", StringComparison.Ordinal)];
        foreach (var property in typeof(CalendarEvent).GetProperties())
            Assert.Contains("'" + JsonNamingPolicy.CamelCase.ConvertName(property.Name) + "'", projection);
    }

    [Fact]
    public void SameSeriesMerge_CombinesDeletesWithoutResurrectingOrMixingAnchors()
    {
        var master = Master();
        var first = master with { ExcludedOccurrenceStartsUtc = [master.StartUtc.AddDays(1)] };
        var second = master with { ExcludedOccurrenceStartsUtc = [master.StartUtc.AddDays(2)] };
        Assert.Equal(2, RecurrenceExclusionMerge.Merge(first, second).ExcludedOccurrenceStartsUtc.Length);
        var tombstone = first with { DeletedAtUtc = DateTimeOffset.UtcNow };
        Assert.Equal(tombstone, RecurrenceExclusionMerge.Merge(tombstone, second));
        var shifted = second with { StartUtc = master.StartUtc.AddHours(1) };
        Assert.Equal(shifted, RecurrenceExclusionMerge.Merge(shifted, first));
    }

    [Fact]
    public async Task RecurringNoChange_IsHandledBeforeScopeSelection()
    {
        var repository = new InMemoryEventRepository();
        var master = Master();
        await repository.CreateAsync(master);
        var service = new CalendarEventService(repository, new InMemoryDeviceService("audit"), new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), TimeProvider.System);
        var result = await new CalendarInteractionService(service).ExecuteAsync(MoveEventIntent.ToTime(master.Id,
            master.StartUtc.AddDays(1), master.EndUtc.AddDays(1), master.StartUtc.AddDays(1).UtcDateTime,
            master.EndUtc.AddDays(1).UtcDateTime, "UTC"));
        Assert.Equal(CalendarInteractionStatus.NoChange, result.Status);
    }
}
