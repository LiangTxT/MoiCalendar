using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;

namespace MoiCalendar.Tests;

public sealed class CalendarImportServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshImport_WritesOnlyAfterConfirmationAndCreatesPendingOperation()
    {
        var context = CreateContext();
        var preview = await context.Service.PrepareAsync(Calendar(Event("new-uid", "新事件")), "fresh.ics");

        Assert.Empty(await context.Events.GetAllIncludingDeletedAsync());
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));

        var result = await context.Service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());

        Assert.Equal(1, result.CreatedCount);
        var imported = Assert.Single(await context.Events.GetAllIncludingDeletedAsync());
        Assert.Equal("new-uid", imported.ExternalUid);
        var operation = Assert.Single(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
        Assert.Equal(SyncOperationType.Create, operation.OperationType);
        Assert.Contains("new-uid", operation.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateUid_DefaultsToSkipAndDoesNotGenerateAnotherOperation()
    {
        var context = CreateContext();
        var first = await context.Service.PrepareAsync(Calendar(Event("same-uid", "原事件")));
        await context.Service.ConfirmAsync(first.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());
        var second = await context.Service.PrepareAsync(Calendar(Event("same-uid", "新标题")));

        var result = await context.Service.ConfirmAsync(second.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());

        Assert.Equal(1, second.PotentialDuplicateCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal("原事件", Assert.Single(await context.Events.GetAllIncludingDeletedAsync()).Title);
        Assert.Single(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task SameExternalUid_OnDifferentDevices_UsesSameLogicalEventId()
    {
        var firstContext = CreateContext();
        var secondContext = CreateContext();
        var firstPreview = await firstContext.Service.PrepareAsync(Calendar(Event("global-uid", "设备一")));
        var secondPreview = await secondContext.Service.PrepareAsync(Calendar(Event("global-uid", "设备二")));

        await firstContext.Service.ConfirmAsync(firstPreview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());
        await secondContext.Service.ConfirmAsync(secondPreview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());

        Assert.Equal(
            Assert.Single(await firstContext.Events.GetAllIncludingDeletedAsync()).Id,
            Assert.Single(await secondContext.Events.GetAllIncludingDeletedAsync()).Id);
    }

    [Fact]
    public async Task DuplicateUid_UpdateReusesLocalIdAndCreatesUpdateOperation()
    {
        var context = CreateContext();
        var first = await context.Service.PrepareAsync(Calendar(Event("same-uid", "原事件")));
        await context.Service.ConfirmAsync(first.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());
        var original = Assert.Single(await context.Events.GetAllIncludingDeletedAsync());
        var second = await context.Service.PrepareAsync(Calendar(Event("same-uid", "已更新")));

        var result = await context.Service.ConfirmAsync(second.ImportId, new Dictionary<int, CalendarImportDuplicateAction>
        {
            [Assert.Single(second.Items).ItemNumber] = CalendarImportDuplicateAction.Update
        });

        var updated = Assert.Single(await context.Events.GetAllIncludingDeletedAsync());
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal("已更新", updated.Title);
        Assert.Equal(2, (await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending)).Count);
    }

    [Fact]
    public async Task Cancel_MakesZeroPersistentChangesAndInvalidatesConfirmation()
    {
        var context = CreateContext();
        var preview = await context.Service.PrepareAsync(Calendar(Event("cancel", "取消")));

        context.Service.Cancel(preview.ImportId);

        await Assert.ThrowsAsync<CalendarImportException>(() =>
            context.Service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>()));
        Assert.Empty(await context.Events.GetAllIncludingDeletedAsync());
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task RecurringImport_PreservesSupportedRule()
    {
        var context = CreateContext();
        var preview = await context.Service.PrepareAsync(Calendar("""
            BEGIN:VEVENT
            UID:weekly
            SUMMARY:每周事件
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            RRULE:FREQ=WEEKLY;BYDAY=MO,TH;COUNT=5
            END:VEVENT
            """));

        await context.Service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());

        Assert.Equal(
            "FREQ=WEEKLY;BYDAY=MO,TH;COUNT=5",
            Assert.Single(await context.Events.GetAllIncludingDeletedAsync()).RecurrenceRule);
    }

    [Fact]
    public async Task PartialParseWarnings_ImportOnlyValidCandidate()
    {
        var context = CreateContext();
        var preview = await context.Service.PrepareAsync(Calendar("""
            BEGIN:VEVENT
            UID:broken
            SUMMARY:坏事件
            DTSTART:20260903T010000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:valid
            SUMMARY:有效事件
            DTSTART:20260904T010000Z
            DTEND:20260904T020000Z
            ATTACH:https://example.com/a
            END:VEVENT
            """));

        var result = await context.Service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());

        Assert.Equal(2, preview.TotalEventCount);
        Assert.Equal(1, preview.ValidEventCount);
        Assert.Equal(1, preview.ErrorCount);
        Assert.Equal(1, preview.WarningCount);
        Assert.Equal(1, result.CreatedCount);
    }

    [Fact]
    public async Task ImportThenSync_UploadsGeneratedOperation()
    {
        var context = CreateContext();
        var preview = await context.Service.PrepareAsync(Calendar(Event("sync-uid", "同步事件")));
        await context.Service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());
        var provider = new RecordingSyncStorageProvider();
        var sync = new SyncService(context.Operations, context.Events, provider);

        var result = await sync.PushAsync();

        Assert.Equal(1, result.PushedCount);
        Assert.Single(provider.UploadedPaths);
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task ExportImportRoundTrip_PreservesSupportedFieldsAndExternalUid()
    {
        var source = new InMemoryEventRepository();
        await source.CreateAsync(new CalendarEvent
        {
            Id = Guid.NewGuid(),
            ExternalUid = "round-trip@example.com",
            Title = "往返 🌏",
            Description = "第一行\n第二行",
            Location = "香港, 九龙",
            StartUtc = new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero),
            TimeZoneId = "UTC",
            IsAllDay = false,
            RecurrenceRule = "FREQ=DAILY;COUNT=3",
            CreatedAtUtc = Now.AddDays(-1),
            UpdatedAtUtc = Now
        });
        var export = await new CalendarExportService(source, new FixedTimeProvider(Now)).CreateExportAsync();
        var context = CreateContext();

        var preview = await context.Service.PrepareAsync(export.Content);
        await context.Service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>());

        var imported = Assert.Single(await context.Events.GetAllIncludingDeletedAsync());
        Assert.Equal("round-trip@example.com", imported.ExternalUid);
        Assert.Equal("往返 🌏", imported.Title);
        Assert.Equal("第一行\n第二行", imported.Description);
        Assert.Equal("香港, 九龙", imported.Location);
        Assert.Equal("FREQ=DAILY;COUNT=3", imported.RecurrenceRule);
    }

    [Fact]
    public async Task RepositoryFailure_IsReportedWithoutPretendingImportSucceeded()
    {
        var events = new InMemoryEventRepository();
        var service = new CalendarImportService(
            new CalendarImportParser(),
            events,
            new FailingImportRepository(),
            new InMemoryDeviceService("failure-device"),
            new FixedTimeProvider(Now));
        var preview = await service.PrepareAsync(Calendar(Event("failure", "失败")));

        var exception = await Assert.ThrowsAsync<CalendarImportException>(() =>
            service.ConfirmAsync(preview.ImportId, new Dictionary<int, CalendarImportDuplicateAction>()));

        Assert.Contains("事务已中止", exception.Message, StringComparison.Ordinal);
        Assert.Empty(await events.GetAllIncludingDeletedAsync());
    }

    [Fact]
    public void Parser_DuplicateUidWithinFile_KeepsOnlyFirstAndReportsError()
    {
        var result = new CalendarImportParser().Parse(Calendar(
            Event("duplicate", "第一个") + Environment.NewLine + Event("duplicate", "第二个")));

        Assert.Equal(2, result.TotalEventCount);
        Assert.Equal("第一个", Assert.Single(result.CandidateEvents).Title);
        Assert.Contains(result.Errors, error => error.Code == "DUPLICATE_SOURCE_UID");
    }

    private static TestContext CreateContext()
    {
        var events = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var changes = new InMemoryEventChangeRepository(events, operations);
        return new TestContext(
            new CalendarImportService(
                new CalendarImportParser(),
                events,
                changes,
                new InMemoryDeviceService("import-device"),
                new FixedTimeProvider(Now)),
            events,
            operations);
    }

    private static string Event(string uid, string title) => $$"""
        BEGIN:VEVENT
        UID:{{uid}}
        SUMMARY:{{title}}
        DTSTART:20260903T010000Z
        DTEND:20260903T020000Z
        END:VEVENT
        """;

    private static string Calendar(string events) => $$"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        {{events}}
        END:VCALENDAR
        """;

    private sealed record TestContext(
        CalendarImportService Service,
        InMemoryEventRepository Events,
        InMemoryOperationRepository Operations);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailingImportRepository : ILocalEventChangeRepository
    {
        public Task ApplyImportAsync(IReadOnlyList<CalendarImportChange> changes, CancellationToken cancellationToken = default) =>
            throw new SyncOperationException("模拟失败", new InvalidOperationException());

        public Task<CalendarEvent> CreateEventAsync(CalendarEvent calendarEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CalendarEvent> UpdateEventAsync(CalendarEvent calendarEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEventAsync(CalendarEvent deletedEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSyncStorageProvider : ISyncStorageProvider
    {
        public List<string> UploadedPaths { get; } = [];

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<SyncTextFile?> DownloadTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<SyncTextFile?>(null);

        public Task<SyncFileMetadata> UploadTextAsync(string path, string content, string? expectedVersionToken = null, CancellationToken cancellationToken = default)
        {
            UploadedPaths.Add(path);
            return Task.FromResult(new SyncFileMetadata(path, "v1", content.Length, Now));
        }

        public Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncFileMetadata>>([]);

        public Task<bool> DeleteAsync(string path, string? expectedVersionToken = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
