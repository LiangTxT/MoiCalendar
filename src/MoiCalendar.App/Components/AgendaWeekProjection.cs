using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

public sealed record AgendaWeekSection(DateOnly Start, DateOnly End, IReadOnlyList<CalendarAgendaDay> Days);

public static class AgendaWeekProjection
{
    public static IReadOnlyList<AgendaWeekSection> Create(CalendarMonth month, CalendarAgendaView view, bool visible, DateOnly today)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
        var days = visible ? view.Days.ToDictionary(day => day.Date) : new Dictionary<DateOnly, CalendarAgendaDay>();
        if (today >= first && today <= last && !days.ContainsKey(today)) days[today] = new CalendarAgendaDay(today, []);
        var result = new List<AgendaWeekSection>();
        for (var start = first; start <= last;)
        {
            var remaining = Math.Min(6 - (int)start.DayOfWeek, last.DayNumber - start.DayNumber);
            var end = start.AddDays(remaining);
            result.Add(new AgendaWeekSection(start, end, days.Values.Where(day => day.Date >= start && day.Date <= end).OrderBy(day => day.Date).ToArray()));
            if (end == last) break;
            start = end.AddDays(1);
        }
        return result;
    }
}
