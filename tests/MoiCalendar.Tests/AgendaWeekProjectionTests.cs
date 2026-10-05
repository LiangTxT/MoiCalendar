using MoiCalendar.App.Components;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class AgendaWeekProjectionTests
{
    [Theory]
    [InlineData(2026, 10, 31)]
    [InlineData(2024, 2, 29)]
    [InlineData(2026, 11, 30)]
    public void EmptyWeeks_CoverEveryDateExactlyOnce(int year, int month, int count)
    {
        var weeks = AgendaWeekProjection.Create(new(year, month), CalendarAgendaView.Empty, true, new(2025, 1, 1));
        var dates = weeks.SelectMany(week => Enumerable.Range(week.Start.DayNumber, week.End.DayNumber - week.Start.DayNumber + 1)).ToArray();
        Assert.Equal(count, dates.Length);
        Assert.Equal(count, dates.Distinct().Count());
        Assert.Equal(new DateOnly(year, month, 1).DayNumber, dates[0]);
        Assert.Equal(new DateOnly(year, month, count).DayNumber, dates[^1]);
        Assert.All(weeks, week => Assert.Empty(week.Days));
    }

    [Fact]
    public void Today_RemainsVisibleWithoutEvents()
    {
        var today = new DateOnly(2026, 10, 5);
        var weeks = AgendaWeekProjection.Create(new(2026, 10), CalendarAgendaView.Empty, true, today);
        var day = Assert.Single(weeks.SelectMany(week => week.Days));
        Assert.Equal(today, day.Date);
        Assert.Empty(day.Events);
    }

    [Fact]
    public async Task Events_AppearOnceAndHonorCalendarVisibility()
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var service = new CalendarEventService(repository, new InMemoryDeviceService("agenda-projection"),
            new InMemoryEventChangeRepository(repository, operations), TimeProvider.System);
        var draft = service.CreateDraft(new(2026, 10, 8), TimeZoneInfo.Utc.Id);
        draft.Title = "测试日程";
        await service.CreateAsync(draft);
        var view = await service.GetAgendaViewAsync(new(2026, 10), TimeZoneInfo.Utc.Id);
        var weeks = AgendaWeekProjection.Create(new(2026, 10), view, true, new(2025, 1, 1));
        Assert.Single(weeks.SelectMany(week => week.Days).SelectMany(day => day.Events));
        var hidden = AgendaWeekProjection.Create(new(2026, 10), view, false, new(2025, 1, 1));
        Assert.All(hidden, week => Assert.Empty(week.Days));
    }
}
