using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class MonthBoundaryRenderingTests
{
    [Theory]
    [InlineData(2027, 2, true, 5)]
    [InlineData(2026, 5, true, 6)]
    [InlineData(2028, 12, true, 6)]
    [InlineData(2026, 9, false, 5)]
    [InlineData(2026, 10, false, 5)]
    [InlineData(2028, 2, false, 5)]
    public async Task OnlyWeekAlignedMonthEnds_AddOneEmptyWeek(int year, int month, bool hasGap, int rows)
    {
        var view = new CalendarMonth(year, month).CreateView(new DateOnly(year, month, 1));
        var layout = CalendarMonthEventView.Empty.CreateLayout(view);
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<ICalendarObservanceProvider, EmptyCalendarObservanceProvider>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<MonthPanel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(MonthPanel.Layout)] = layout }));
            return component.ToHtmlString();
        });

        Assert.Equal(hasGap, html.Contains("month-boundary-week"));
        Assert.Contains($"aria-rowcount=\"{rows}\"", html);
        Assert.Contains($"--month-week-count: {rows}", html);
        if (hasGap)
        {
            var boundary = html[html.IndexOf("month-boundary-week", StringComparison.Ordinal)..];
            Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(boundary, "role=\"gridcell\"").Count);
            Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(boundary, "class=\"calendar-date month-blank-date\"").Count);
            Assert.DoesNotContain("data-date", boundary);
            Assert.DoesNotContain("tabindex", boundary);
        }
    }
}
