using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarLayoutEngineTests
{
    private static readonly CalendarVisibleRange FixedWeek = new(
        new DateOnly(2026, 9, 7),
        new DateOnly(2026, 9, 14));

    [Fact]
    public void VisibleRange_MonthUsesSixMondayFirstRows()
    {
        var range = VisibleRangeLayoutEngine.Calculate(
            CalendarViewMode.Month,
            new DateOnly(2026, 9, 23));

        Assert.Equal(new DateOnly(2026, 8, 31), range.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 12), range.EndDateExclusive);
        Assert.Equal(42, range.DayCount);
    }

    [Fact]
    public void VisibleRange_WeekAndDayAreDeterministic()
    {
        var anchor = new DateOnly(2026, 9, 10);

        var week = VisibleRangeLayoutEngine.Calculate(CalendarViewMode.Week, anchor);
        var day = VisibleRangeLayoutEngine.Calculate(CalendarViewMode.Day, anchor);

        Assert.Equal(FixedWeek, week);
        Assert.Equal(new CalendarVisibleRange(anchor, anchor.AddDays(1)), day);
    }

    [Fact]
    public void TimedLayout_NoOverlapUsesFullWidth()
    {
        var first = Timed(1, 9 * 60, 10 * 60);
        var second = Timed(2, 10 * 60, 11 * 60);

        var layout = OverlapLayoutEngine.Layout([second, first]);

        Assert.Equal(new TimedEventLayout(0, 1), layout[first.SegmentId]);
        Assert.Equal(new TimedEventLayout(0, 1), layout[second.SegmentId]);
        Assert.Equal(0, layout[first.SegmentId].LeftPercentage);
        Assert.Equal(100, layout[first.SegmentId].WidthPercentage);
    }

    [Fact]
    public void TimedLayout_TwoOverlapsSplitIntoTwoColumns()
    {
        var first = Timed(1, 9 * 60, 10 * 60);
        var second = Timed(2, 9 * 60 + 30, 10 * 60 + 30);

        var layout = OverlapLayoutEngine.Layout([second, first]);

        Assert.Equal(new TimedEventLayout(0, 2), layout[first.SegmentId]);
        Assert.Equal(new TimedEventLayout(1, 2), layout[second.SegmentId]);
        Assert.Equal(50, layout[first.SegmentId].WidthPercentage);
        Assert.Equal(50, layout[second.SegmentId].LeftPercentage);
    }

    [Fact]
    public void TimedLayout_ThreeNestedEventsUseThreeColumns()
    {
        var outer = Timed(1, 9 * 60, 12 * 60);
        var middle = Timed(2, 9 * 60 + 30, 11 * 60);
        var inner = Timed(3, 10 * 60, 10 * 60 + 30);

        var layout = OverlapLayoutEngine.Layout([inner, middle, outer]);

        Assert.Equal(new TimedEventLayout(0, 3), layout[outer.SegmentId]);
        Assert.Equal(new TimedEventLayout(1, 3), layout[middle.SegmentId]);
        Assert.Equal(new TimedEventLayout(2, 3), layout[inner.SegmentId]);
    }

    [Fact]
    public void TimedLayout_ChainedOverlapsStayInOneCollisionGroup()
    {
        var first = Timed(1, 9 * 60, 10 * 60);
        var bridge = Timed(2, 9 * 60 + 30, 10 * 60 + 30);
        var last = Timed(3, 10 * 60, 11 * 60);

        var layout = OverlapLayoutEngine.Layout([last, bridge, first]);

        Assert.Equal(new TimedEventLayout(0, 2), layout[first.SegmentId]);
        Assert.Equal(new TimedEventLayout(1, 2), layout[bridge.SegmentId]);
        Assert.Equal(new TimedEventLayout(0, 2), layout[last.SegmentId]);
    }

    [Fact]
    public void TimedLayout_IdenticalTimesAreOrderedByStableId()
    {
        var first = Timed(1, 9 * 60, 10 * 60);
        var second = Timed(2, 9 * 60, 10 * 60);

        var forward = OverlapLayoutEngine.Layout([first, second]);
        var reversed = OverlapLayoutEngine.Layout([second, first]);

        Assert.Equal(forward, reversed);
        Assert.Equal(0, forward[first.SegmentId].Column);
        Assert.Equal(1, forward[second.SegmentId].Column);
    }

    [Fact]
    public void MultiDaySegmentation_SplitsCrossMidnightWithoutDuplicatingEventIdentity()
    {
        var calendarEvent = RenderEvent(
            1,
            new DateTime(2026, 9, 8, 23, 30, 0),
            new DateTime(2026, 9, 9, 0, 30, 0));

        var segments = MultiDayLayoutEngine.Segment([calendarEvent], FixedWeek);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, segment => Assert.Equal(calendarEvent.Id, segment.EventId));
        Assert.Equal((23 * 60 + 30, 24 * 60), (segments[0].StartMinute, segments[0].EndMinute));
        Assert.Equal((0, 30), (segments[1].StartMinute, segments[1].EndMinute));
        Assert.True(segments[0].ContinuesToNextDay);
        Assert.True(segments[1].ContinuesFromPreviousDay);
    }

    [Fact]
    public void MultiDaySegmentation_ClipsToVisibleRangeAndRetainsContinuationFlags()
    {
        var calendarEvent = RenderEvent(
            1,
            new DateTime(2026, 9, 5, 12, 0, 0),
            new DateTime(2026, 9, 15, 12, 0, 0));

        var segments = MultiDayLayoutEngine.Segment([calendarEvent], FixedWeek);

        Assert.Equal(7, segments.Count);
        Assert.Equal(FixedWeek.StartDate, segments[0].Date);
        Assert.Equal(FixedWeek.EndDateExclusive.AddDays(-1), segments[^1].Date);
        Assert.All(segments, segment => Assert.True(segment.StartsBeforeVisibleRange));
        Assert.All(segments, segment => Assert.True(segment.EndsAfterVisibleRange));
        Assert.All(segments, segment => Assert.True(segment.ContinuesFromPreviousDay));
        Assert.All(segments, segment => Assert.True(segment.ContinuesToNextDay));
    }

    [Fact]
    public void AllDayLayout_AllocatesReusableRowsAndKeepsExclusiveEndDate()
    {
        var longEvent = RenderEvent(
            1,
            new DateTime(2026, 9, 7),
            new DateTime(2026, 9, 10),
            isAllDay: true);
        var overlapping = RenderEvent(
            2,
            new DateTime(2026, 9, 8),
            new DateTime(2026, 9, 9),
            isAllDay: true);
        var after = RenderEvent(
            3,
            new DateTime(2026, 9, 10),
            new DateTime(2026, 9, 11),
            isAllDay: true);

        var layout = AllDayLayoutEngine.Layout([overlapping, after, longEvent], FixedWeek);

        Assert.Equal(0, layout.Single(item => item.EventId == longEvent.Id).Row);
        Assert.Equal(1, layout.Single(item => item.EventId == overlapping.Id).Row);
        Assert.Equal(0, layout.Single(item => item.EventId == after.Id).Row);
        Assert.Equal(
            new DateOnly(2026, 9, 9),
            layout.Single(item => item.EventId == longEvent.Id).EndDateInclusive);
    }

    [Fact]
    public void AllDayLayout_SplitsAnEventAtMonthGridRowBoundaries()
    {
        var range = new CalendarVisibleRange(
            new DateOnly(2026, 8, 31),
            new DateOnly(2026, 9, 14));
        var calendarEvent = RenderEvent(
            1,
            new DateTime(2026, 9, 5),
            new DateTime(2026, 9, 9),
            isAllDay: true);

        var layout = AllDayLayoutEngine.Layout([calendarEvent], range);

        Assert.Equal(2, layout.Count);
        Assert.Equal(new DateOnly(2026, 9, 6), layout[0].EndDateInclusive);
        Assert.Equal(new DateOnly(2026, 9, 7), layout[1].StartDate);
        Assert.True(layout[0].ContinuesToNextDay);
        Assert.True(layout[1].ContinuesFromPreviousDay);
    }

    [Fact]
    public void MonthLayout_CalculatesOverflowOnceForEachDayCell()
    {
        var events = Enumerable.Range(1, 5)
            .Select(index => RenderEvent(
                index,
                new DateTime(2026, 9, 8, 8 + index, 0, 0),
                new DateTime(2026, 9, 8, 9 + index, 0, 0)))
            .ToArray();

        var layout = MonthLayoutEngine.Layout(events, FixedWeek, availableEventRows: 3)
            .GetDay(new DateOnly(2026, 9, 8));

        Assert.Equal(3, layout.VisibleEvents.Count);
        Assert.Equal(2, layout.OverflowCount);
    }

    [Fact]
    public void TimeGrid_ClipsToVisibleHoursAndUsesCanonicalRoundTripConversion()
    {
        var metrics = new TimeGridMetrics(8 * 60, 18 * 60, minimumDisplayMinutes: 15);
        var input = Timed(1, 7 * 60, 9 * 60);

        var item = Assert.Single(TimeGridLayoutEngine.Layout([input], metrics));

        Assert.Equal(0, item.Position.TopPercentage);
        Assert.Equal(10, item.Position.HeightPercentage);
        var minute = TimeGridCoordinateConverter.PercentageToMinute(50, metrics);
        Assert.Equal(13 * 60, minute);
        Assert.Equal(50, TimeGridCoordinateConverter.MinuteToPercentage(minute, metrics));
    }

    private static TimedLayoutInput Timed(int id, int startMinute, int endMinute) =>
        new(StableId(id), startMinute, endMinute);

    private static CalendarRenderEvent RenderEvent(
        int id,
        DateTime start,
        DateTime end,
        bool isAllDay = false) =>
        new(StableId(id), $"事件 {id}", start, end, isAllDay);

    private static Guid StableId(int value)
    {
        var bytes = new byte[16];
        bytes[0] = (byte)value;
        return new Guid(bytes);
    }
}
