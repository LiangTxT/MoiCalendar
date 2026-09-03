namespace MoiCalendar.Core;

public sealed class CalendarAgendaView
{
    internal CalendarAgendaView(IReadOnlyList<CalendarAgendaDay> days)
    {
        Days = days;
    }

    public IReadOnlyList<CalendarAgendaDay> Days { get; }

    public static CalendarAgendaView Empty { get; } = new(Array.Empty<CalendarAgendaDay>());
}

public sealed record CalendarAgendaDay(
    DateOnly Date,
    IReadOnlyList<CalendarEventListItem> Events);
