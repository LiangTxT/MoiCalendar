namespace MoiCalendar.Core;

public enum CalendarShortcutAction
{
    NewEvent,
    Today,
    MonthView,
    WeekView,
    DayView,
    AgendaView,
    PreviousPeriod,
    NextPeriod,
    CloseOverlay,
    OpenSelected,
    DeleteSelected,
    Search
}

public static class CalendarShortcutPolicy
{
    public static CalendarShortcutAction? Resolve(
        string? key,
        bool isTextEntry,
        bool controlKey,
        bool altKey,
        bool metaKey)
    {
        if (isTextEntry || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (key.Equals("k", StringComparison.OrdinalIgnoreCase) &&
            !altKey &&
            controlKey != metaKey)
        {
            return CalendarShortcutAction.Search;
        }

        if (controlKey || altKey || metaKey)
        {
            return null;
        }

        return key.ToLowerInvariant() switch
        {
            "n" => CalendarShortcutAction.NewEvent,
            "t" => CalendarShortcutAction.Today,
            "m" => CalendarShortcutAction.MonthView,
            "w" => CalendarShortcutAction.WeekView,
            "d" => CalendarShortcutAction.DayView,
            "a" => CalendarShortcutAction.AgendaView,
            "arrowleft" => CalendarShortcutAction.PreviousPeriod,
            "arrowright" => CalendarShortcutAction.NextPeriod,
            "escape" => CalendarShortcutAction.CloseOverlay,
            "enter" => CalendarShortcutAction.OpenSelected,
            "delete" => CalendarShortcutAction.DeleteSelected,
            _ => null
        };
    }
}
