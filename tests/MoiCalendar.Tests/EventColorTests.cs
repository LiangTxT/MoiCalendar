using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MoiCalendar.App.Components;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class EventColorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateEditAndViews_PreserveChosenColor(bool allDay)
    {
        var repository = new InMemoryEventRepository();
        var service = CreateService(repository);
        var date = new DateOnly(2026, 10, 6);
        var draft = service.CreateDraft(date, TimeZoneInfo.Utc.Id);
        draft.Title = "颜色测试";
        draft.ColorIndex = 4;
        draft.SetAllDay(allDay);
        var created = await service.CreateAsync(draft);
        Assert.Equal(4, created.ColorIndex);
        var edit = service.CreateDraft(created);
        Assert.Equal(4, edit.ColorIndex);
        edit.ColorIndex = 8;
        var updated = await service.UpdateAsync(created.Id, edit);
        var month = new CalendarMonth(2026, 10);
        var monthEvents = await service.GetMonthViewAsync(month.CreateView(date), TimeZoneInfo.Utc.Id);
        Assert.Equal(8, Assert.Single(monthEvents.GetEvents(date)).CalendarColorIndex);
        var agenda = await service.GetAgendaViewAsync(month, TimeZoneInfo.Utc.Id);
        Assert.Equal(8, Assert.Single(Assert.Single(agenda.Days).Events).CalendarColorIndex);
        var week = await service.GetWeekViewAsync(CalendarWeek.FromDate(date).CreateView(date), TimeZoneInfo.Utc.Id);
        var day = week.Days.Single(item => item.Date.Date == date);
        Assert.Equal(8, allDay ? Assert.Single(day.AllDayEvents).CalendarColorIndex : Assert.Single(day.TimedEvents).CalendarColorIndex);
        var json = JsonSerializer.Serialize(updated);
        Assert.Equal(updated, JsonSerializer.Deserialize<CalendarEvent>(json));
        var backup = await new LocalBackupService(repository, TimeProvider.System).CreateExportAsync();
        Assert.Contains("\"colorIndex\": 8", backup.Json);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public async Task InvalidPaletteValue_IsRejected(int color)
    {
        var service = CreateService(new InMemoryEventRepository());
        var draft = service.CreateDraft(new(2026, 10, 6), TimeZoneInfo.Utc.Id);
        draft.Title = "无效颜色";
        draft.ColorIndex = color;
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(draft));
    }

    [Fact]
    public async Task RecurringAndDetachedOccurrence_PreserveIndependentColors()
    {
        var repository = new InMemoryEventRepository();
        var service = CreateService(repository);
        var date = new DateOnly(2026, 10, 6);
        var draft = service.CreateDraft(date, TimeZoneInfo.Utc.Id);
        draft.Title = "有颜色的系列";
        draft.ColorIndex = 3;
        draft.Recurrence.RepeatOption = CalendarEventRepeatOption.Daily;
        var series = await service.CreateAsync(draft);
        var month = new CalendarMonth(2026, 10);
        var before = await service.GetMonthViewAsync(month.CreateView(date), TimeZoneInfo.Utc.Id);
        Assert.Equal(3, Assert.Single(before.GetEvents(date.AddDays(1))).CalendarColorIndex);
        var single = service.CreateDraft(series);
        single.StartLocal = single.StartLocal.AddDays(1);
        single.EndLocal = single.EndLocal.AddDays(1);
        single.Recurrence.RepeatOption = CalendarEventRepeatOption.Never;
        single.ColorIndex = 5;
        var detached = await service.UpdateOccurrenceAsync(series.Id, series.StartUtc.AddDays(1), single);
        Assert.Equal(5, detached.ColorIndex);
        Assert.Equal(3, (await repository.GetByIdAsync(series.Id))!.ColorIndex);
    }

    [Fact]
    public async Task OlderJson_DefaultsToThemeBlue()
    {
        var service = CreateService(new InMemoryEventRepository());
        var draft = service.CreateDraft(new(2026, 10, 6), TimeZoneInfo.Utc.Id);
        draft.Title = "旧事件";
        var created = await service.CreateAsync(draft);
        var json = JsonSerializer.Serialize(created).Replace(",\"ColorIndex\":1", "", StringComparison.Ordinal);
        Assert.DoesNotContain("ColorIndex", json);
        Assert.Equal(1, JsonSerializer.Deserialize<CalendarEvent>(json)!.ColorIndex);
    }

    [Fact]
    public async Task Picker_RendersNamedNativeRadiosAndSelectedColor()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<EventColorPicker>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(EventColorPicker.Value)] = 4 }))).ToHtmlString());
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(html, "type=\"radio\"").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "checked").Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("烟紫", System.Net.WebUtility.HtmlDecode(html));
    }

    private static CalendarEventService CreateService(InMemoryEventRepository repository) =>
        new(repository, new InMemoryDeviceService("color-tests"), new InMemoryEventChangeRepository(repository, new InMemoryOperationRepository()), TimeProvider.System);
}
