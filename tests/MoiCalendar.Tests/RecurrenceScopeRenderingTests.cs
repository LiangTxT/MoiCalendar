using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class RecurrenceScopeRenderingTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("本地保存失败，请重试。", true)]
    public async Task Scope_OnlyRendersActualErrors(string? error, bool hasAlert)
    {
        var html = await RenderAsync(error, false);
        Assert.Equal(hasAlert, html.Contains("role=\"alert\""));
        Assert.DoesNotContain("detailError", html);
        Assert.Contains("删除整个系列", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task BusyScope_DisablesAllActions()
    {
        var html = await RenderAsync(null, true);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "disabled").Count);
        Assert.Contains("正在删除", System.Net.WebUtility.HtmlDecode(html));
    }

    [Fact]
    public void Home_BindsActualErrorAndBusyState()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MoiCalendar.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var home = File.ReadAllText(Path.Combine(directory.FullName, "src", "MoiCalendar.App", "Pages", "Home.razor"));
        Assert.DoesNotContain("Error=\"detailError\"", home);
        Assert.Contains("Busy=\"isDeletingFromDetails\"", home);
        Assert.Contains("if (isDeletingFromDetails || overlayState is not RecurrenceScopeOverlayState scope)", home);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownOccurrence_ShowsBothDeletionScopes(bool busy)
    {
        var html = await RenderAsync(null, busy, DateTimeOffset.UtcNow);
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("仅删除这一次", decoded);
        if (!busy) Assert.Contains("删除整个系列", decoded);
        Assert.Equal(busy ? 4 : 0, System.Text.RegularExpressions.Regex.Matches(html, "disabled").Count);
    }

    private static async Task<string> RenderAsync(string? error, bool busy, DateTimeOffset? occurrenceStartUtc = null)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<RecurrenceScopeChooser>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RecurrenceScopeChooser.CalendarEvent)] = new CalendarEvent { Id = Guid.NewGuid(), Title = "重复事件测试", RecurrenceRule = "FREQ=DAILY", UpdatedAtUtc = DateTimeOffset.UtcNow, TimeZoneId = TimeZoneInfo.Utc.Id, Description = "", Location = "", StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), CreatedAtUtc = DateTimeOffset.UtcNow, IsAllDay = false },
                [nameof(RecurrenceScopeChooser.Action)] = CalendarRecurrenceAction.DeleteSeries,
                [nameof(RecurrenceScopeChooser.Error)] = error,
                [nameof(RecurrenceScopeChooser.Busy)] = busy,
                [nameof(RecurrenceScopeChooser.OccurrenceStartUtc)] = occurrenceStartUtc
            }));
            return component.ToHtmlString();
        });
    }
}
