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
        Assert.Contains("private async Task OpenEventEditorAsync(CalendarOccurrenceReference occurrence)", home);
        Assert.Contains("occurrenceStartUtc: occurrence.StartUtc", home);
        Assert.Contains("occurrenceStartUtc: selectedOccurrence?.StartUtc", home);
        Assert.Contains("EventService.UpdateOccurrenceAsync(occurrence.Id, start, editor)", home);
        Assert.Contains("if (editor is null || isSavingEvent)", home);
        Assert.Contains("if (isSavingEvent) return;", home);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditScope_OffersSingleEditOnlyForKnownOccurrence(bool known)
    {
        var html = System.Net.WebUtility.HtmlDecode(await RenderAsync(null, false,
            known ? new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero) : null, CalendarRecurrenceAction.EditSeries));
        Assert.Equal(known, html.Contains("仅编辑这一次"));
        Assert.Contains("编辑整个系列", html);
        Assert.Contains("aria-describedby=\"recurrence-scope-description\"", html);
        Assert.DoesNotContain("仅删除这一次", html);
    }

    private static async Task<string> RenderAsync(string? error, bool busy, DateTimeOffset? occurrenceStartUtc = null,
        CalendarRecurrenceAction action = CalendarRecurrenceAction.DeleteSeries)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<RecurrenceScopeChooser>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RecurrenceScopeChooser.CalendarEvent)] = new CalendarEvent { Id = Guid.NewGuid(), Title = "重复事件测试", RecurrenceRule = "FREQ=DAILY", UpdatedAtUtc = DateTimeOffset.UtcNow, TimeZoneId = TimeZoneInfo.Utc.Id, Description = "", Location = "", StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddHours(1), CreatedAtUtc = DateTimeOffset.UtcNow, IsAllDay = false },
                [nameof(RecurrenceScopeChooser.Action)] = action,
                [nameof(RecurrenceScopeChooser.Error)] = error,
                [nameof(RecurrenceScopeChooser.Busy)] = busy,
                [nameof(RecurrenceScopeChooser.OccurrenceStartUtc)] = occurrenceStartUtc
            }));
            return component.ToHtmlString();
        });
    }
}
