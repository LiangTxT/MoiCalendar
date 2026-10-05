using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class OccurrenceDeletionTests
{
    [Theory]
    [InlineData("DAILY")]
    [InlineData("WEEKLY")]
    [InlineData("MONTHLY")]
    [InlineData("YEARLY")]
    public async Task DeleteOne_LeavesOtherOccurrencesAndDoesNotExtendCount(string frequency)
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent() with { RecurrenceRule = $"FREQ={frequency};COUNT=5" };
        await repository.CreateAsync(master);
        var before = Expand(master);
        Assert.Equal(5, before.Count);
        var updated = await service.DeleteOccurrenceAsync(master.Id, before[2].StartUtc);
        Assert.Equal(before.Where((_, index) => index != 2).Select(item => item.StartUtc), Expand(updated).Select(item => item.StartUtc));
        Assert.Null(updated.DeletedAtUtc);
        Assert.Equal(master.RecurrenceRule, updated.RecurrenceRule);
    }

    [Fact]
    public async Task Deletion_SurvivesSerializationAndRepeatedRequest()
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent();
        await repository.CreateAsync(master);
        var excluded = master.StartUtc.AddDays(1);
        var updated = await service.DeleteOccurrenceAsync(master.Id, excluded);
        var again = await service.DeleteOccurrenceAsync(master.Id, excluded.ToOffset(TimeSpan.FromHours(8)));
        Assert.Same(updated, again);
        var restored = JsonSerializer.Deserialize<CalendarEvent>(JsonSerializer.Serialize(updated))!;
        var (reloadedRepository, reloadedService) = CreateContext();
        await reloadedRepository.CreateAsync(restored);
        Assert.DoesNotContain(Expand((await reloadedService.GetByIdAsync(master.Id))!), item => item.StartUtc == excluded);
        Assert.Single(restored.ExcludedOccurrenceStartsUtc);
        Assert.Equal(updated, restored);
        Assert.Equal(updated.GetHashCode(), restored.GetHashCode());
    }

    [Fact]
    public async Task DeleteAllDayOccurrence_RemovesItsEntireMultiDaySpan()
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent() with { IsAllDay = true, StartUtc = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), EndUtc = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), RecurrenceRule = "FREQ=WEEKLY;COUNT=3" };
        await repository.CreateAsync(master);
        var updated = await service.DeleteOccurrenceAsync(master.Id, master.StartUtc.AddDays(7));
        Assert.Equal(new[] { master.StartUtc, master.StartUtc.AddDays(14) }, Expand(updated).Select(item => item.StartUtc));
        Assert.Empty(new RecurrenceExpansionService().Expand([updated], master.StartUtc.AddDays(8), master.StartUtc.AddDays(9)));
    }

    [Fact]
    public async Task DeleteOccurrence_UsesActualStartAcrossDaylightSaving()
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent() with { StartUtc = new(2026, 10, 31, 13, 0, 0, TimeSpan.Zero), EndUtc = new(2026, 10, 31, 14, 0, 0, TimeSpan.Zero), TimeZoneId = "America/New_York" };
        await repository.CreateAsync(master);
        var occurrences = Expand(master);
        Assert.Equal(14, occurrences[1].StartUtc.Hour);
        var updated = await service.DeleteOccurrenceAsync(master.Id, occurrences[1].StartUtc);
        Assert.DoesNotContain(Expand(updated), item => item.StartUtc == occurrences[1].StartUtc);
        Assert.Equal(4, Expand(updated).Count);
    }

    [Fact]
    public async Task InvalidOccurrence_DoesNotWriteAnExclusion()
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent();
        await repository.CreateAsync(master);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteOccurrenceAsync(master.Id, master.StartUtc.AddMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteOccurrenceAsync(master.Id, master.StartUtc.AddDays(5)));
        Assert.Empty((await repository.GetByIdAsync(master.Id))!.ExcludedOccurrenceStartsUtc);
        var single = master with { Id = Guid.NewGuid(), RecurrenceRule = null };
        await repository.CreateAsync(single);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteOccurrenceAsync(single.Id, single.StartUtc));
    }

    [Fact]
    public async Task EditingTitle_PreservesExclusions()
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent();
        await repository.CreateAsync(master);
        var updated = await service.DeleteOccurrenceAsync(master.Id, master.StartUtc);
        var draft = service.CreateDraft(updated);
        draft.Title = "更名后";
        var edited = await service.UpdateAsync(master.Id, draft);
        Assert.Equal(updated.ExcludedOccurrenceStartsUtc, edited.ExcludedOccurrenceStartsUtc);
        Assert.Equal(4, Expand(edited).Count);
    }

    [Fact]
    public void OlderStoredEvent_WithoutExclusions_RemainsCompatible()
    {
        var json = JsonSerializer.Serialize(CreateEvent());
        json = json.Replace(",\"ExcludedOccurrenceStartsUtc\":[]", "", StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<CalendarEvent>(json)!;
        Assert.Empty(restored.ExcludedOccurrenceStartsUtc);
        Assert.Equal(5, Expand(restored).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_PreservesExcludedDates(bool allDay)
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent() with { IsAllDay = allDay };
        if (allDay) master = master with { StartUtc = new DateTimeOffset(master.StartUtc.Date, TimeSpan.Zero), EndUtc = new DateTimeOffset(master.StartUtc.Date.AddDays(1), TimeSpan.Zero) };
        await repository.CreateAsync(master);
        var updated = await service.DeleteOccurrenceAsync(master.Id, master.StartUtc.AddDays(1));
        var export = await new CalendarExportService(repository, TimeProvider.System).CreateExportAsync();
        Assert.Contains(allDay ? "EXDATE;VALUE=DATE:20261002" : "EXDATE:20261002T090000Z", export.Content);
        var backup = await new LocalBackupService(repository, TimeProvider.System).CreateExportAsync();
        var restored = JsonSerializer.Deserialize<MyCalendarBackup>(backup.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(updated, Assert.Single(restored.CalendarData.CalendarEvents));
    }

    [Fact]
    public async Task AllViews_UseClickedOccurrenceIdentityAndHideOnlyThatDate()
    {
        var (repository, service) = CreateContext();
        var master = CreateEvent();
        await repository.CreateAsync(master);
        var month = new CalendarMonth(2026, 10);
        var date = new DateOnly(2026, 10, 2);
        var before = await service.GetMonthViewAsync(month.CreateView(date), TimeZoneInfo.Utc.Id);
        var clicked = Assert.Single(before.GetEvents(date));
        Assert.Equal(master.StartUtc.AddDays(1), clicked.OriginalStartUtc);
        await service.DeleteOccurrenceAsync(clicked.Id, clicked.OriginalStartUtc);
        var after = await service.GetMonthViewAsync(month.CreateView(date), TimeZoneInfo.Utc.Id);
        Assert.Empty(after.GetEvents(date));
        Assert.Single(after.GetEvents(date.AddDays(-1)));
        Assert.Single(after.GetEvents(date.AddDays(1)));
        var agenda = await service.GetAgendaViewAsync(month, TimeZoneInfo.Utc.Id);
        Assert.DoesNotContain(agenda.Days, day => day.Date == date);
        var week = await service.GetWeekViewAsync(CalendarWeek.FromDate(date).CreateView(date), TimeZoneInfo.Utc.Id);
        Assert.Empty(week.Days.Single(day => day.Date.Date == date).TimedEvents);
        Assert.Single(week.Days.Single(day => day.Date.Date == date.AddDays(1)).TimedEvents);
    }

    private static IReadOnlyList<CalendarEvent> Expand(CalendarEvent master) =>
        new RecurrenceExpansionService().Expand([master], master.StartUtc.AddDays(-1), master.StartUtc.AddYears(6));

    private static (InMemoryEventRepository, CalendarEventService) CreateContext()
    {
        var repository = new InMemoryEventRepository();
        var operations = new InMemoryOperationRepository();
        return (repository, new CalendarEventService(repository, new InMemoryDeviceService("occurrence-tests"), new InMemoryEventChangeRepository(repository, operations), TimeProvider.System));
    }

    private static CalendarEvent CreateEvent() => new()
    {
        Id = Guid.NewGuid(), Title = "重复事件", Description = "", Location = "",
        StartUtc = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), EndUtc = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero),
        TimeZoneId = TimeZoneInfo.Utc.Id, IsAllDay = false, RecurrenceRule = "FREQ=DAILY;COUNT=5",
        CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
    };
}
