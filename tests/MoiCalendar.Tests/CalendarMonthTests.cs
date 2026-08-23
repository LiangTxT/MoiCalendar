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

    [Fact]
    public void CreateView_LeapYearFebruary_ContainsTwentyNineActiveDates()
    {
        var today = new DateOnly(2024, 2, 29);

        var view = new CalendarMonth(2024, 2).CreateView(today);

        Assert.Equal(42, view.Dates.Count);
        Assert.Equal(29, view.Dates.Count(date => date.IsInActiveMonth));
        Assert.Contains(view.Dates, date => date.Date == today && date.IsToday);
    }

    [Fact]
    public void CreateView_NormalFebruary_ContainsTwentyEightActiveDates()
    {
        var view = new CalendarMonth(2023, 2).CreateView(new DateOnly(2023, 2, 10));

        Assert.Equal(42, view.Dates.Count);
        Assert.Equal(28, view.Dates.Count(date => date.IsInActiveMonth));
        Assert.DoesNotContain(view.Dates, date => date.IsInActiveMonth && date.DayNumber == 29);
    }

    [Theory]
    [InlineData(2026, 4, 30)]
    [InlineData(2026, 7, 31)]
    public void CreateView_UsesTheCorrectNumberOfDaysForTheMonth(int year, int month, int expectedDays)
    {
        var view = new CalendarMonth(year, month).CreateView(new DateOnly(year, month, 1));

        Assert.Equal(expectedDays, view.Dates.Count(date => date.IsInActiveMonth));
    }

    [Fact]
    public void CreateView_MonthStartingMonday_BeginsWithFirstDayOfMonth()
    {
        var view = new CalendarMonth(2024, 1).CreateView(new DateOnly(2024, 1, 15));

        Assert.Equal(new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }, view.WeekdayLabels);
        Assert.Equal(new DateOnly(2024, 1, 1), view.Dates[0].Date);
        Assert.True(view.Dates[0].IsInActiveMonth);
        Assert.Equal(new DateOnly(2024, 2, 11), view.Dates[^1].Date);
    }

    [Fact]
    public void CreateView_MonthStartingSunday_PutsSundayInTheSeventhColumn()
    {
        var view = new CalendarMonth(2024, 9).CreateView(new DateOnly(2024, 9, 15));

        Assert.Equal(new DateOnly(2024, 8, 26), view.Dates[0].Date);
        Assert.False(view.Dates[0].IsInActiveMonth);
        Assert.Equal(new DateOnly(2024, 9, 1), view.Dates[6].Date);
        Assert.True(view.Dates[6].IsInActiveMonth);
        Assert.Equal(new DateOnly(2024, 10, 6), view.Dates[^1].Date);
    }
}
