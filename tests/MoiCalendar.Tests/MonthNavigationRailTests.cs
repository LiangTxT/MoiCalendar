using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class MonthNavigationRailTests
{
    [Theory]
    [InlineData(2026, 1)]
    [InlineData(2026, 10)]
    [InlineData(2027, 12)]
    public async Task Rail_AlwaysHasTwelveStopsAndExactlyOneCurrentMonth(int year, int month)
    {
        var html = WebUtility.HtmlDecode(await RenderAsync(new CalendarMonth(year, month)));
        Assert.Equal(12, Regex.Matches(html, "<button\\b").Count);
        Assert.Single(Regex.Matches(html, "aria-current=\"date\""));
        Assert.Contains($"{year}年月份导航", html);
        for (var number = 1; number <= 12; number++)
            Assert.Contains($"跳转到{year}年{number}月", html);
        Assert.Matches($"aria-label=\"跳转到{year}年{month}月\"[^>]*aria-current=\"date\"", html);
    }

    [Fact]
    public async Task Rail_ForNewYearDoesNotRetainPreviousYearLabels()
    {
        var html = WebUtility.HtmlDecode(await RenderAsync(new CalendarMonth(2027, 1)));
        Assert.DoesNotContain("2026年", html);
        Assert.Contains("跳转到2027年12月", html);
    }

    private static async Task<string> RenderAsync(CalendarMonth month)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<MonthNavigationRail>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(MonthNavigationRail.DisplayedMonth)] = month
                }));
            return component.ToHtmlString();
        });
    }
}
