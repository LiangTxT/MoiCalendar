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
        Assert.Single(await repository.GetAllIncludingDeletedAsync());
        Assert.Same(master, await repository.GetByIdAsync(master.Id));
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
