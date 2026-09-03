using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class CalendarEventRecurrenceDraftTests
{
    [Theory]
    [InlineData(CalendarEventRepeatOption.Never, null)]
    [InlineData(CalendarEventRepeatOption.Daily, "FREQ=DAILY")]
    [InlineData(CalendarEventRepeatOption.Weekly, "FREQ=WEEKLY")]
    [InlineData(CalendarEventRepeatOption.Monthly, "FREQ=MONTHLY")]
    [InlineData(CalendarEventRepeatOption.Yearly, "FREQ=YEARLY")]
    public void SimpleRepeatOptions_ConvertToExpectedRule(
        CalendarEventRepeatOption repeatOption,
        string? expected)
    {
        var draft = new CalendarEventRecurrenceDraft { RepeatOption = repeatOption };

        Assert.Equal(expected, draft.ToRecurrenceRule(new DateTime(2026, 8, 3, 9, 0, 0)));
    }

    [Fact]
    public void CustomWeekly_ConvertsIntervalSelectedDaysAndCountDeterministically()
    {
        var draft = new CalendarEventRecurrenceDraft
        {
            RepeatOption = CalendarEventRepeatOption.Custom,
            CustomFrequency = RecurrenceFrequency.Weekly,
            Interval = 2,
            EndOption = RecurrenceEndOption.AfterCount,
            OccurrenceCount = 8
        };
        draft.SetWeekdaySelected(DayOfWeek.Friday, true);
        draft.SetWeekdaySelected(DayOfWeek.Monday, true);
        draft.SetWeekdaySelected(DayOfWeek.Wednesday, true);

        var rule = draft.ToRecurrenceRule(new DateTime(2026, 8, 3, 9, 0, 0));

        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE,FR;COUNT=8", rule);
    }

    [Fact]
    public void CustomUntil_ConvertsEndDateToRfcDateValue()
    {
        var draft = new CalendarEventRecurrenceDraft
        {
            RepeatOption = CalendarEventRepeatOption.Custom,
            CustomFrequency = RecurrenceFrequency.Daily,
            Interval = 1,
            EndOption = RecurrenceEndOption.OnDate,
            UntilDate = new DateOnly(2026, 8, 31)
        };

        Assert.Equal(
            "FREQ=DAILY;UNTIL=20260831",
            draft.ToRecurrenceRule(new DateTime(2026, 8, 3, 9, 0, 0)));
    }

    [Fact]
    public void ExistingRule_LoadsAsStructuredCustomSettingsWithoutExposingRawText()
    {
        var calendarEvent = CreateEvent() with
        {
            RecurrenceRule = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=6"
        };
        var service = CreateService();

        var draft = service.CreateDraft(calendarEvent);

        Assert.Equal(CalendarEventRepeatOption.Custom, draft.Recurrence.RepeatOption);
        Assert.Equal(RecurrenceFrequency.Weekly, draft.Recurrence.CustomFrequency);
        Assert.Equal(2, draft.Recurrence.Interval);
        Assert.Equal(RecurrenceEndOption.AfterCount, draft.Recurrence.EndOption);
        Assert.Equal(6, draft.Recurrence.OccurrenceCount);
        Assert.Equal(
            [DayOfWeek.Monday, DayOfWeek.Wednesday],
            draft.Recurrence.SelectedWeekdays.OrderBy(day => ((int)day + 6) % 7));
    }

    [Fact]
    public void ExistingUtcUntilRule_IsPreservedWhenOnlyOtherEventFieldsAreEdited()
    {
        const string originalRule = "FREQ=DAILY;UNTIL=20260831T010000Z";
        var calendarEvent = CreateEvent() with { RecurrenceRule = originalRule };
        var service = CreateService();

        var draft = service.CreateDraft(calendarEvent);
        draft.Title = "只修改标题";

        Assert.Equal(originalRule, draft.Recurrence.ToRecurrenceRule(draft.StartLocal));
    }

    [Fact]
    public void ExistingCountAndUntilCombination_IsPreservedUntilRecurrenceSettingsChange()
    {
        const string originalRule = "FREQ=DAILY;COUNT=5;UNTIL=20260831";
        var calendarEvent = CreateEvent() with { RecurrenceRule = originalRule };
        var draft = CreateService().CreateDraft(calendarEvent);

        Assert.Equal(originalRule, draft.Recurrence.ToRecurrenceRule(draft.StartLocal));

        draft.Recurrence.Interval = 2;
        Assert.Equal("FREQ=DAILY;INTERVAL=2;COUNT=5", draft.Recurrence.ToRecurrenceRule(draft.StartLocal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CustomInterval_MustBePositive(int interval)
    {
        var draft = new CalendarEventRecurrenceDraft
        {
            RepeatOption = CalendarEventRepeatOption.Custom,
            CustomFrequency = RecurrenceFrequency.Daily,
            Interval = interval
        };

        Assert.Throws<ArgumentException>(() => draft.ToRecurrenceRule(DateTime.Today));
    }

    [Fact]
    public void WeeklyCustomRule_RequiresAtLeastOneSelectedDay()
    {
        var draft = new CalendarEventRecurrenceDraft
        {
            RepeatOption = CalendarEventRepeatOption.Custom,
            CustomFrequency = RecurrenceFrequency.Weekly
        };

        Assert.Throws<ArgumentException>(() => draft.ToRecurrenceRule(DateTime.Today));
    }

    [Fact]
    public void UntilDate_CannotBeBeforeSeriesStart()
    {
        var draft = new CalendarEventRecurrenceDraft
        {
            RepeatOption = CalendarEventRepeatOption.Custom,
            CustomFrequency = RecurrenceFrequency.Monthly,
            EndOption = RecurrenceEndOption.OnDate,
            UntilDate = new DateOnly(2026, 7, 31)
        };

        Assert.Throws<ArgumentException>(
            () => draft.ToRecurrenceRule(new DateTime(2026, 8, 1, 9, 0, 0)));
    }

    private static CalendarEventService CreateService()
    {
        var events = new MoiCalendar.Storage.InMemoryEventRepository();
        var operations = new MoiCalendar.Storage.InMemoryOperationRepository();
        return new CalendarEventService(
            events,
            new MoiCalendar.Storage.InMemoryDeviceService("recurrence-editor-device"),
            new MoiCalendar.Storage.InMemoryEventChangeRepository(events, operations),
            TimeProvider.System);
    }

    private static CalendarEvent CreateEvent()
    {
        var start = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "重复编辑测试",
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            CreatedAtUtc = start.AddDays(-1),
            UpdatedAtUtc = start
        };
    }
}
