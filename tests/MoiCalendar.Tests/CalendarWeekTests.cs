using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarWeekTests
{
    [Fact]
    public void FromDate_StartsOnMondayAndContainsSevenDays()
    {
        var today = new DateOnly(2026, 8, 26);

        var view = CalendarWeek.FromDate(today).CreateView(today);

        Assert.Equal(new DateOnly(2026, 8, 24), view.Week.StartDate);
        Assert.Equal(new DateOnly(2026, 8, 30), view.Week.EndDate);
        Assert.Equal(7, view.Dates.Count);
        Assert.Equal(DayOfWeek.Monday, view.Dates[0].Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, view.Dates[^1].Date.DayOfWeek);
        Assert.Single(view.Dates, date => date.IsToday && date.Date == today);
    }

    [Fact]
    public void PreviousAndNext_CrossMonthBoundary()
    {
        var week = CalendarWeek.FromDate(new DateOnly(2026, 9, 1));

        Assert.Equal(new DateOnly(2026, 8, 24), week.Previous().StartDate);
        Assert.Equal(new DateOnly(2026, 9, 7), week.Next().StartDate);
    }

    [Fact]
    public void Week_CanCrossYearBoundary()
    {
        var week = CalendarWeek.FromDate(new DateOnly(2026, 1, 1));

        Assert.Equal(new DateOnly(2025, 12, 29), week.StartDate);
        Assert.Equal(new DateOnly(2026, 1, 4), week.EndDate);
        Assert.Equal(new DateOnly(2026, 1, 5), week.Next().StartDate);
    }

    [Fact]
    public void Today_CreatesTheCurrentWeekAfterNavigation()
    {
        var today = new DateOnly(2026, 8, 26);
        var navigated = CalendarWeek.FromDate(today).Previous().Previous();

        var current = CalendarWeek.FromDate(today);

        Assert.NotEqual(navigated, current);
        Assert.Equal(new DateOnly(2026, 8, 24), current.StartDate);
    }
}
