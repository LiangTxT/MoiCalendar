using System.Globalization;
using System.Text;

namespace MoiCalendar.Core;

public sealed record ICalendarExport(
    string FileName,
    string Content,
    string MediaType = "text/calendar;charset=utf-8");

public interface ICalendarExportService
{
    Task<ICalendarExport> CreateExportAsync(CancellationToken cancellationToken = default);
}

public sealed class CalendarExportService(
    IEventRepository eventRepository,
    TimeProvider timeProvider) : ICalendarExportService
{
    private const string ProductIdentifier = "-//MoiCalendar//MoiCalendar V1//ZH-CN";

    public async Task<ICalendarExport> CreateExportAsync(CancellationToken cancellationToken = default)
    {
        var events = (await eventRepository.GetAllIncludingDeletedAsync(cancellationToken))
            .Where(calendarEvent => calendarEvent.DeletedAtUtc is null)
            .OrderBy(calendarEvent => calendarEvent.StartUtc)
            .ThenBy(calendarEvent => calendarEvent.Id)
            .ToArray();
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "PRODID:" + ProductIdentifier,
            "VERSION:2.0",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH"
        };

        foreach (var calendarEvent in events)
        {
            AppendEvent(lines, calendarEvent);
        }

        lines.Add("END:VCALENDAR");
        var content = string.Join("\r\n", lines.SelectMany(FoldLine)) + "\r\n";
        return new ICalendarExport(
            $"moicalendar-calendar-{timeProvider.GetUtcNow():yyyy-MM-dd}.ics",
            content);
    }

    private static void AppendEvent(ICollection<string> lines, CalendarEvent calendarEvent)
    {
        var timeZone = ResolveTimeZone(calendarEvent.TimeZoneId);
        lines.Add("BEGIN:VEVENT");
        lines.Add("UID:" + EscapeText(
            string.IsNullOrWhiteSpace(calendarEvent.ExternalUid)
                ? $"{calendarEvent.Id:D}@moicalendar.local"
                : calendarEvent.ExternalUid));
        lines.Add("SUMMARY:" + EscapeText(calendarEvent.Title));
        if (!string.IsNullOrEmpty(calendarEvent.Description))
        {
            lines.Add("DESCRIPTION:" + EscapeText(calendarEvent.Description));
        }

        if (!string.IsNullOrEmpty(calendarEvent.Location))
        {
            lines.Add("LOCATION:" + EscapeText(calendarEvent.Location));
        }

        if (calendarEvent.IsAllDay)
        {
            var startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(calendarEvent.StartUtc, timeZone).DateTime);
            var endDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(calendarEvent.EndUtc, timeZone).DateTime);
            lines.Add($"DTSTART;VALUE=DATE:{startDate:yyyyMMdd}");
            lines.Add($"DTEND;VALUE=DATE:{endDate:yyyyMMdd}");
        }
        else if (timeZone.Equals(TimeZoneInfo.Utc))
        {
            lines.Add("DTSTART:" + FormatUtc(calendarEvent.StartUtc));
            lines.Add("DTEND:" + FormatUtc(calendarEvent.EndUtc));
        }
        else
        {
            var timeZoneId = EscapeParameterValue(calendarEvent.TimeZoneId);
            lines.Add($"DTSTART;TZID={timeZoneId}:{FormatLocal(calendarEvent.StartUtc, timeZone)}");
            lines.Add($"DTEND;TZID={timeZoneId}:{FormatLocal(calendarEvent.EndUtc, timeZone)}");
        }

        if (!string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule))
        {
            _ = RecurrenceRuleParser.Parse(calendarEvent.RecurrenceRule);
            var rule = calendarEvent.RecurrenceRule.Trim();
            if (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            {
                rule = rule[6..];
            }

            lines.Add("RRULE:" + rule);
        }

        lines.Add("CREATED:" + FormatUtc(calendarEvent.CreatedAtUtc));
        lines.Add("LAST-MODIFIED:" + FormatUtc(calendarEvent.UpdatedAtUtc));
        lines.Add("END:VEVENT");
    }

    private static string EscapeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal);

    private static string EscapeParameterValue(string value)
    {
        if (value.Any(character => character is ':' or ';' or ',' or '"'))
        {
            return '"' + value.Replace("\"", "'", StringComparison.Ordinal) + '"';
        }

        return value;
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string FormatLocal(DateTimeOffset value, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(value, timeZone).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ICalendarExportException("事件包含当前设备无法识别的时区，无法导出日历。", exception);
        }
    }

    private static IEnumerable<string> FoldLine(string line)
    {
        var current = new StringBuilder();
        var byteCount = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeText);
            if (byteCount > 0 && byteCount + runeBytes > limit)
            {
                yield return current.ToString();
                current.Clear();
                current.Append(' ');
                byteCount = 1;
                limit = 75;
            }

            current.Append(runeText);
            byteCount += runeBytes;
        }

        yield return current.ToString();
    }
}

public sealed class ICalendarExportException : Exception
{
    public ICalendarExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
