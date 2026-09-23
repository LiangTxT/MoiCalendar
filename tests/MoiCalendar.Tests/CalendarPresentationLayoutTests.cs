using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarPresentationLayoutTests
{
    [Fact]
    public void MonthDayLayout_KeepsCellHeightStableAndReportsOverflow()
    {
        var events = Enumerable.Range(1, 5)
            .Select(index => CreateTimedEvent(index, index * 60, 30))
            .Select(item => new CalendarEventListItem(
                item.Id,
                item.Title,
                item.TimeLabel,
                false,
                TimeSpan.FromMinutes(item.StartMinute)))
            .ToArray();

        var layout = CalendarPresentationLayout.GetMonthDayLayout(events, 3);

        Assert.Equal(3, layout.VisibleEvents.Count);
        Assert.Equal(2, layout.OverflowCount);
        Assert.Equal(events.Take(3), layout.VisibleEvents);
    }

    [Fact]
    public void OverlapLayout_AssignsDeterministicVisibleColumns()
    {
        var first = CreateTimedEvent(1, 10 * 60, 90);
        var second = CreateTimedEvent(2, 10 * 60 + 30, 60);
        var third = CreateTimedEvent(3, 10 * 60 + 45, 30);

        var layout = CalendarPresentationLayout.LayoutOverlappingEvents([third, first, second]);

        Assert.Equal(new TimedEventLayout(0, 3), layout[first.Id]);
        Assert.Equal(new TimedEventLayout(1, 3), layout[second.Id]);
        Assert.Equal(new TimedEventLayout(2, 3), layout[third.Id]);
    }

    [Fact]
    public void OverlapLayout_StartsANewGroupWhenPreviousEventsHaveEnded()
    {
        var first = CreateTimedEvent(1, 9 * 60, 60);
        var second = CreateTimedEvent(2, 11 * 60, 60);

        var layout = CalendarPresentationLayout.LayoutOverlappingEvents([second, first]);

        Assert.Equal(new TimedEventLayout(0, 1), layout[first.Id]);
        Assert.Equal(new TimedEventLayout(0, 1), layout[second.Id]);
    }

    [Fact]
    public void OverlapLayout_AccountsForMinimumVisualHeight()
    {
        var first = CreateTimedEvent(1, 10 * 60, 5);
        var second = CreateTimedEvent(2, 10 * 60 + 10, 5);

        var layout = CalendarPresentationLayout.LayoutOverlappingEvents([first, second]);

        Assert.Equal(new TimedEventLayout(0, 2), layout[first.Id]);
        Assert.Equal(new TimedEventLayout(1, 2), layout[second.Id]);
    }

    [Fact]
    public void TimedPosition_UsesStartTimeAndMinimumReadableHeight()
    {
        var position = CalendarPresentationLayout.CalculateTimedPosition(6 * 60, 5);

        Assert.Equal(25d, position.TopPercentage, 6);
        Assert.Equal(30d * 100d / (24 * 60), position.HeightPercentage, 6);
    }

    [Theory]
    [InlineData(10, CalendarEventSegmentPosition.Start)]
    [InlineData(11, CalendarEventSegmentPosition.Middle)]
    [InlineData(12, CalendarEventSegmentPosition.End)]
    public void SegmentPosition_DescribesMultiDayContinuation(
        int day,
        CalendarEventSegmentPosition expected)
    {
        var position = CalendarPresentationLayout.GetSegmentPosition(
            new DateOnly(2026, 9, day),
            new DateOnly(2026, 9, 10),
            new DateOnly(2026, 9, 12));

        Assert.Equal(expected, position);
    }

    [Fact]
    public void CurrentDateDetection_UsesTheSuppliedLocalToday()
    {
        var today = new DateOnly(2026, 9, 11);

        Assert.True(CalendarPresentationLayout.IsCurrentDate(today, today));
        Assert.False(CalendarPresentationLayout.IsCurrentDate(today.AddDays(-1), today));
    }

    private static CalendarWeekTimedEvent CreateTimedEvent(
        int index,
        int startMinute,
        int durationMinutes)
    {
        var idBytes = new byte[16];
        idBytes[0] = (byte)index;
        var position = CalendarPresentationLayout.CalculateTimedPosition(startMinute, durationMinutes);
        return new CalendarWeekTimedEvent(
            new Guid(idBytes),
            $"事件 {index}",
            "10:00–11:00",
            position.TopPercentage,
            position.HeightPercentage,
            startMinute,
            durationMinutes);
    }
}
