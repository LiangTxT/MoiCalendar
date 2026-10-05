using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class MonthOverflowProjectionTests
{
    private static readonly DateOnly Date = new(2026, 10, 20);

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public void AdjacentMonth_UsesItsOwnDensityAndOrdering(bool compact, int visibleCount)
    {
        var items = Enumerable.Range(1, 8).Select(Item).ToArray();
        var layouts = new[]
        {
            CalendarMonthEventView.Empty.CreateLayout(new CalendarMonth(2026, 9).CreateView(Date)),
            Layout(new CalendarMonth(2026, 10), items)
        };

        var hidden = MonthOverflowProjection.GetHiddenEvents(layouts, Date, compact);

        Assert.Equal(items.Skip(visibleCount), hidden);
        Assert.Equal(8 - visibleCount, hidden.Count);
    }

    [Fact]
    public void DuplicateAdjacentCell_PrefersDateOwningMonth()
    {
        var emptyPadding = Layout(new CalendarMonth(2026, 9), [], active: false);
        var owner = Layout(new CalendarMonth(2026, 10), Enumerable.Range(1, 5).Select(Item).ToArray());

        var hidden = MonthOverflowProjection.GetHiddenEvents([emptyPadding, owner], Date, false);

        Assert.Equal([Item(4), Item(5)], hidden);
    }

    [Fact]
    public void MissingDateAndHiddenCalendar_ReturnEmpty()
    {
        var layout = Layout(new CalendarMonth(2026, 10));
        Assert.Empty(MonthOverflowProjection.GetHiddenEvents([layout], Date, false));
        Assert.Empty(MonthOverflowProjection.GetHiddenEvents([layout], Date.AddDays(1), true));
        Assert.Empty(MonthOverflowProjection.GetHiddenEvents([], Date, false));
    }

    [Fact]
    public void PaddingOnly_FallsBackAndPreservesMultiDayIdentityAndColor()
    {
        var continuation = Item(4) with { IsAllDay = true, SegmentPosition = CalendarEventSegmentPosition.Middle, CalendarColorIndex = 6 };
        var layout = Layout(new CalendarMonth(2026, 9), [Item(1), Item(2), Item(3), continuation], active: false);

        var hidden = MonthOverflowProjection.GetHiddenEvents([layout], Date, false);

        Assert.Same(continuation, Assert.Single(hidden));
    }

    [Fact]
    public void VisibleOnly_ReturnsNoOverflowWithoutMutatingInput()
    {
        var items = new[] { Item(1), Item(2) };
        var layout = Layout(new CalendarMonth(2026, 10), items);
        Assert.Empty(MonthOverflowProjection.GetHiddenEvents([layout], Date, false));
        Assert.Equal(items, layout.Weeks[0].Days[0].AllEvents);
    }

    private static CalendarMonthLayoutView Layout(CalendarMonth month, CalendarEventListItem[]? items = null, bool active = true)
    {
        items ??= [];
        var day = new CalendarMonthDayLayout(new CalendarDateCell(Date, active, false),
            MonthLayoutEngine.LayoutDay(items, 3), MonthLayoutEngine.LayoutDay(items, 2), items);
        return new CalendarMonthLayoutView(month.CreateView(Date), [new CalendarMonthWeekLayout([day])]);
    }

    private static CalendarEventListItem Item(int index) => new(new Guid(index, 0, 0, new byte[8]),
        $"事件 {index}", "09:00", false, TimeSpan.FromHours(9));
}
