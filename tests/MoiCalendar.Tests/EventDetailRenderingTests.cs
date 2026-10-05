using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class EventDetailRenderingTests
{
    [Fact]
    public async Task LongContent_KeepsFullTextAndActions()
    {
        var title = string.Concat(Enumerable.Repeat("长标题无空格LongTitle", 30));
        var location = "https://example.invalid/" + new string('a', 400);
        var notes = "第一行\n第二行\n" + new string('b', 600);
        var html = await RenderAsync(title, location, notes);
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains(title, decoded);
        Assert.Contains(location, decoded);
        Assert.Contains(notes, decoded);
        Assert.Contains("关闭事件详情", decoded);
        Assert.Contains("编辑", decoded);
        Assert.Contains("删除", decoded);
        Assert.Contains("event-notes-preview", html);
    }

    [Fact]
    public async Task UserContent_IsEscapedRatherThanInterpretedAsHtml()
    {
        const string content = "<script>alert('测试')</script> & <img src=x>";
        var html = await RenderAsync(content, content, content);

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img src=x>", html);
        Assert.Contains(content, WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task BlankOptionalContent_DoesNotCreateEmptyDetailRows()
    {
        var decoded = WebUtility.HtmlDecode(await RenderAsync("普通事件", "  ", "\n"));
        Assert.DoesNotContain("<dt>地点</dt>", decoded);
        Assert.DoesNotContain("<dt>备注</dt>", decoded);
        Assert.Contains("普通事件", decoded);
    }

    private static async Task<string> RenderAsync(string title, string location, string notes)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<EventDetailPopover>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(EventDetailPopover.CalendarEvent)] = new CalendarEvent
                    {
                        Id = Guid.Parse("ea333450-82e5-4897-a25f-a1d4c29c8ea0"),
                        Title = title,
                        Location = location,
                        Description = notes,
                        IsAllDay = false,
                        StartUtc = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero),
                        EndUtc = new DateTimeOffset(2026, 9, 29, 11, 0, 0, TimeSpan.Zero),
                        CreatedAtUtc = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero),
                        UpdatedAtUtc = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero),
                        TimeZoneId = TimeZoneInfo.Utc.Id
                    },
                    [nameof(EventDetailPopover.TimeText)] = "2026年9月29日 10:00–11:00"
                }));
            return component.ToHtmlString();
        });
    }
}
