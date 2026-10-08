using System.Globalization;
using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class FollowupAuditRegressionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CalendarEvent Master() => new()
    {
        Id = Guid.NewGuid(), Title = "复验", Description = "", Location = "", ColorIndex = 7,
        StartUtc = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), EndUtc = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
        TimeZoneId = "UTC", IsAllDay = false, RecurrenceRule = "FREQ=DAILY;COUNT=5",
        CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
    };
    private static CalendarEventService Service(InMemoryEventRepository repository) => new(repository,
        new InMemoryDeviceService("followup"), new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), TimeProvider.System);
    private static CalendarImportService Import(InMemoryEventRepository repository) => new(new CalendarImportParser(), repository,
        new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), new InMemoryDeviceService("followup"), TimeProvider.System);

    [Fact]
    public async Task SelfExportImport_UpdatesOriginalId_AndSkipsDeletedOriginal()
    {
        var repository = new InMemoryEventRepository();
        var item = Master();
        await repository.CreateAsync(item);
        var export = await new CalendarExportService(repository, TimeProvider.System).CreateExportAsync();
        var importer = Import(repository);
        var preview = await importer.PrepareAsync(export.Content);
        Assert.Equal(item.Id, Assert.Single(preview.Items).ExistingEventId);
        await importer.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction> { [1] = CalendarImportDuplicateAction.Update });
        Assert.Single(await repository.GetAllIncludingDeletedAsync());
        await repository.UpdateAsync(item with { DeletedAtUtc = DateTimeOffset.UtcNow });
        preview = await importer.PrepareAsync(export.Content);
        Assert.Equal(0, preview.PotentialDuplicateCount);
        Assert.Equal(1, (await importer.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction> { [1] = CalendarImportDuplicateAction.Update })).SkippedCount);
    }

    [Fact]
    public async Task ExplicitImportExdates_UnionWithRemappedLocalExclusions()
    {
        var repository = new InMemoryEventRepository();
        var item = Master() with { ExternalUid = "union", ExcludedOccurrenceStartsUtc = [new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)] };
        await repository.CreateAsync(item);
        var fileRepository = new InMemoryEventRepository();
        await fileRepository.CreateAsync(item with { StartUtc = item.StartUtc.AddHours(1), EndUtc = item.EndUtc.AddHours(1),
            ExcludedOccurrenceStartsUtc = [item.StartUtc.AddDays(2).AddHours(1)] });
        var export = await new CalendarExportService(fileRepository, TimeProvider.System).CreateExportAsync();
        var importer = Import(repository);
        var preview = await importer.PrepareAsync(export.Content);
        await importer.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction> { [1] = CalendarImportDuplicateAction.Update });
        var saved = (await repository.GetByIdAsync(item.Id))!;
        Assert.Equal(new[] { item.StartUtc.AddDays(1).AddHours(1), item.StartUtc.AddDays(2).AddHours(1) }, saved.ExcludedOccurrenceStartsUtc);
    }

    [Fact]
    public async Task KeepLocalConflict_PreservesBothDeletions_ThenReleasesDeferredCursorAfterAcknowledgment()
    {
        var repository = new InMemoryEventRepository();
        var item = Master();
        var local = item with { ExcludedOccurrenceStartsUtc = [item.StartUtc.AddDays(1)] };
        var remote = item with { ExcludedOccurrenceStartsUtc = [item.StartUtc.AddDays(2)] };
        await repository.CreateAsync(local);
        var outbox = new InMemorySyncOutboxRepository(repository);
        var conflict = new SyncOutboxEntry { MutationId = Guid.NewGuid(), DeviceId = Guid.NewGuid(), EntityType = SyncEntityType.CalendarEvent,
            EntityId = item.Id, Operation = SyncOperationType.Update, Payload = JsonSerializer.Serialize(local, Json),
            CreatedAtUtc = item.UpdatedAtUtc, ConflictCode = "stale_revision", ConflictServerRevision = 12, ConflictRemoteEvent = remote };
        await outbox.AddAsync(conflict);
        var states = new InMemorySyncStateRepository();
        var apply = new InMemoryCloudChangeApplyRepository(repository, states, outbox);
        await apply.ApplyAndAdvanceCursorAsync("cloud", [new(remote, 12)], 15, DateTimeOffset.UtcNow);
        var replacement = await outbox.KeepLocalAsync(conflict.MutationId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var merged = JsonSerializer.Deserialize<CalendarEvent>(replacement.Payload, Json)!;
        Assert.Equal(2, merged.ExcludedOccurrenceStartsUtc.Length);
        Assert.Equal(merged.ExcludedOccurrenceStartsUtc, (await repository.GetByIdAsync(item.Id))!.ExcludedOccurrenceStartsUtc);
        Assert.Equal(11, (await states.GetAsync())!.LastSuccessfulServerRevision);
        await outbox.RemoveAsync(replacement.MutationId); // Server acknowledged the replacement.
        await apply.ApplyAndAdvanceCursorAsync("cloud", [new(remote, 12), new(merged, 16)], 16, DateTimeOffset.UtcNow);
        Assert.Empty((await states.GetAsync())!.DeferredEntityRevisions!);
        Assert.Equal(16, (await states.GetAsync())!.LastSuccessfulServerRevision);
        Assert.Equal(2, (await repository.GetByIdAsync(item.Id))!.ExcludedOccurrenceStartsUtc.Length);
    }

    [Fact]
    public async Task AllDayIcs_PreservesAnchorZoneColorAndExdates_UnderNonGregorianCulture()
    {
        var repository = new InMemoryEventRepository();
        var zone = CalendarTimeZone.Resolve("Asia/Hong_Kong");
        var start = CalendarTimeZone.ToUtc(new DateTime(2026, 10, 1), zone);
        var item = Master() with { StartUtc = start, EndUtc = start.AddDays(1), TimeZoneId = zone.Id, IsAllDay = true,
            ExcludedOccurrenceStartsUtc = [start.AddDays(1)] };
        await repository.CreateAsync(item);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var export = await new CalendarExportService(repository, TimeProvider.System).CreateExportAsync();
            var candidate = Assert.Single(new CalendarImportParser().Parse(export.Content).CandidateEvents);
            Assert.Equal(item.StartUtc, candidate.StartUtc);
            Assert.Equal(item.TimeZoneId, candidate.TimeZoneId);
            Assert.Equal(7, candidate.ColorIndex);
            Assert.Equal(item.ExcludedOccurrenceStartsUtc, candidate.ExcludedOccurrenceStartsUtc);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task ChangingStartTime_RetainsUntilFinalCalendarDate()
    {
        var repository = new InMemoryEventRepository();
        var item = Master() with { RecurrenceRule = "FREQ=DAILY;UNTIL=20261005T090000Z" };
        await repository.CreateAsync(item);
        var service = Service(repository);
        var draft = service.CreateDraft(item);
        draft.StartLocal = draft.StartLocal.AddHours(1);
        draft.EndLocal = draft.EndLocal.AddHours(1);
        var saved = await service.UpdateAsync(item.Id, draft);
        var occurrences = new RecurrenceExpansionService().Expand([saved], saved.StartUtc, saved.StartUtc.AddDays(7));
        Assert.Equal(5, occurrences.Count);
    }

    [Fact]
    public async Task DraftToggle_RestoresTimedHours_AndExistingAllDayDefaultsToOneHour()
    {
        var service = Service(new InMemoryEventRepository());
        var item = Master() with { StartUtc = Master().StartUtc.AddHours(5), EndUtc = Master().EndUtc.AddHours(5) };
        var draft = service.CreateDraft(item);
        var start = draft.StartLocal;
        draft.SetAllDay(true);
        draft.SetAllDay(false);
        Assert.Equal(start, draft.StartLocal);
        Assert.Equal(TimeSpan.FromHours(1), draft.EndLocal - draft.StartLocal);
        var allDay = service.CreateDraft(item with { StartUtc = new(item.StartUtc.Date, TimeSpan.Zero), EndUtc = new(item.StartUtc.Date.AddDays(1), TimeSpan.Zero), IsAllDay = true });
        allDay.SetAllDay(false);
        Assert.Equal(TimeSpan.FromHours(1), allDay.EndLocal - allDay.StartLocal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StaleWholeSeriesEditor_DoesNotOverwriteConcurrentChanges()
    {
        var repository = new InMemoryEventRepository();
        var item = Master();
        await repository.CreateAsync(item);
        var service = Service(repository);
        var draft = service.CreateDraft(item);
        await repository.UpdateAsync(item with { Title = "另一设备", UpdatedAtUtc = item.UpdatedAtUtc.AddTicks(1) });
        await Assert.ThrowsAsync<EventRepositoryException>(() => service.UpdateAsync(item.Id, draft));
        Assert.Equal("另一设备", (await repository.GetByIdAsync(item.Id))!.Title);
    }

    [Fact]
    public async Task InvalidRule_HasRecoveryEntry_DoesNotBlockExport_AndDeleteClearsWarning()
    {
        var repository = new InMemoryEventRepository();
        var invalid = Master() with { RecurrenceRule = "FREQ=DAILY;COUNT=3;UNTIL=20261005" };
        await repository.CreateAsync(invalid);
        await repository.CreateAsync(Master());
        var service = Service(repository);
        Assert.Equal(invalid.Id, Assert.Single(await service.GetInvalidEventsAsync()).Id);
        var export = await new CalendarExportService(repository, TimeProvider.System).CreateExportAsync();
        Assert.Single(export.Warnings);
        Assert.Equal(2, export.Content.Split("BEGIN:VEVENT").Length - 1);
        await service.GetMonthViewAsync(new CalendarMonth(2026, 10).CreateView(new(2026, 10, 1)), "UTC");
        Assert.Equal(1, service.DisplayIssueCount);
        await service.DeleteAsync(invalid.Id);
        await service.GetMonthViewAsync(new CalendarMonth(2026, 10).CreateView(new(2026, 10, 1)), "UTC");
        Assert.Equal(0, service.DisplayIssueCount);
    }

    [Fact]
    public void PrefixedTimeZone_ResolvesCanonicalZone() =>
        Assert.Equal(CalendarTimeZone.Resolve("America/New_York").Id, CalendarTimeZone.Resolve("/freeassociation.sourceforge.net/Tzfile/America/New_York").Id);

    [Fact]
    public async Task AgendaBatch_MatchesIndividualMonths_AndReflectsUpdates()
    {
        var repository = new InMemoryEventRepository();
        var item = Master() with { RecurrenceRule = "FREQ=DAILY;COUNT=70" };
        await repository.CreateAsync(item);
        var service = Service(repository);
        CalendarMonth[] months = [new(2026, 10), new(2026, 11), new(2026, 12)];
        var batch = await service.GetAgendaViewsAsync(months, "UTC");
        foreach (var month in months)
        {
            var individual = await service.GetAgendaViewAsync(month, "UTC");
            Assert.Equal(individual.Days.Select(day => (day.Date, day.Events.Count)), batch[month].Days.Select(day => (day.Date, day.Events.Count)));
        }
        await repository.UpdateAsync(item with { Title = "刷新后" });
        batch = await service.GetAgendaViewsAsync(months, "UTC");
        Assert.Equal("刷新后", batch[months[1]].Days[0].Events[0].Title);
    }

    [Fact]
    public async Task MonthBufferBatch_MatchesIndividualGrids_WithOnlyOneRecurrenceExpansion()
    {
        var repository = new InMemoryEventRepository();
        await repository.CreateAsync(Master() with { RecurrenceRule = "FREQ=DAILY;COUNT=70" });
        var counter = new CountingExpansion();
        var service = new CalendarEventService(repository, new InMemoryDeviceService("batch"),
            new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), TimeProvider.System, counter);
        var views = new[] { new CalendarMonth(2026, 10), new CalendarMonth(2026, 11), new CalendarMonth(2026, 12) }
            .Select(month => month.CreateView(new(2026, 10, 1))).ToArray();
        var batch = await service.GetMonthViewsAsync(views, "UTC");
        Assert.Equal(1, counter.Calls);
        foreach (var view in views)
        {
            var individual = await service.GetMonthViewAsync(view, "UTC");
            foreach (var date in view.Dates)
                Assert.Equal(individual.GetEvents(date.Date), batch[view.Month].GetEvents(date.Date));
        }
        var empty = await service.GetMonthViewsAsync([], "UTC");
        Assert.Empty(empty);
    }

    [Fact]
    public void RemappingManyExclusions_ExpandsOnlyOnce()
    {
        var master = Master();
        master = master with { ExcludedOccurrenceStartsUtc = [master.StartUtc.AddDays(1), master.StartUtc.AddDays(2)] };
        var counter = new CountingExpansion();
        var mapped = RecurrenceExclusionPolicy.Remap(master, master with { StartUtc = master.StartUtc.AddHours(1), EndUtc = master.EndUtc.AddHours(1) }, counter);
        Assert.Equal(2, mapped.Length);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public void VersionNormalization_RetainsPrereleaseSuffix_WithoutAcceptingArbitraryText()
    {
        Assert.Equal("2.0.30-beta.1", ApplicationVersion.Normalize("2.0.30-beta.1"));
        Assert.Null(ApplicationVersion.Normalize("2.0.30-token=secret"));
    }

    private sealed class CountingExpansion : IRecurrenceExpansionService
    {
        public int Calls { get; private set; }
        public IReadOnlyList<CalendarEvent> Expand(IEnumerable<CalendarEvent> events, DateTimeOffset start, DateTimeOffset end)
        {
            Calls++;
            return new RecurrenceExpansionService().Expand(events, start, end);
        }
    }
}
