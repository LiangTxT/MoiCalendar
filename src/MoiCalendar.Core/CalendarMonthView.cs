namespace MoiCalendar.Core;

public sealed class CalendarMonthView
{
    public const int WeekCount = 6;
    public const int DaysPerWeek = 7;
    public const int DateCount = WeekCount * DaysPerWeek;

    private static readonly IReadOnlyList<string> MondayFirstWeekdayLabels =
        Array.AsReadOnly(new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" });

    internal CalendarMonthView(CalendarMonth month, IReadOnlyList<CalendarDateCell> dates)
    {
        Month = month;
        Dates = dates;
    }

    public CalendarMonth Month { get; }

    public IReadOnlyList<string> WeekdayLabels => MondayFirstWeekdayLabels;

    public IReadOnlyList<CalendarDateCell> Dates { get; }
}

public sealed record CalendarDateCell(DateOnly Date, bool IsInActiveMonth, bool IsToday)
{
    public int DayNumber => Date.Day;
}
