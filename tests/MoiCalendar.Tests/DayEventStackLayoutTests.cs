using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class DayEventStackLayoutTests
{
    private static TimedLayoutInput Event(int id, int start, int end) =>
        new(new Guid(id, 0, 0, new byte[8]), start, end);

    [Fact]
    public void ReferenceNestedEvents_LongEventIsBackdrop_ShortEventsShareForeground()
    {
        var events = new[] { Event(1, 645, 780), Event(2, 690, 750), Event(3, 720, 750) };
        var layout = DayEventStackLayoutEngine.Layout(events, TimeGridMetrics.FullDay);
        Assert.Equal(new DayEventStackLayout(0, 100, 1), layout[events[0].SegmentId]);
        Assert.Equal(new DayEventStackLayout(2, 49, 2), layout[events[1].SegmentId]);
        Assert.Equal(new DayEventStackLayout(51, 49, 2), layout[events[2].SegmentId]);
        Assert.Equal(layout.OrderBy(item => item.Key),
            DayEventStackLayoutEngine.Layout(events.Reverse().ToArray(), TimeGridMetrics.FullDay).OrderBy(item => item.Key));
    }

    [Theory]
    [InlineData(540, 660, 540, 600)] // 同起点，不能覆盖标题
    [InlineData(540, 600, 570, 660)] // 链式重叠，没有共同底层
    [InlineData(540, 660, 555, 600)] // 标题空间不足
    public void UnsafeBackdrop_UsesSharedEqualColumns(int start, int end, int otherStart, int otherEnd)
    {
        var events = new[] { Event(1, start, end), Event(2, otherStart, otherEnd) };
        var layout = DayEventStackLayoutEngine.Layout(events, TimeGridMetrics.FullDay);
        Assert.All(layout.Values, item => { Assert.Equal(50, item.WidthPercentage); Assert.Equal(1, item.Layer); });
    }

    [Fact]
    public void ClippedStarts_DoNotHideTitles_AndOutsideEventsAreExcluded()
    {
        var events = new[] { Event(1, 420, 660), Event(2, 450, 600), Event(3, 100, 200) };
        var layout = DayEventStackLayoutEngine.Layout(events, new TimeGridMetrics(480, 1080));
        Assert.Equal(2, layout.Count);
        Assert.All(layout.Values, item => Assert.Equal(50, item.WidthPercentage));
    }

    [Fact]
    public void NonOverlappingAndEmpty_KeepFullWidth()
    {
        var events = new[] { Event(1, 540, 600), Event(2, 600, 660) };
        Assert.All(DayEventStackLayoutEngine.Layout(events, TimeGridMetrics.FullDay).Values,
            item => Assert.Equal(new DayEventStackLayout(0, 100, 1), item));
        Assert.Empty(DayEventStackLayoutEngine.Layout([], TimeGridMetrics.FullDay));
    }
}
