using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class TimelineViewCacheTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static CalendarWeekView View(int offset = 0) =>
        CalendarWeek.FromDate(Today.AddDays(offset * 7)).CreateView(Today);

    [Fact]
    public async Task SevenDaySwitchesAndBacktracking_LoadEachWeekOnlyOnce()
    {
        var cache = new TimelineViewCache();
        var reads = 0;
        Task<CalendarWeekEventView> Load(CalendarWeekView view, string zone)
        {
            reads++;
            return Task.FromResult(CalendarWeekEventView.Empty);
        }
        for (var day = 0; day < 7; day++) await cache.GetAsync(View(), "UTC", Load);
        Assert.Equal(1, reads); // 之前每天调用服务，七次导航读七次。
        await cache.GetAsync(View(1), "UTC", Load);
        await cache.GetAsync(View(), "UTC", Load);
        Assert.Equal(2, reads);
        cache.Clear();
        await cache.GetAsync(View(), "UTC", Load);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task ConcurrentLoadsShareRequest_AndInvalidationDoesNotReuseOldRequest()
    {
        var cache = new TimelineViewCache();
        var old = new TaskCompletionSource<CalendarWeekEventView>();
        var pending = cache.GetAsync(View(), "UTC", (_, _) => old.Task);
        Assert.Same(pending, cache.GetAsync(View(), "UTC", (_, _) => throw new Exception("重复加载")));
        cache.Clear();
        var fresh = cache.GetAsync(View(), "UTC", (_, _) => Task.FromResult(CalendarWeekEventView.Empty));
        old.SetException(new InvalidOperationException("旧请求失败"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Same(fresh, cache.GetAsync(View(), "UTC", (_, _) => throw new Exception("误清除新缓存")));
    }

    [Fact]
    public async Task TrimKeepsFiveWeeks_AndDifferentTimeZonesDoNotShareResults()
    {
        var cache = new TimelineViewCache();
        var reads = 0;
        Task<CalendarWeekEventView> Load(CalendarWeekView view, string zone)
        {
            reads++;
            return Task.FromResult(CalendarWeekEventView.Empty);
        }
        for (var i = -3; i <= 3; i++) await cache.GetAsync(View(i), "UTC", Load);
        cache.Trim(View().Week, "UTC");
        for (var i = -2; i <= 2; i++) await cache.GetAsync(View(i), "UTC", Load);
        Assert.Equal(7, reads);
        await cache.GetAsync(View(-3), "UTC", Load);
        await cache.GetAsync(View(), "Asia/Shanghai", Load);
        Assert.Equal(9, reads);
    }
}
