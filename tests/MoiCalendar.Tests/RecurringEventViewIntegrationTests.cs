using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class RecurringEventViewIntegrationTests
{
    [Fact]
    public async Task MonthWeekAndAgenda_ReuseRangeExpansionWithoutPersistingOccurrences()
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("recurrence-view-device"),
            new InMemoryEventChangeRepository(repository, operations),
            TimeProvider.System,
            new RecurrenceExpansionService());
        var master = CreateMaster();
        await repository.CreateAsync(master);

        var month = new CalendarMonth(2026, 8);
        var monthView = await service.GetMonthViewAsync(month.CreateView(new DateOnly(2026, 8, 1)), TimeZoneInfo.Utc.Id);
        var agenda = await service.GetAgendaViewAsync(month, TimeZoneInfo.Utc.Id);
        var week = CalendarWeek.FromDate(new DateOnly(2026, 8, 10));
        var weekView = await service.GetWeekViewAsync(week.CreateView(new DateOnly(2026, 8, 10)), TimeZoneInfo.Utc.Id);

        Assert.Equal(42, month.CreateView(new DateOnly(2026, 8, 1)).Dates.Sum(
            date => monthView.GetEvents(date.Date).Count));
        Assert.Equal(31, agenda.Days.Sum(day => day.Events.Count));
        Assert.Equal(7, weekView.Days.Sum(day => day.TimedEvents.Count));
        Assert.All(
            month.CreateView(new DateOnly(2026, 8, 1)).Dates.SelectMany(date => monthView.GetEvents(date.Date)),
            item => Assert.True(item.IsRecurring));
        Assert.All(agenda.Days.SelectMany(day => day.Events), item => Assert.True(item.IsRecurring));
        Assert.All(weekView.Days.SelectMany(day => day.TimedEvents), item => Assert.True(item.IsRecurring));
        Assert.Single(await repository.GetAllIncludingDeletedAsync());
        Assert.Same(master, await repository.GetByIdAsync(master.Id));
    }

    [Fact]
    public async Task EditorGeneratedWeeklyRule_DisplaysSelectedDaysAndHonorsCount()
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        var service = new CalendarEventService(
            repository,
            new InMemoryDeviceService("recurrence-editor-view-device"),
            new InMemoryEventChangeRepository(repository, operations),
            TimeProvider.System);
        var monday = new DateOnly(2026, 8, 3);
        var draft = service.CreateDraft(monday, TimeZoneInfo.Utc.Id);
        draft.Title = "每周一和周三";
        draft.Recurrence.RepeatOption = CalendarEventRepeatOption.Custom;
        draft.Recurrence.CustomFrequency = RecurrenceFrequency.Weekly;
        draft.Recurrence.EndOption = RecurrenceEndOption.AfterCount;
        draft.Recurrence.OccurrenceCount = 3;
        draft.Recurrence.SetWeekdaySelected(DayOfWeek.Monday, true);
        draft.Recurrence.SetWeekdaySelected(DayOfWeek.Wednesday, true);

        var master = await service.CreateAsync(draft);
        var week = CalendarWeek.FromDate(monday);
        var view = await service.GetWeekViewAsync(week.CreateView(monday), TimeZoneInfo.Utc.Id);

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=3", master.RecurrenceRule);
        Assert.Equal(
            [monday, monday.AddDays(2)],
            view.Days
                .Where(day => day.TimedEvents.Count > 0)
                .Select(day => day.Date.Date));
        Assert.All(view.Days.SelectMany(day => day.TimedEvents), item => Assert.True(item.IsRecurring));
        Assert.Single(await repository.GetAllIncludingDeletedAsync());
    }

    private static CalendarEvent CreateMaster()
    {
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "每天重复",
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            RecurrenceRule = "FREQ=DAILY",
            CreatedAtUtc = start.AddDays(-1),
            UpdatedAtUtc = start
        };
    }
}
