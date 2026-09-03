using System.Globalization;

namespace MoiCalendar.Core;

public sealed class CalendarEventDraft
{
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

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
        IsAllDay = isAllDay;

        if (!isAllDay)
        {
            return;
        }

        StartLocal = StartLocal.Date;
        EndLocal = EndLocal.Date <= StartLocal.Date
            ? StartLocal.Date.AddDays(1)
            : EndLocal.Date;
    }

    internal static CalendarEventDraft ForDate(DateOnly date, string timeZoneId) => new()
    {
        StartLocal = date.ToDateTime(new TimeOnly(9, 0)),
        EndLocal = date.ToDateTime(new TimeOnly(10, 0)),
        TimeZoneId = timeZoneId
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
            StartLocal = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified),
            EndLocal = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified),
            TimeZoneId = calendarEvent.TimeZoneId
        };

        draft.SetAllDay(calendarEvent.IsAllDay);
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
