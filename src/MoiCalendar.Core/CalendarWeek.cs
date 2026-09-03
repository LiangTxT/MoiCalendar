namespace MoiCalendar.Core;

public readonly record struct CalendarWeek
{
    public const int DayCount = 7;

    public CalendarWeek(DateOnly startDate)
    {
        if (startDate.DayOfWeek != DayOfWeek.Monday)
        {
            throw new ArgumentException("每周必须从星期一开始。", nameof(startDate));
        }

        StartDate = startDate;
    }

    public DateOnly StartDate { get; }

    public DateOnly EndDate => StartDate.AddDays(DayCount - 1);

    public static CalendarWeek FromDate(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + DayCount) % DayCount;
        return new CalendarWeek(date.AddDays(-daysFromMonday));
    }

    public CalendarWeek Previous() => new(StartDate.AddDays(-DayCount));

    public CalendarWeek Next() => new(StartDate.AddDays(DayCount));

    public CalendarWeekView CreateView(DateOnly today)
    {
        var startDate = StartDate;
        var dates = Enumerable.Range(0, DayCount)
            .Select(index =>
            {
                var date = startDate.AddDays(index);
                return new CalendarWeekDate(date, date == today, GetWeekdayLabel(date.DayOfWeek));
            })
            .ToArray();

        return new CalendarWeekView(this, dates);
    }

    private static string GetWeekdayLabel(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        DayOfWeek.Sunday => "周日",
        _ => string.Empty
    };
}

public sealed class CalendarWeekView(
    CalendarWeek week,
    IReadOnlyList<CalendarWeekDate> dates)
{
    public CalendarWeek Week { get; } = week;

    public IReadOnlyList<CalendarWeekDate> Dates { get; } = dates;
}

public sealed record CalendarWeekDate(
    DateOnly Date,
    bool IsToday,
    string WeekdayLabel);
