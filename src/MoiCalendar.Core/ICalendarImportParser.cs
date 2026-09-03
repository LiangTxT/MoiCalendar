using System.Globalization;
using System.Text;

namespace MoiCalendar.Core;

public enum ICalendarImportMessageSeverity
{
    Warning,
    Error
}

public sealed record ICalendarImportMessage(
    ICalendarImportMessageSeverity Severity,
    string Code,
    string Message,
    int? EventNumber = null);

public sealed record ICalendarImportCandidate(
    int SourceEventNumber,
    string ExternalUid,
    string Title,
    string Description,
    string Location,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string TimeZoneId,
    bool IsAllDay,
    string? RecurrenceRule);

public sealed record ICalendarImportResult(
    string? SourceName,
    string? CalendarName,
    int TotalEventCount,
    IReadOnlyList<ICalendarImportCandidate> CandidateEvents,
    IReadOnlyList<ICalendarImportMessage> Messages)
{
    public int ValidEventCount => CandidateEvents.Count;

    public int WarningCount => Messages.Count(message => message.Severity == ICalendarImportMessageSeverity.Warning);

    public int ErrorCount => Messages.Count(message => message.Severity == ICalendarImportMessageSeverity.Error);

    public IReadOnlyList<ICalendarImportMessage> Warnings =>
        Messages.Where(message => message.Severity == ICalendarImportMessageSeverity.Warning).ToArray();

    public IReadOnlyList<ICalendarImportMessage> Errors =>
        Messages.Where(message => message.Severity == ICalendarImportMessageSeverity.Error).ToArray();
}

public interface ICalendarImportParser
{
    ICalendarImportResult Parse(string content, string? sourceName = null);
}

public sealed class CalendarImportParser : ICalendarImportParser
{
    private const int MaxContentLength = 10 * 1024 * 1024;
    private const int MaxEventCount = 100_000;
    private const int MaxPropertyLength = 128 * 1024;

    private static readonly HashSet<string> SilentlyIgnoredEventProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "BEGIN", "END", "DTSTAMP", "SEQUENCE", "STATUS", "TRANSP", "CLASS", "CATEGORIES"
    };

    public ICalendarImportResult Parse(string content, string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ICalendarImportException("所选文件为空，不是有效的 iCalendar 文件。");
        }

        if (content.Length > MaxContentLength)
        {
            throw new ICalendarImportException("iCalendar 文件超过 10 MB，当前版本无法安全解析。");
        }

        if (content.IndexOf('\0') >= 0)
        {
            throw new ICalendarImportException("iCalendar 文件包含无效字符。");
        }

        var lines = UnfoldLines(content);
        if (lines.Count < 2 ||
            !lines[0].Equals("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) ||
            !lines[^1].Equals("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new ICalendarImportException("文件缺少完整的 VCALENDAR 开始或结束标记。");
        }

        var messages = new List<ICalendarImportMessage>();
        var candidates = new List<ICalendarImportCandidate>();
        var currentEvent = new List<ParsedProperty>();
        var insideEvent = false;
        var totalEventCount = 0;
        string? calendarName = null;

        for (var lineIndex = 1; lineIndex < lines.Count - 1; lineIndex++)
        {
            var property = ParseProperty(lines[lineIndex], lineIndex + 1);
            if (property.Name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) &&
                property.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (insideEvent)
                {
                    throw new ICalendarImportException("文件包含嵌套的 VEVENT，结构无效。");
                }

                insideEvent = true;
                currentEvent.Clear();
                totalEventCount++;
                if (totalEventCount > MaxEventCount)
                {
                    throw new ICalendarImportException("文件中的事件数量过多，当前版本无法安全解析。");
                }

                continue;
            }

            if (property.Name.Equals("END", StringComparison.OrdinalIgnoreCase) &&
                property.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (!insideEvent)
                {
                    throw new ICalendarImportException("文件包含未配对的 VEVENT 结束标记。");
                }

                TryCreateCandidate(currentEvent, totalEventCount, candidates, messages);
                insideEvent = false;
                currentEvent.Clear();
                continue;
            }

            if (insideEvent)
            {
                currentEvent.Add(property);
            }
            else if (property.Name.Equals("X-WR-CALNAME", StringComparison.OrdinalIgnoreCase))
            {
                calendarName = UnescapeText(property.Value);
            }
            else if (property.Name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                throw new ICalendarImportException("文件包含当前版本不支持的嵌套日历组件。");
            }
        }

        if (insideEvent)
        {
            throw new ICalendarImportException("文件包含未结束的 VEVENT。");
        }

        var seenUids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (seenUids.Add(candidate.ExternalUid))
            {
                continue;
            }

            candidates.RemoveAt(index);
            index--;
            messages.Add(new ICalendarImportMessage(
                ICalendarImportMessageSeverity.Error,
                "DUPLICATE_SOURCE_UID",
                $"事件 {candidate.SourceEventNumber} 与文件中的另一事件使用相同 UID，已跳过以避免重复导入。",
                candidate.SourceEventNumber));
        }

        return new ICalendarImportResult(sourceName, calendarName, totalEventCount, candidates, messages);
    }

    private static void TryCreateCandidate(
        IReadOnlyList<ParsedProperty> properties,
        int eventNumber,
        ICollection<ICalendarImportCandidate> candidates,
        ICollection<ICalendarImportMessage> messages)
    {
        try
        {
            var uid = ReadSingle(properties, "UID", required: true, eventNumber);
            var summary = ReadSingle(properties, "SUMMARY", required: false, eventNumber);
            var description = ReadSingle(properties, "DESCRIPTION", required: false, eventNumber);
            var location = ReadSingle(properties, "LOCATION", required: false, eventNumber);
            var startProperty = ReadSingleProperty(properties, "DTSTART", required: true, eventNumber)!;
            var endProperty = ReadSingleProperty(properties, "DTEND", required: true, eventNumber)!;

            RejectUnsupportedRecurrenceFeatures(properties, eventNumber, messages);

            var title = summary is null ? "（无标题）" : UnescapeText(summary);
            var decodedDescription = description is null ? string.Empty : UnescapeText(description);
            var decodedLocation = location is null ? string.Empty : UnescapeText(location);
            ValidateTextLengths(uid!, title, decodedDescription, decodedLocation, eventNumber);

            var start = ParseDateTime(startProperty, eventNumber, messages);
            var end = ParseDateTime(endProperty, eventNumber, messages);
            if (start.IsAllDay != end.IsAllDay)
            {
                throw InvalidEvent(eventNumber, "DTSTART 与 DTEND 必须同时是全天日期或定时时间。");
            }

            if (end.Utc <= start.Utc)
            {
                throw InvalidEvent(eventNumber, "DTEND 必须晚于 DTSTART。");
            }

            var recurrenceRule = ReadRecurrenceRule(properties, eventNumber, messages);
            AddUnsupportedPropertyWarnings(properties, eventNumber, messages);

            candidates.Add(new ICalendarImportCandidate(
                eventNumber,
                uid!,
                title,
                decodedDescription,
                decodedLocation,
                start.Utc,
                end.Utc,
                start.TimeZoneId,
                start.IsAllDay,
                recurrenceRule));
        }
        catch (ICalendarEventImportException exception)
        {
            messages.Add(new ICalendarImportMessage(
                ICalendarImportMessageSeverity.Error,
                "INVALID_EVENT",
                exception.Message,
                eventNumber));
        }
    }

    private static string? ReadRecurrenceRule(
        IReadOnlyList<ParsedProperty> properties,
        int eventNumber,
        ICollection<ICalendarImportMessage> messages)
    {
        var value = ReadSingle(properties, "RRULE", required: false, eventNumber);
        if (value is null)
        {
            return null;
        }

        try
        {
            _ = RecurrenceRuleParser.Parse(value);
            return value.Trim();
        }
        catch (RecurrenceRuleException exception)
        {
            messages.Add(new ICalendarImportMessage(
                ICalendarImportMessageSeverity.Warning,
                "UNSUPPORTED_RRULE",
                $"事件 {eventNumber} 的重复规则不受支持，导入时将忽略该规则：{exception.Message}",
                eventNumber));
            return null;
        }
    }

    private static void RejectUnsupportedRecurrenceFeatures(
        IReadOnlyList<ParsedProperty> properties,
        int eventNumber,
        ICollection<ICalendarImportMessage> messages)
    {
        var unsupported = properties
            .Select(property => property.Name)
            .Where(name => name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("RDATE", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length == 0)
        {
            return;
        }

        messages.Add(new ICalendarImportMessage(
            ICalendarImportMessageSeverity.Warning,
            "UNSUPPORTED_RECURRENCE_FEATURE",
            $"事件 {eventNumber} 使用了当前版本不支持的重复例外属性 {string.Join("、", unsupported)}，为避免改变原日程，该事件不会导入。",
            eventNumber));
        throw InvalidEvent(eventNumber, "包含当前版本无法安全保留的重复例外。");
    }

    private static ParsedDateTime ParseDateTime(
        ParsedProperty property,
        int eventNumber,
        ICollection<ICalendarImportMessage> messages)
    {
        var valueType = property.Parameters.GetValueOrDefault("VALUE");
        var timeZoneId = property.Parameters.GetValueOrDefault("TZID");
        if (valueType?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true ||
            (valueType is null && property.Value.Length == 8))
        {
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                throw InvalidEvent(eventNumber, $"{property.Name} 的全天日期不能包含 TZID。");
            }

            if (!DateTime.TryParseExact(
                    property.Value,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                throw InvalidEvent(eventNumber, $"{property.Name} 包含无效的全天日期。");
            }

            return new ParsedDateTime(new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)), "UTC", true);
        }

        if (property.Value.EndsWith('Z'))
        {
            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                throw InvalidEvent(eventNumber, $"{property.Name} 的 UTC 时间不能同时包含 TZID。");
            }

            if (!DateTimeOffset.TryParseExact(
                    property.Value,
                    "yyyyMMdd'T'HHmmss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utc))
            {
                throw InvalidEvent(eventNumber, $"{property.Name} 包含无效的 UTC 时间。");
            }

            return new ParsedDateTime(utc, "UTC", false);
        }

        if (!DateTime.TryParseExact(
                property.Value,
                "yyyyMMdd'T'HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            throw InvalidEvent(eventNumber, $"{property.Name} 包含当前版本不支持的日期时间格式。");
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            messages.Add(new ICalendarImportMessage(
                ICalendarImportMessageSeverity.Warning,
                "FLOATING_TIME_AS_UTC",
                $"事件 {eventNumber} 的浮动时间未指定时区，当前版本将其按 UTC 处理。",
                eventNumber));
            return new ParsedDateTime(new DateTimeOffset(local, TimeSpan.Zero), "UTC", false);
        }

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(Unquote(timeZoneId));
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw InvalidEvent(eventNumber, $"{property.Name} 包含当前设备无法识别的时区 {timeZoneId}。");
        }

        if (timeZone.IsInvalidTime(local))
        {
            throw InvalidEvent(eventNumber, $"{property.Name} 落在时区切换导致的无效本地时间内。");
        }

        var utcValue = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new ParsedDateTime(new DateTimeOffset(utcValue), timeZone.Id, false);
    }

    private static ParsedProperty? ReadSingleProperty(
        IReadOnlyList<ParsedProperty> properties,
        string name,
        bool required,
        int eventNumber)
    {
        var matches = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
        {
            throw InvalidEvent(eventNumber, $"{name} 不得重复。");
        }

        if (matches.Length == 0)
        {
            if (required)
            {
                throw InvalidEvent(eventNumber, $"缺少必需字段 {name}。");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(matches[0].Value))
        {
            throw InvalidEvent(eventNumber, $"{name} 不能为空。");
        }

        return matches[0];
    }

    private static string? ReadSingle(
        IReadOnlyList<ParsedProperty> properties,
        string name,
        bool required,
        int eventNumber) => ReadSingleProperty(properties, name, required, eventNumber)?.Value;

    private static void ValidateTextLengths(
        string uid,
        string title,
        string description,
        string location,
        int eventNumber)
    {
        if (uid.Length > 1_024)
        {
            throw InvalidEvent(eventNumber, "UID 过长。");
        }

        if (title.Length > 200 || description.Length > 4_000 || location.Length > 300)
        {
            throw InvalidEvent(eventNumber, "事件文本超过 MoiCalendar 支持的长度限制。");
        }
    }

    private static void AddUnsupportedPropertyWarnings(
        IEnumerable<ParsedProperty> properties,
        int eventNumber,
        ICollection<ICalendarImportMessage> messages)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UID", "SUMMARY", "DESCRIPTION", "LOCATION", "DTSTART", "DTEND", "RRULE"
        };
        foreach (var name in properties
                     .Select(property => property.Name)
                     .Where(name => !supported.Contains(name) && !SilentlyIgnoredEventProperties.Contains(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            messages.Add(new ICalendarImportMessage(
                ICalendarImportMessageSeverity.Warning,
                "UNSUPPORTED_PROPERTY",
                $"事件 {eventNumber} 的属性 {name} 当前不会导入。",
                eventNumber));
        }
    }

    private static List<string> UnfoldLines(string content)
    {
        var normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var physicalLines = normalized.Split('\n');
        var logicalLines = new List<string>(physicalLines.Length);
        foreach (var physicalLine in physicalLines)
        {
            if ((physicalLine.StartsWith(' ') || physicalLine.StartsWith('\t')) && logicalLines.Count > 0)
            {
                logicalLines[^1] += physicalLine[1..];
            }
            else
            {
                logicalLines.Add(physicalLine);
            }
        }

        while (logicalLines.Count > 0 && logicalLines[^1].Length == 0)
        {
            logicalLines.RemoveAt(logicalLines.Count - 1);
        }

        return logicalLines;
    }

    private static ParsedProperty ParseProperty(string line, int lineNumber)
    {
        if (line.Length > MaxPropertyLength)
        {
            throw new ICalendarImportException($"第 {lineNumber} 行过长，无法安全解析。");
        }

        var separator = FindUnquotedSeparator(line, ':');
        if (separator <= 0)
        {
            throw new ICalendarImportException($"第 {lineNumber} 行不是有效的 iCalendar 属性。");
        }

        var segments = SplitUnquoted(line[..separator], ';');
        var name = segments[0].Trim();
        if (name.Length == 0)
        {
            throw new ICalendarImportException($"第 {lineNumber} 行缺少属性名称。");
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments.Skip(1))
        {
            var equals = FindUnquotedSeparator(segment, '=');
            if (equals <= 0 || equals == segment.Length - 1)
            {
                throw new ICalendarImportException($"第 {lineNumber} 行包含无效参数。");
            }

            if (!parameters.TryAdd(segment[..equals].Trim(), Unquote(segment[(equals + 1)..].Trim())))
            {
                throw new ICalendarImportException($"第 {lineNumber} 行包含重复参数。");
            }
        }

        return new ParsedProperty(name, parameters, line[(separator + 1)..]);
    }

    private static int FindUnquotedSeparator(string value, char separator)
    {
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && value[index] == separator)
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] SplitUnquoted(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && value[index] == separator)
            {
                parts.Add(value[start..index]);
                start = index + 1;
            }
        }

        if (quoted)
        {
            throw new ICalendarImportException("iCalendar 属性包含未结束的引号。");
        }

        parts.Add(value[start..]);
        return parts.ToArray();
    }

    private static string UnescapeText(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index == value.Length - 1)
            {
                result.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            result.Append(escaped switch
            {
                'n' or 'N' => '\n',
                '\\' => '\\',
                ',' => ',',
                ';' => ';',
                _ => escaped
            });
        }

        return result.ToString();
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static ICalendarEventImportException InvalidEvent(int eventNumber, string message) =>
        new($"事件 {eventNumber} 无效：{message}");

    private sealed record ParsedProperty(
        string Name,
        IReadOnlyDictionary<string, string> Parameters,
        string Value);

    private sealed record ParsedDateTime(DateTimeOffset Utc, string TimeZoneId, bool IsAllDay);
}

public sealed class ICalendarImportException : Exception
{
    public ICalendarImportException(string message)
        : base(message)
    {
    }
}

internal sealed class ICalendarEventImportException : Exception
{
    public ICalendarEventImportException(string message)
        : base(message)
    {
    }
}
