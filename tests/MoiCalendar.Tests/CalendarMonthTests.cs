using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarMonthTests
{
    [Fact]
    public void FromDate_UsesYearAndMonth()
    {
        var month = CalendarMonth.FromDate(new DateOnly(2026, 8, 23));

        Assert.Equal(new CalendarMonth(2026, 8), month);
    }

    [Fact]
    public void Previous_FromJanuary_ReturnsDecemberOfPreviousYear()
    {
        var month = new CalendarMonth(2026, 1).Previous();

        Assert.Equal(new CalendarMonth(2025, 12), month);
    }

    [Fact]
    public void Next_FromDecember_ReturnsJanuaryOfNextYear()
    {
        var month = new CalendarMonth(2026, 12).Next();

        Assert.Equal(new CalendarMonth(2027, 1), month);
    }
}
