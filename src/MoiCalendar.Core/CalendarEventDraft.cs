using System.Globalization;

namespace MoiCalendar.Core;

public sealed class CalendarEventDraft
{
    private TimeSpan? timedStart;
    private TimeSpan timedDuration = TimeSpan.FromHours(1);
    internal DateTimeOffset? ExpectedUpdatedAtUtc { get; private set; }
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public int ColorIndex { get; set; } = 1;
    public int RemovedExclusionCount { get; internal set; }
    public int? ReminderMinutesBeforeStart { get; set; }
    public string? ReminderTimeZoneId { get; set; }
    public int AllDayReminderMinuteOfDay { get; set; } = 420;
    public TimeOnly AllDayReminderTime
    {
        get => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(AllDayReminderMinuteOfDay));
        set => AllDayReminderMinuteOfDay = value.Hour * 60 + value.Minute;
    }
    public string AllDayReminderTimeInput
    {
        get => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(AllDayReminderMinuteOfDay)).ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            if (TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                AllDayReminderMinuteOfDay = time.Hour * 60 + time.Minute;
        }
    }
    public int ReminderChoice { get => ReminderMinutesBeforeStart ?? -1; set => ReminderMinutesBeforeStart = value < 0 ? null : value; }

    public DateTime StartLocal { get; set; }

    public DateTime EndLocal { get; set; }

    public string TimeZoneId { get; set; } = TimeZoneInfo.Utc.Id;

    public bool IsAllDay { get; private set; }

    public CalendarEventRecurrenceDraft Recurrence { get; private set; } = new();

    public string StartInput
    {
        get => FormatInput(StartLocal);
        set => StartLocal = ParseInput(value, StartLocal);
    }

    public string EndInput
    {
        get => FormatInput(EndLocal);
        set => EndLocal = ParseInput(value, EndLocal);
    }

    public void SetAllDay(bool isAllDay)
    {
        var wasAllDay = IsAllDay;
        if (wasAllDay == isAllDay) return;
        IsAllDay = isAllDay;

        if (!isAllDay)
        {
            if (wasAllDay)
            {
                StartLocal = StartLocal.Date + (timedStart ?? TimeSpan.FromHours(9));
                EndLocal = StartLocal + timedDuration;
            }
            return;
        }

        timedStart = StartLocal.TimeOfDay;
        timedDuration = EndLocal > StartLocal ? EndLocal - StartLocal : TimeSpan.FromHours(1);
        StartLocal = StartLocal.Date;
        EndLocal = EndLocal.Date <= StartLocal.Date
            ? StartLocal.Date.AddDays(1)
            : EndLocal.Date;
    }

    internal static CalendarEventDraft ForDate(DateOnly date, string timeZoneId) => new()
    {
        StartLocal = date.ToDateTime(new TimeOnly(9, 0)),
        EndLocal = date.ToDateTime(new TimeOnly(10, 0)),
        TimeZoneId = timeZoneId,
        ReminderTimeZoneId = timeZoneId
    };

    internal static CalendarEventDraft FromEvent(CalendarEvent calendarEvent, TimeZoneInfo timeZone)
    {
        var startLocal = TimeZoneInfo.ConvertTime(calendarEvent.StartUtc, timeZone).DateTime;
        var endLocal = TimeZoneInfo.ConvertTime(calendarEvent.EndUtc, timeZone).DateTime;

        var draft = new CalendarEventDraft
        {
            Title = calendarEvent.Title,
            Description = calendarEvent.Description,
            Location = calendarEvent.Location,
            ColorIndex = calendarEvent.ColorIndex,
            ExpectedUpdatedAtUtc = calendarEvent.UpdatedAtUtc,
            StartLocal = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified),
            EndLocal = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified),
            TimeZoneId = calendarEvent.TimeZoneId,
            ReminderMinutesBeforeStart = calendarEvent.ReminderMinutesBeforeStart,
            AllDayReminderMinuteOfDay = calendarEvent.AllDayReminderMinuteOfDay,
            ReminderTimeZoneId = calendarEvent.ReminderTimeZoneId ?? (calendarEvent.IsAllDay ? TimeZoneInfo.Local.Id : calendarEvent.TimeZoneId)
        };

        // Loading an existing all-day event has no remembered timed duration.
        draft.IsAllDay = calendarEvent.IsAllDay;
        draft.Recurrence = CalendarEventRecurrenceDraft.FromRule(
            calendarEvent.RecurrenceRule,
            draft.StartLocal,
            timeZone);
        return draft;
    }

    private string FormatInput(DateTime value) =>
        value.ToString(IsAllDay ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static DateTime ParseInput(string value, DateTime currentValue)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return currentValue;
        }

        return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
    }
}
