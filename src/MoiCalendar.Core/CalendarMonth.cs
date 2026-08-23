namespace MoiCalendar.Core;

public readonly record struct CalendarMonth
{
    public CalendarMonth(int year, int month)
    {
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
}
