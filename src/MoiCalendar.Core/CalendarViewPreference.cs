namespace MoiCalendar.Core;

public enum CalendarViewMode
{
    Month,
    Week,
    Agenda
}

public interface ICalendarViewPreferenceStore
{
    Task<CalendarViewMode?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CalendarViewMode viewMode, CancellationToken cancellationToken = default);
}
