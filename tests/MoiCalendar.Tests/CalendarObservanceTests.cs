using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarObservanceTests
{
    private readonly ChineseCalendarObservanceProvider provider = new();

    [Fact]
    public void October2026_IncludesNationalDayAndColdDewAsReadOnlyCalendarSources()
    {
        var observances = provider.GetObservances(
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 11, 1));

        Assert.Contains(observances, item =>
            item.Date == new DateOnly(2026, 10, 1) &&
            item.Title == "国庆节" &&
            item.Kind == CalendarObservanceKind.Holiday);
        Assert.Contains(observances, item =>
            item.Date == new DateOnly(2026, 10, 8) &&
            item.Title == "寒露" &&
            item.Kind == CalendarObservanceKind.SolarTerm);
    }

    [Fact]
    public void LunarFestivalAndLunarDay_AreCalculatedLocally()
    {
        var observances = provider.GetObservances(
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 10, 1));

        Assert.Contains(observances, item =>
            item.Date == new DateOnly(2026, 9, 25) && item.Title == "中秋节");
        Assert.Equal("十九", provider.GetLunarLabel(new DateOnly(2026, 9, 29)));
    }

    [Fact]
    public void ObservanceIdentity_IsStableAcrossQueries()
    {
        var start = new DateOnly(2026, 10, 1);
        var first = provider.GetObservances(start, start.AddDays(1)).Single();
        var second = provider.GetObservances(start, start.AddDays(2))
            .Single(item => item.Title == first.Title);

        Assert.Equal(first.Id, second.Id);
    }
}
