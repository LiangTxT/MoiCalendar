using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class DayNavigationRenderingTests
{
    [Fact]
    public async Task DayStrip_KeepsMondayFirst_CrossMonthDates_LunarAndSelectedState()
    {
        var selected = new DateOnly(2026, 9, 30);
        var days = CalendarWeek.FromDate(selected).CreateView(selected).Dates
            .Select(date => new CalendarWeekDayEvents(date, [], [])).ToArray();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<ICalendarObservanceProvider, ChineseCalendarObservanceProvider>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TimeGridHeader>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(TimeGridHeader.Days)] = days,
                    [nameof(TimeGridHeader.IsDayNavigation)] = true,
                    [nameof(TimeGridHeader.SelectedDate)] = selected,
                    [nameof(TimeGridHeader.Today)] = selected
                }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
        Assert.Equal(DayOfWeek.Monday, days[0].Date.Date.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, days[^1].Date.Date.DayOfWeek);
        Assert.Contains("day-date-navigation", html);
        Assert.Contains("day-lunar-label", html);
        Assert.Contains("选择 2026-10-04", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Contains("adjacent-month", html);
        Assert.True(html.IndexOf("周一", StringComparison.Ordinal) < html.IndexOf("周日", StringComparison.Ordinal));
    }
}
