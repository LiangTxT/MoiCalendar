using MoiCalendar.App.Components;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarOverlayStateTests
{
    [Fact]
    public void MonthQuickCreate_OpensOneAuthoritativeOverlayWithDatePrefilled()
    {
        var draft = Draft(new DateTime(2026, 9, 28, 9, 0, 0));

        var state = CalendarOverlayTransitions.OpenQuickCreate(draft);

        var quickCreate = Assert.IsType<QuickCreateOverlayState>(state);
        Assert.Equal(CalendarOverlayKind.QuickCreate, quickCreate.Kind);
        Assert.Equal(new DateOnly(2026, 9, 28), DateOnly.FromDateTime(quickCreate.Draft.StartLocal));
    }

    [Fact]
    public async Task SelectedTimeRange_ProducesQuickCreateDraftWithoutPersisting()
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var service = new CalendarInteractionService(new CalendarEventService(
            repository,
            new InMemoryDeviceService("overlay-selection"),
            new InMemoryEventChangeRepository(repository, operations),
            TimeProvider.System));
        var intent = new SelectTimeRangeIntent(
            new DateTime(2026, 9, 28, 13, 0, 0),
            new DateTime(2026, 9, 28, 14, 30, 0),
            TimeZoneInfo.Utc.Id);

        var result = await service.ExecuteAsync(intent);
        var range = Assert.IsType<SelectTimeRangeIntent>(result.Intent);
        var draft = Draft(range.StartLocal);
        draft.EndLocal = range.EndLocal;
        var state = CalendarOverlayTransitions.OpenQuickCreate(draft);

        Assert.IsType<QuickCreateOverlayState>(state);
        Assert.Empty(await repository.GetAllIncludingDeletedAsync());
        Assert.Empty(await operations.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public void MoreDetails_TransfersTheSameDraftWithoutLosingUserInput()
    {
        var draft = Draft(new DateTime(2026, 9, 28, 9, 0, 0));
        draft.Title = "保留这个标题";
        draft.Location = "会议室";
        CalendarOverlayState state = CalendarOverlayTransitions.OpenQuickCreate(draft);

        state = CalendarOverlayTransitions.PromoteQuickCreate(state);

        var editor = Assert.IsType<FullEditorOverlayState>(state);
        Assert.Same(draft, editor.Draft);
        Assert.Equal("保留这个标题", editor.Draft.Title);
        Assert.Equal("会议室", editor.Draft.Location);
    }

    [Fact]
    public void EventDetails_ContainsTheSelectedEvent()
    {
        var calendarEvent = Event();

        var state = CalendarOverlayTransitions.OpenEventDetails(calendarEvent);

        Assert.Same(calendarEvent, Assert.IsType<EventDetailsOverlayState>(state).CalendarEvent);
    }

    [Fact]
    public void MoreEvents_TransitionsDirectlyToEventDetails()
    {
        CalendarOverlayState state = CalendarOverlayTransitions.OpenMoreEvents(new DateOnly(2026, 9, 28));
        var calendarEvent = Event();

        state = CalendarOverlayTransitions.OpenEventFromMore(state, calendarEvent);

        Assert.Same(calendarEvent, Assert.IsType<EventDetailsOverlayState>(state).CalendarEvent);
    }

    [Fact]
    public async Task FailedCreate_LeavesTheSameDraftInQuickCreateState()
    {
        var draft = Draft(new DateTime(2026, 9, 28, 9, 0, 0));
        draft.Title = "写入失败后不能丢失";
        CalendarOverlayState state = CalendarOverlayTransitions.OpenQuickCreate(draft);
        var repository = new InMemoryEventRepository();
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("overlay-failure"),
            new FailingCreateChanges(),
            TimeProvider.System);

        await Assert.ThrowsAsync<EventRepositoryException>(() => service.CreateAsync(draft));

        var quickCreate = Assert.IsType<QuickCreateOverlayState>(state);
        Assert.Same(draft, quickCreate.Draft);
        Assert.Equal("写入失败后不能丢失", quickCreate.Draft.Title);
    }

    [Fact]
    public async Task MoreEvents_ReadFromPreviousDateCannotReplaceNewDatePopup()
    {
        var gate = new CalendarOverlayRequestGate();
        CalendarOverlayState state = CalendarOverlayTransitions.OpenMoreEvents(new DateOnly(2026, 9, 28));
        var request = gate.BeginRequest();
        var read = new TaskCompletionSource<CalendarEvent>();
        var pending = CompleteAsync();

        state = CalendarOverlayTransitions.OpenMoreEvents(new DateOnly(2026, 9, 29));
        gate.Invalidate();
        read.SetResult(Event());
        await pending;

        Assert.Equal(new DateOnly(2026, 9, 29), Assert.IsType<MoreEventsOverlayState>(state).Date);

        async Task CompleteAsync()
        {
            var calendarEvent = await read.Task;
            if (gate.IsCurrent(request))
            {
                state = CalendarOverlayTransitions.OpenEventFromMore(state, calendarEvent);
            }
        }
    }

    [Fact]
    public async Task PendingEditor_FocusWaitCannotOverwriteQuickCreateDraft()
    {
        var gate = new CalendarOverlayRequestGate();
        var request = gate.BeginRequest();
        var focus = new TaskCompletionSource<bool>();
        CalendarOverlayState state = CalendarOverlayState.None;
        var pending = CompleteAsync();

        var draft = Draft(new DateTime(2026, 9, 28, 9, 0, 0));
        draft.Title = "新输入的草稿";
        state = CalendarOverlayTransitions.OpenQuickCreate(draft);
        gate.Invalidate();
        focus.SetResult(true);
        await pending;

        Assert.Same(draft, Assert.IsType<QuickCreateOverlayState>(state).Draft);
        Assert.Equal("新输入的草稿", draft.Title);

        async Task CompleteAsync()
        {
            await focus.Task;
            if (gate.IsCurrent(request))
            {
                state = new FullEditorOverlayState(Draft(new DateTime(2026, 9, 28, 10, 0, 0)));
            }
        }
    }

    private static CalendarEventDraft Draft(DateTime start) => new()
    {
        Title = string.Empty,
        Description = string.Empty,
        Location = string.Empty,
        StartLocal = start,
        EndLocal = start.AddHours(1),
        TimeZoneId = TimeZoneInfo.Utc.Id
    };

    private static CalendarEvent Event() => new()
    {
        Id = Guid.NewGuid(),
        Title = "详情事件",
        Description = "备注",
        Location = "办公室",
        StartUtc = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero),
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = false,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class FailingCreateChanges : ILocalEventChangeRepository
    {
        public Task<CalendarEvent> CreateEventAsync(CalendarEvent calendarEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new EventRepositoryException("模拟创建失败。", new InvalidOperationException("测试故障"));

        public Task<CalendarEvent> UpdateEventAsync(CalendarEvent calendarEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEventAsync(CalendarEvent deletedEvent, SyncOperation operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ApplyImportAsync(IReadOnlyList<CalendarImportChange> changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
