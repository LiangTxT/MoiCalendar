using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

public enum CalendarOverlayKind
{
    None,
    QuickCreate,
    EventDetails,
    MoreEvents,
    Search,
    RecurrenceScope,
    FullEditor
}

public enum CalendarRecurrenceAction
{
    EditSeries,
    DeleteSeries,
    ApplyInteractionToSeries
}

public abstract record CalendarOverlayState(CalendarOverlayKind Kind)
{
    public static CalendarOverlayState None { get; } = new NoCalendarOverlay();
}

public sealed record NoCalendarOverlay() : CalendarOverlayState(CalendarOverlayKind.None);

public sealed record QuickCreateOverlayState(CalendarEventDraft Draft) :
    CalendarOverlayState(CalendarOverlayKind.QuickCreate);

public sealed record EventDetailsOverlayState(CalendarEvent CalendarEvent) :
    CalendarOverlayState(CalendarOverlayKind.EventDetails);

public sealed record MoreEventsOverlayState(DateOnly Date) :
    CalendarOverlayState(CalendarOverlayKind.MoreEvents);

public sealed record SearchOverlayState() :
    CalendarOverlayState(CalendarOverlayKind.Search);

public sealed record RecurrenceScopeOverlayState(
    CalendarEvent CalendarEvent,
    CalendarRecurrenceAction Action,
    CalendarInteractionIntent? PendingIntent = null,
    DateTimeOffset? OccurrenceStartUtc = null) :
    CalendarOverlayState(CalendarOverlayKind.RecurrenceScope);

public sealed record FullEditorOverlayState(CalendarEventDraft Draft, CalendarOccurrenceReference? Occurrence = null) :
    CalendarOverlayState(CalendarOverlayKind.FullEditor);

public static class CalendarOverlayTransitions
{
    /// <summary>延迟的日历单击不能覆盖正在使用的任务弹层。</summary>
    public static bool CanOpenEventFromCalendar(CalendarOverlayKind kind) =>
        kind is CalendarOverlayKind.None or CalendarOverlayKind.EventDetails;

    public static CalendarOverlayState OpenQuickCreate(CalendarEventDraft draft) =>
        new QuickCreateOverlayState(draft);

    public static CalendarOverlayState PromoteQuickCreate(CalendarOverlayState state) =>
        state is QuickCreateOverlayState quickCreate
            ? new FullEditorOverlayState(quickCreate.Draft)
            : state;

    public static CalendarOverlayState OpenEventDetails(CalendarEvent calendarEvent) =>
        new EventDetailsOverlayState(calendarEvent);

    public static CalendarOverlayState OpenMoreEvents(DateOnly date) =>
        new MoreEventsOverlayState(date);

    public static CalendarOverlayState OpenSearch() => new SearchOverlayState();

    public static CalendarOverlayState OpenEventFromMore(
        CalendarOverlayState state,
        CalendarEvent calendarEvent) =>
        state is MoreEventsOverlayState
            ? new EventDetailsOverlayState(calendarEvent)
            : state;

    public static CalendarOverlayState OpenRecurrenceScope(
        CalendarEvent calendarEvent,
        CalendarRecurrenceAction action,
        CalendarInteractionIntent? pendingIntent = null,
        DateTimeOffset? occurrenceStartUtc = null) =>
        new RecurrenceScopeOverlayState(calendarEvent, action, pendingIntent, occurrenceStartUtc);
}
