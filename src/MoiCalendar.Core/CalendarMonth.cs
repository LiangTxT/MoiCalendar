namespace MoiCalendar.Core;

public readonly record struct CalendarMonth
{
    public CalendarMonth(int year, int month)
    {
        if (year is < 1 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year), "年份必须介于 1 和 9999 之间。");
        }

        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "月份必须介于 1 和 12 之间。");
        }

        Year = year;
        Month = month;
    }

    public int Year { get; }

    public int Month { get; }

    public static CalendarMonth FromDate(DateOnly date) => new(date.Year, date.Month);

    public CalendarMonth Previous() =>
        Month == 1 ? new CalendarMonth(Year - 1, 12) : new CalendarMonth(Year, Month - 1);

    public CalendarMonth Next() =>
        Month == 12 ? new CalendarMonth(Year + 1, 1) : new CalendarMonth(Year, Month + 1);

    public CalendarMonthView CreateView(DateOnly today)
    {
        var firstDayOfMonth = new DateOnly(Year, Month, 1);
        var daysFromMonday = ((int)firstDayOfMonth.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var gridStart = firstDayOfMonth.AddDays(-daysFromMonday);
        var dates = new CalendarDateCell[CalendarMonthView.DateCount];

        for (var index = 0; index < dates.Length; index++)
        {
            var date = gridStart.AddDays(index);
            dates[index] = new CalendarDateCell(
                date,
                date.Year == Year && date.Month == Month,
                date == today);
        }

        return new CalendarMonthView(this, Array.AsReadOnly(dates));
    }
}
