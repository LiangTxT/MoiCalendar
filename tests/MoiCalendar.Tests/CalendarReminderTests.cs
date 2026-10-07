using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class CalendarReminderTests
{
    private static CalendarEvent Event() => new()
    {
        Id = Guid.Parse("11223344-5566-7788-9900-112233445566"), Title = "测试提醒", Description = "", Location = "",
        StartUtc = DateTimeOffset.Parse("2026-10-06T09:00:00Z"), EndUtc = DateTimeOffset.Parse("2026-10-06T10:00:00Z"),
        TimeZoneId = "UTC", IsAllDay = false, ReminderMinutesBeforeStart = 15, ReminderTimeZoneId = "Asia/Hong_Kong",
        CreatedAtUtc = DateTimeOffset.Parse("2026-10-01T00:00:00Z"), UpdatedAtUtc = DateTimeOffset.Parse("2026-10-01T00:00:00Z")
    };

    [Theory]
    [InlineData(0)] [InlineData(5)] [InlineData(15)] [InlineData(30)] [InlineData(60)] [InlineData(1440)]
    public void TimedReminder_UsesAnInstant(int minutes)
    {
        var e = Event() with { ReminderMinutesBeforeStart = minutes };
        Assert.Equal(e.StartUtc.AddMinutes(-minutes), ReminderPolicy.DueAt(e));
    }

    [Theory]
    [InlineData(420, "2026-10-05T23:00:00Z")]
    [InlineData(510, "2026-10-06T00:30:00Z")]
    [InlineData(0, "2026-10-05T16:00:00Z")]
    [InlineData(1439, "2026-10-06T15:59:00Z")]
    public void AllDayReminder_UsesDateAndCustomTime(int time, string expected)
    {
        var e = Event() with { IsAllDay = true, StartUtc = DateTimeOffset.Parse("2026-10-06T00:00:00Z"), ReminderMinutesBeforeStart = 0, AllDayReminderMinuteOfDay = time };
        Assert.Equal(DateTimeOffset.Parse(expected), ReminderPolicy.DueAt(e));
    }

    [Fact]
    public void AdvanceOneDay_PreservesSevenAmAcrossDst()
    {
        var e = Event() with { IsAllDay = true, StartUtc = DateTimeOffset.Parse("2026-03-08T00:00:00Z"), ReminderMinutesBeforeStart = 1440, ReminderTimeZoneId = "America/New_York" };
        Assert.Equal(DateTimeOffset.Parse("2026-03-07T12:00:00Z"), ReminderPolicy.DueAt(e));
    }

    [Fact]
    public async Task DeletedOccurrence_DoesNotRemindOrExtendCount()
    {
        var e = Event() with { RecurrenceRule = "FREQ=DAILY;COUNT=3", ExcludedOccurrenceStartsUtc = [Event().StartUtc.AddDays(1)] };
        var repository = new InMemoryEventRepository(); await repository.CreateAsync(e);
        var reminders = await new CalendarReminderService(repository, new RecurrenceExpansionService()).GetDueAsync(e.StartUtc.AddHours(-1), e.StartUtc.AddDays(5));
        Assert.Equal(new[] { e.StartUtc, e.StartUtc.AddDays(2) }, reminders.Select(r => r.OccurrenceStartUtc));
        Assert.Equal(new DateOnly(2026, 10, 6), reminders[0].Date);
        Assert.Contains(":15:", reminders[0].Key);
    }

    [Fact]
    public async Task DueRange_IsExclusiveThenInclusiveAndTombstonesAreSilent()
    {
        var e = Event(); var due = ReminderPolicy.DueAt(e);
        var repository = new InMemoryEventRepository(); await repository.CreateAsync(e);
        var service = new CalendarReminderService(repository, new RecurrenceExpansionService());
        Assert.Single(await service.GetDueAsync(due.AddSeconds(-1), due));
        Assert.Empty(await service.GetDueAsync(due, due.AddSeconds(1)));
        await repository.UpdateAsync(e with { DeletedAtUtc = e.UpdatedAtUtc });
        Assert.Empty(await service.GetDueAsync(due.AddSeconds(-1), due));
    }

    [Fact]
    public void MetadataRoundTripAndDraft_PreserveCustomTime()
    {
        var original = Event() with { AllDayReminderMinuteOfDay = 505 };
        var restored = JsonSerializer.Deserialize<CalendarEvent>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original, restored); Assert.Equal(original.GetHashCode(), restored.GetHashCode());
        var draft = new CalendarEventDraft(); Assert.Equal("07:00", draft.AllDayReminderTimeInput);
        draft.AllDayReminderTimeInput = "08:25"; Assert.Equal(505, draft.AllDayReminderMinuteOfDay);
        draft.AllDayReminderTimeInput = "not-a-time"; Assert.Equal(505, draft.AllDayReminderMinuteOfDay);
        draft.AllDayReminderTimeInput = "08:30:00"; Assert.Equal(510, draft.AllDayReminderMinuteOfDay);
        draft.AllDayReminderTime = new TimeOnly(9, 15); Assert.Equal(555, draft.AllDayReminderMinuteOfDay);
        Assert.NotEqual(ReminderPolicy.Key(original), ReminderPolicy.Key(original with { IsAllDay = true }));
    }

    [Theory] [InlineData(-1)] [InlineData(1440)]
    public void InvalidAllDayTime_IsRejected(int value) => Assert.Throws<ArgumentException>(() => ReminderPolicy.ValidateAllDayTime(value));
    [Theory] [InlineData(-1)] [InlineData(10)] [InlineData(2000)]
    public void InvalidOffset_IsRejected(int value) => Assert.Throws<ArgumentException>(() => ReminderPolicy.Validate(value));
}
