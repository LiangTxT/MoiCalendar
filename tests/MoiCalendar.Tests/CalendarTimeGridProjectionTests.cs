using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarTimeGridProjectionTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    [Fact]
    public void EmptyDay_ProducesNoTimedLayout()
    {
        var day = Day(Monday);

        var layout = CalendarTimeGridProjection.LayoutTimedEvents(day, TimeGridMetrics.FullDay);

        Assert.Empty(layout);
    }

    [Fact]
    public void NormalEvent_UsesConfiguredVisibleHourRange()
    {
        var calendarEvent = Timed(1, 9 * 60, 60);
        var day = Day(Monday, timedEvents: [calendarEvent]);
        var metrics = new TimeGridMetrics(8 * 60, 18 * 60);

        var item = CalendarTimeGridProjection.LayoutTimedEvents(day, metrics)[calendarEvent.Id];

        Assert.Equal(10, item.Position.TopPercentage, 6);
        Assert.Equal(10, item.Position.HeightPercentage, 6);
    }

    [Fact]
    public void OverlappingAndIdenticalEvents_ShareDeterministicColumns()
    {
        var first = Timed(1, 9 * 60, 120);
        var second = Timed(2, 9 * 60 + 30, 60);
        var third = Timed(3, 9 * 60, 120);
        var day = Day(Monday, timedEvents: [third, second, first]);

        var layout = CalendarTimeGridProjection.LayoutTimedEvents(day, TimeGridMetrics.FullDay);

        Assert.Equal(3, layout[first.Id].Overlap.ColumnCount);
        Assert.Equal(3, layout[second.Id].Overlap.ColumnCount);
        Assert.Equal(3, layout[third.Id].Overlap.ColumnCount);
        Assert.Equal(0, layout[first.Id].Overlap.Column);
        Assert.Equal(1, layout[third.Id].Overlap.Column);
        Assert.Equal(2, layout[second.Id].Overlap.Column);
    }

    [Fact]
    public void AllDayEvent_IsNotConvertedIntoTimedGeometry()
    {
        var allDay = new CalendarWeekAllDayEvent(StableId(1), "全天");
        var day = Day(Monday, allDayEvents: [allDay]);

        var layout = CalendarTimeGridProjection.LayoutTimedEvents(day, TimeGridMetrics.FullDay);

        Assert.Empty(layout);
        Assert.Same(allDay, Assert.Single(day.AllDayEvents));
    }

    [Fact]
    public void NowIndicator_UsesSameHourRangeAndHidesOutsideIt()
    {
        var metrics = new TimeGridMetrics(8 * 60, 18 * 60);
        var noon = new DateTime(2026, 9, 21, 13, 0, 0);

        var top = CurrentTimeIndicatorLayout.CalculateTopPercentage(Monday, noon, metrics);

        Assert.Equal(50, top);
        Assert.Null(CurrentTimeIndicatorLayout.CalculateTopPercentage(Monday, noon.AddDays(1), metrics));
        Assert.Null(CurrentTimeIndicatorLayout.CalculateTopPercentage(Monday, noon.Date.AddHours(18), metrics));
    }

    [Fact]
    public void WeekDayWeekTransition_UsesOneProjectionWithoutLosingWeekOrder()
    {
        var days = Enumerable.Range(0, 7).Select(offset => Day(Monday.AddDays(offset))).ToArray();

        var weekBefore = CalendarTimeGridProjection.SelectDays(days, visibleDate: null);
        var day = CalendarTimeGridProjection.SelectDays(days, Monday.AddDays(3));
        var weekAfter = CalendarTimeGridProjection.SelectDays(days, visibleDate: null);

        Assert.Equal(7, weekBefore.Count);
        Assert.Equal(Monday.AddDays(3), Assert.Single(day).Date.Date);
        Assert.Equal(weekBefore.Select(item => item.Date.Date), weekAfter.Select(item => item.Date.Date));
    }

    private static CalendarWeekDayEvents Day(
        DateOnly date,
        IReadOnlyList<CalendarWeekAllDayEvent>? allDayEvents = null,
        IReadOnlyList<CalendarWeekTimedEvent>? timedEvents = null) =>
        new(
            new CalendarWeekDate(date, false, "周一"),
            allDayEvents ?? Array.Empty<CalendarWeekAllDayEvent>(),
            timedEvents ?? Array.Empty<CalendarWeekTimedEvent>());

    private static CalendarWeekTimedEvent Timed(int id, int startMinute, int durationMinutes) =>
        new(
            StableId(id),
            $"事件 {id}",
            "",
            0,
            0,
            startMinute,
            durationMinutes);

    private static Guid StableId(int value)
    {
        var bytes = new byte[16];
        bytes[0] = (byte)value;
        return new Guid(bytes);
    }
}
