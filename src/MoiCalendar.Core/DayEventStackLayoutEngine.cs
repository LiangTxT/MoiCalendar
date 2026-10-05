namespace MoiCalendar.Core;

/// <summary>日视图的横向展示层；纵向坐标仍由共享时间网格生成。</summary>
public sealed record DayEventStackLayout(double LeftPercentage, double WidthPercentage, int Layer);

public static class DayEventStackLayoutEngine
{
    public static IReadOnlyDictionary<Guid, DayEventStackLayout> Layout(
        IReadOnlyList<TimedLayoutInput> events, TimeGridMetrics metrics)
    {
        var items = TimeGridLayoutEngine.Layout(events, metrics);
        var result = items.ToDictionary(item => item.SegmentId,
            item => new DayEventStackLayout(item.Overlap.LeftPercentage, item.Overlap.WidthPercentage, 1));
        var group = new List<TimeGridLayoutItem>();
        double groupEnd = 0;
        foreach (var item in items.OrderBy(item => item.Position.TopPercentage)
                     .ThenByDescending(item => item.Position.HeightPercentage).ThenBy(item => item.SegmentId))
        {
            if (group.Count > 0 && item.Position.TopPercentage >= groupEnd)
            {
                ApplyGroup();
                group.Clear();
            }
            group.Add(item);
            groupEnd = group.Max(value => value.Position.TopPercentage + value.Position.HeightPercentage);
        }
        ApplyGroup();
        return result;

        void ApplyGroup()
        {
            if (group.Count < 2) return;
            var backdrop = group[0];
            var end = backdrop.Position.TopPercentage + backdrop.Position.HeightPercentage;
            // 留出完整标题区；同起点、裁剪后同起点和链式重叠仍采用平等分栏。
            var titleClearance = TimeGridCoordinateConverter.DurationToPercentage(
                Math.Max(45, metrics.MinimumDisplayMinutes), metrics);
            if (group.Skip(1).Any(item =>
                    item.Position.TopPercentage - backdrop.Position.TopPercentage < titleClearance ||
                    item.Position.TopPercentage + item.Position.HeightPercentage > end)) return;
            result[backdrop.SegmentId] = new(0, 100, 1);
            var ids = group.Skip(1).Select(item => item.SegmentId).ToHashSet();
            var foreground = TimeGridLayoutEngine.Layout(events.Where(item => ids.Contains(item.SegmentId)).ToArray(), metrics);
            foreach (var item in foreground)
                result[item.SegmentId] = new(2 + item.Overlap.LeftPercentage * .98,
                    item.Overlap.WidthPercentage * .98, 2);
        }
    }
}
