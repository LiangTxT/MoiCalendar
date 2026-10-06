using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

/// <summary>页面内的短期展示缓存；保存、删除或重新加载后失效，不替代本地仓储。</summary>
public sealed class TimelineViewCache
{
    private readonly Dictionary<(CalendarWeek Week, string Zone), Task<CalendarWeekEventView>> entries = new();

    public void Clear() => entries.Clear();

    public Task<CalendarWeekEventView> GetAsync(CalendarWeekView view, string zone,
        Func<CalendarWeekView, string, Task<CalendarWeekEventView>> load)
    {
        var key = (view.Week, zone);
        if (entries.TryGetValue(key, out var cached)) return cached;
        var completion = new TaskCompletionSource<CalendarWeekEventView>();
        entries[key] = completion.Task;
        _ = LoadAsync();
        return completion.Task;

        async Task LoadAsync()
        {
            try { completion.SetResult(await load(view, zone)); }
            catch (Exception error)
            {
                // 失败可以重试；失效前的旧请求不能清除新一代请求。
                if (entries.TryGetValue(key, out var current) && ReferenceEquals(current, completion.Task))
                    entries.Remove(key);
                completion.SetException(error);
            }
        }
    }

    public void Trim(CalendarWeek center, string zone)
    {
        foreach (var key in entries.Keys.Where(key => key.Zone != zone
            || Math.Abs(key.Week.StartDate.DayNumber - center.StartDate.DayNumber) > 14).ToArray())
            entries.Remove(key);
    }
}
