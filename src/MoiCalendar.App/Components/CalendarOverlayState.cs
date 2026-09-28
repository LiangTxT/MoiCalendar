using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

public enum CalendarOverlayKind
{
    None,
    QuickCreate,
    EventDetails,
    MoreEvents,
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

public sealed record RecurrenceScopeOverlayState(
    CalendarEvent CalendarEvent,
    CalendarRecurrenceAction Action,
    CalendarInteractionIntent? PendingIntent = null) :
    CalendarOverlayState(CalendarOverlayKind.RecurrenceScope);

public sealed record FullEditorOverlayState(CalendarEventDraft Draft) :
    CalendarOverlayState(CalendarOverlayKind.FullEditor);

public static class CalendarOverlayTransitions
{
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

    public static CalendarOverlayState OpenEventFromMore(
        CalendarOverlayState state,
        CalendarEvent calendarEvent) =>
        state is MoreEventsOverlayState
            ? new EventDetailsOverlayState(calendarEvent)
            : state;

    public static CalendarOverlayState OpenRecurrenceScope(
        CalendarEvent calendarEvent,
        CalendarRecurrenceAction action,
        CalendarInteractionIntent? pendingIntent = null) =>
        new RecurrenceScopeOverlayState(calendarEvent, action, pendingIntent);
}
