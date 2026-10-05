using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

/// <summary>从连续月历已生成的布局取出隐藏事件，不重新计算排序或密度。</summary>
public static class MonthOverflowProjection
{
    public static IReadOnlyList<CalendarEventListItem> GetHiddenEvents(
        IEnumerable<CalendarMonthLayoutView> layouts, DateOnly date, bool compact)
    {
        ArgumentNullException.ThrowIfNull(layouts);
        // 同一个日期可出现在相邻月份的补齐格中；优先使用真正绘制该日期的所属月。
        var candidates = layouts.SelectMany(layout => layout.Weeks)
            .SelectMany(week => week.Days)
            .Where(day => day.Date.Date == date)
            .ToArray();
        var day = candidates.FirstOrDefault(day => day.Date.IsInActiveMonth)
            ?? candidates.FirstOrDefault();
        if (day is null) return Array.Empty<CalendarEventListItem>();

        var density = compact ? day.Compact : day.Regular;
        return day.AllEvents.Skip(density.VisibleEvents.Count).ToArray();
    }
}
