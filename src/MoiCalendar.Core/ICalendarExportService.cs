using System.Globalization;
using System.Text;

namespace MoiCalendar.Core;

public sealed record ICalendarExport(
    string FileName,
    string Content,
    string MediaType = "text/calendar;charset=utf-8")
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

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

        var warnings = new List<string>();
        foreach (var calendarEvent in events)
        {
            AppendEvent(lines, calendarEvent, warnings);
        }

        lines.Add("END:VCALENDAR");
        var content = string.Join("\r\n", lines.SelectMany(FoldLine)) + "\r\n";
        return new ICalendarExport(
            $"moicalendar-calendar-{timeProvider.GetUtcNow():yyyy-MM-dd}.ics",
            content) { Warnings = warnings };
    }

    private static void AppendEvent(ICollection<string> lines, CalendarEvent calendarEvent, ICollection<string> warnings)
    {
        var timeZone = ResolveTimeZone(calendarEvent.TimeZoneId);
        lines.Add("BEGIN:VEVENT");
        lines.Add("UID:" + EscapeText(
            string.IsNullOrWhiteSpace(calendarEvent.ExternalUid)
                ? $"{calendarEvent.Id:D}@moicalendar.local"
                : calendarEvent.ExternalUid));
        lines.Add("SUMMARY:" + EscapeText(calendarEvent.Title));
        lines.Add("X-MOICALENDAR-COLOR-INDEX:" + calendarEvent.ColorIndex.ToString(CultureInfo.InvariantCulture));
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
            lines.Add("X-MOICALENDAR-TZID:" + EscapeText(calendarEvent.TimeZoneId));
            lines.Add("DTSTART;VALUE=DATE:" + startDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            lines.Add("DTEND;VALUE=DATE:" + endDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
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
            ParsedRecurrenceRule? parsed = null;
            try { parsed = RecurrenceRuleParser.Parse(calendarEvent.RecurrenceRule); }
            catch (RecurrenceRuleException)
            {
                warnings.Add($"“{calendarEvent.Title}”的重复规则不受支持，已保留原始规则；导入其他日历前请检查。");
            }
            var rule = calendarEvent.RecurrenceRule.Trim();
            if (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            {
                rule = rule[6..];
            }

            if (!calendarEvent.IsAllDay && parsed?.Until?.Date is { } untilDate)
            {
                var utcEnd = CalendarTimeZone.ToUtc(untilDate.ToDateTime(new TimeOnly(23, 59, 59)), timeZone);
                rule = string.Join(';', rule.Split(';').Select(part => part.StartsWith("UNTIL=", StringComparison.OrdinalIgnoreCase)
                    ? "UNTIL=" + FormatUtc(utcEnd) : part));
            }
            if (rule.Contains('\r') || rule.Contains('\n'))
                lines.Add("X-MOICALENDAR-RAW-RRULE:" + EscapeText(rule));
            else lines.Add("RRULE:" + rule);
            foreach (var excluded in calendarEvent.ExcludedOccurrenceStartsUtc ?? [])
            {
                lines.Add(calendarEvent.IsAllDay
                    ? "EXDATE;VALUE=DATE:" + TimeZoneInfo.ConvertTime(excluded, timeZone).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                    : "EXDATE:" + FormatUtc(excluded));
            }
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
            return CalendarTimeZone.Resolve(timeZoneId);
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
