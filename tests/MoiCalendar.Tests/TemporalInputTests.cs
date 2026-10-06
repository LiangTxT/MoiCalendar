using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class TemporalInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editor_SeparatesDatesAndMinuteTimes_WithoutMutatingDraft(bool allDay)
    {
        var draft = new CalendarEventDraft { StartLocal = new(2026, 10, 8, 23, 34, 0), EndLocal = new(2026, 10, 9, 1, 19, 0) };
        draft.SetAllDay(allDay);
        var start = draft.StartLocal;
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<EventDateTimeFields>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EventDateTimeFields.Draft)] = draft }));
            return component.ToHtmlString();
        });
        Assert.Contains("value=\"2026-10-08\"", html);
        Assert.Contains("value=\"2026-10-09\"", html);
        Assert.DoesNotContain("datetime-local", html);
        Assert.Equal(!allDay, html.Contains("id=\"event-start-time\""));
        if (!allDay)
        {
            Assert.Contains("value=\"23:34\"", html);
            Assert.Contains("value=\"01:19\"", html);
            Assert.Contains("step=\"60\"", html);
        }
        Assert.DoesNotContain("<svg", html);
        Assert.Contains("aria-describedby=\"editor-date-time-hint\"", html);
        Assert.Equal(start, draft.StartLocal);
    }

    [Fact]
    public void DateAndTimeEdits_PreserveOtherPartAndUnspecifiedKind()
    {
        var current = new DateTime(2026, 10, 8, 23, 34, 0, DateTimeKind.Unspecified);
        var date = EventDateTimeFields.WithDate(current, "2026-10-09");
        Assert.Equal(new DateTime(2026, 10, 9, 23, 34, 0), date);
        var time = EventDateTimeFields.WithTime(date, "01:19");
        Assert.Equal(new DateTime(2026, 10, 9, 1, 19, 0), time);
        Assert.Equal(DateTimeKind.Unspecified, time.Kind);
        Assert.Equal(time, EventDateTimeFields.WithDate(time, ""));
        Assert.Equal(time, EventDateTimeFields.WithDate(time, "2026-02-30"));
        Assert.Equal(time, EventDateTimeFields.WithTime(time, "24:01"));
    }
}
