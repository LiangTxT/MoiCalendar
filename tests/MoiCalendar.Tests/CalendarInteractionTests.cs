using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarInteractionTests
{
    [Fact]
    public async Task MoveToDate_PreservesTimedEventClockTimeAndDuration()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(
            MoveEventIntent.ToDate(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateOnly(2026, 9, 11),
                new DateOnly(2026, 9, 13),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.Committed, result.Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero), result.UpdatedEvent!.StartUtc);
        Assert.Equal(TimeSpan.FromHours(1), result.UpdatedEvent.EndUtc - result.UpdatedEvent.StartUtc);
    }

    [Fact]
    public async Task MoveTimedEvent_UsesSnappedTimesAndPreservesDuration()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await context.Repository.CreateAsync(calendarEvent);
        var result = await context.Interactions.ExecuteAsync(
            MoveEventIntent.ToTime(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateTime(2026, 9, 11, 14, 26, 0),
                new DateTime(2026, 9, 11, 15, 26, 0),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.Committed, result.Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 14, 30, 0, TimeSpan.Zero), result.UpdatedEvent!.StartUtc);
        Assert.Equal(TimeSpan.FromHours(1), result.UpdatedEvent.EndUtc - result.UpdatedEvent.StartUtc);
    }

    [Fact]
    public async Task ResizeEvent_ChangesOnlyEndAndQueuesOneNormalUpdate()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(
            new ResizeEventIntent(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateTime(2026, 9, 11, 12, 15, 0),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.Committed, result.Status);
        Assert.Equal(calendarEvent.StartUtc, result.UpdatedEvent!.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 12, 15, 0, TimeSpan.Zero), result.UpdatedEvent.EndUtc);
        Assert.Single(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Theory]
    [InlineData(9, 26, CalendarInteractionStatus.Committed)]
    [InlineData(10, 55, CalendarInteractionStatus.Rejected)]
    public async Task ResizeStart_PreservesEndAndValidatesMinimumDuration(int hour, int minute, CalendarInteractionStatus expected)
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(new ResizeEventIntent(
            calendarEvent.Id, calendarEvent.StartUtc, calendarEvent.EndUtc,
            new DateTime(2026, 9, 11, 11, 0, 0), TimeZoneInfo.Utc.Id,
            NewStartLocal: new DateTime(2026, 9, 11, hour, minute, 0)));

        Assert.Equal(expected, result.Status);
        var saved = await context.Repository.GetByIdAsync(calendarEvent.Id);
        Assert.Equal(calendarEvent.EndUtc, saved!.EndUtc);
        Assert.Equal(expected == CalendarInteractionStatus.Committed
            ? new DateTimeOffset(2026, 9, 11, 9, 30, 0, TimeSpan.Zero)
            : calendarEvent.StartUtc, saved.StartUtc);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(8, 15)]
    [InlineData(22, 15)]
    [InlineData(38, 45)]
    [InlineData(1439, 1440)]
    public void Snapping_UsesFifteenMinuteIntervals(double minute, int expected)
    {
        Assert.Equal(expected, CalendarInteractionGeometry.SnapMinute(minute));
    }

    [Fact]
    public void MonthPointerConversion_MapsPixelsToTheCorrectCalendarCell()
    {
        var index = CalendarInteractionGeometry.MonthDateIndexFromPointer(
            pointerX: 350,
            pointerY: 250,
            gridLeft: 0,
            gridTop: 0,
            gridWidth: 700,
            gridHeight: 600,
            dateCount: 42);

        Assert.Equal(17, index);
    }

    [Fact]
    public async Task Resize_RejectsInvalidDurationWithoutWriting()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(
            new ResizeEventIntent(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateTime(2026, 9, 11, 10, 0, 0),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.Rejected, result.Status);
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
        Assert.Equal(calendarEvent, await context.Repository.GetByIdAsync(calendarEvent.Id));
    }

    [Fact]
    public async Task FailedPersistence_LeavesStoredEventAtOriginalPosition()
    {
        var repository = new InMemoryEventRepository();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await repository.CreateAsync(calendarEvent);
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("interaction-failure"),
            new FailingEventChanges(),
            TimeProvider.System);
        var interactions = new CalendarInteractionService(service);

        var result = await interactions.ExecuteAsync(
            MoveEventIntent.ToTime(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateTime(2026, 9, 11, 14, 0, 0),
                new DateTime(2026, 9, 11, 15, 0, 0),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.Failed, result.Status);
        Assert.Equal(calendarEvent, await repository.GetByIdAsync(calendarEvent.Id));
    }

    [Fact]
    public async Task MoveToDate_PreservesAllDayStateAndDayCount()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            isAllDay: true);
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(
            MoveEventIntent.ToDate(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateOnly(2026, 9, 11),
                new DateOnly(2026, 9, 15),
                TimeZoneInfo.Utc.Id));

        Assert.True(result.UpdatedEvent!.IsAllDay);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), result.UpdatedEvent.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), result.UpdatedEvent.EndUtc);
    }

    [Fact]
    public async Task RecurringEvent_RequiresScopeWithoutWriting()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero)) with
        {
            RecurrenceRule = "FREQ=WEEKLY"
        };
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(
            MoveEventIntent.ToDate(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateOnly(2026, 9, 11),
                new DateOnly(2026, 9, 12),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.RecurrenceScopeRequired, result.Status);
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task NoChange_DoesNotCreateDuplicateUpdate()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(
            new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 11, 11, 0, 0, TimeSpan.Zero));
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(
            MoveEventIntent.ToTime(
                calendarEvent.Id,
                calendarEvent.StartUtc,
                calendarEvent.EndUtc,
                new DateTime(2026, 9, 11, 10, 0, 0),
                new DateTime(2026, 9, 11, 11, 0, 0),
                TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.NoChange, result.Status);
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task TimeSelection_ReturnsEditorRangeWithoutPersisting()
    {
        var context = CreateContext();
        var intent = new SelectTimeRangeIntent(
            new DateTime(2026, 9, 11, 13, 0, 0),
            new DateTime(2026, 9, 11, 14, 30, 0),
            TimeZoneInfo.Utc.Id);

        var result = await context.Interactions.ExecuteAsync(intent);

        Assert.Equal(CalendarInteractionStatus.SelectionReady, result.Status);
        var selection = Assert.IsType<SelectTimeRangeIntent>(result.Intent);
        Assert.Equal(new DateTime(2026, 9, 11, 13, 0, 0), selection.StartLocal);
        Assert.Equal(new DateTime(2026, 9, 11, 14, 30, 0), selection.EndLocal);
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task WeekHorizontalMove_ChangesDayAndPreservesDuration()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(Utc(2026, 9, 11, 10), Utc(2026, 9, 11, 11));
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(MoveEventIntent.ToTime(
            calendarEvent.Id,
            calendarEvent.StartUtc,
            calendarEvent.EndUtc,
            new DateTime(2026, 9, 12, 9, 7, 0),
            new DateTime(2026, 9, 12, 10, 7, 0),
            TimeZoneInfo.Utc.Id));

        Assert.Equal(CalendarInteractionStatus.Committed, result.Status);
        Assert.Equal(Utc(2026, 9, 12, 9), result.UpdatedEvent!.StartUtc);
        Assert.Equal(TimeSpan.FromHours(1), result.UpdatedEvent.EndUtc - result.UpdatedEvent.StartUtc);
    }

    [Fact]
    public async Task MonthMove_FromMiddleSegmentAppliesDateDeltaToWholeEvent()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(Utc(2026, 9, 10), Utc(2026, 9, 13), isAllDay: true);
        await context.Repository.CreateAsync(calendarEvent);

        var result = await context.Interactions.ExecuteAsync(MoveEventIntent.ToDate(
            calendarEvent.Id,
            calendarEvent.StartUtc,
            calendarEvent.EndUtc,
            sourceDate: new DateOnly(2026, 9, 11),
            targetDate: new DateOnly(2026, 9, 15),
            TimeZoneInfo.Utc.Id));

        Assert.Equal(Utc(2026, 9, 14), result.UpdatedEvent!.StartUtc);
        Assert.Equal(Utc(2026, 9, 17), result.UpdatedEvent.EndUtc);
        Assert.True(result.UpdatedEvent.IsAllDay);
    }

    [Fact]
    public async Task ReadOnlyIntent_IsRejectedWithoutWriting()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(Utc(2026, 9, 11, 10), Utc(2026, 9, 11, 11));
        await context.Repository.CreateAsync(calendarEvent);

        var intent = MoveEventIntent.ToTime(
            calendarEvent.Id,
            calendarEvent.StartUtc,
            calendarEvent.EndUtc,
            new DateTime(2026, 9, 11, 12, 0, 0),
            new DateTime(2026, 9, 11, 13, 0, 0),
            TimeZoneInfo.Utc.Id) with { IsReadOnly = true };
        var result = await context.Interactions.ExecuteAsync(intent);

        Assert.Equal(CalendarInteractionStatus.Rejected, result.Status);
        Assert.Equal(calendarEvent, await context.Repository.GetByIdAsync(calendarEvent.Id));
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task DuplicateCommit_CreatesOnlyOneUpdate()
    {
        var context = CreateContext();
        var calendarEvent = CreateEvent(Utc(2026, 9, 11, 10), Utc(2026, 9, 11, 11));
        await context.Repository.CreateAsync(calendarEvent);
        var intent = MoveEventIntent.ToTime(
            calendarEvent.Id,
            calendarEvent.StartUtc,
            calendarEvent.EndUtc,
            new DateTime(2026, 9, 11, 12, 0, 0),
            new DateTime(2026, 9, 11, 13, 0, 0),
            TimeZoneInfo.Utc.Id);

        var first = await context.Interactions.ExecuteAsync(intent);
        var duplicate = await context.Interactions.ExecuteAsync(intent);

        Assert.Equal(CalendarInteractionStatus.Committed, first.Status);
        Assert.Equal(CalendarInteractionStatus.Rejected, duplicate.Status);
        Assert.Single(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public async Task DateRangeSelection_DoesNotCreateOrUpdateEvent()
    {
        var context = CreateContext();

        var result = await context.Interactions.ExecuteAsync(
            new SelectDateRangeIntent(new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 14)));

        Assert.Equal(CalendarInteractionStatus.SelectionReady, result.Status);
        Assert.Empty(await context.Repository.GetAllIncludingDeletedAsync());
        Assert.Empty(await context.Operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    private static TestContext CreateContext()
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("interaction-device"),
            new InMemoryEventChangeRepository(repository, operations),
            TimeProvider.System);
        return new TestContext(repository, operations, new CalendarInteractionService(service));
    }

    private static CalendarEvent CreateEvent(
        DateTimeOffset start,
        DateTimeOffset end,
        bool isAllDay = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = "交互测试",
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = start,
        EndUtc = end,
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = isAllDay,
        CreatedAtUtc = start.AddDays(-1),
        UpdatedAtUtc = start.AddDays(-1)
    };

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private sealed record TestContext(
        InMemoryEventRepository Repository,
        InMemoryOperationRepository Operations,
        CalendarInteractionService Interactions);

    private sealed class FailingEventChanges : ILocalEventChangeRepository
    {
        public Task<CalendarEvent> CreateEventAsync(CalendarEvent calendarEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CalendarEvent> UpdateEventAsync(CalendarEvent calendarEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new EventRepositoryException("模拟写入失败。", new InvalidOperationException("测试故障"));

        public Task<bool> DeleteEventAsync(CalendarEvent deletedEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ApplyImportAsync(IReadOnlyList<CalendarImportChange> changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
