using System.Globalization;

namespace MoiCalendar.Core;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly
}

public sealed record RecurrenceUntil(DateOnly? Date, DateTimeOffset? Utc)
{
    public bool Includes(DateTime localStart, DateTimeOffset utcStart) =>
        Date is { } date
            ? DateOnly.FromDateTime(localStart) <= date
            : Utc is { } utc && utcStart <= utc;
}

public sealed record ParsedRecurrenceRule(
    RecurrenceFrequency Frequency,
    int Interval,
    int? Count,
    RecurrenceUntil? Until,
    IReadOnlyList<DayOfWeek> ByDay);

public static class RecurrenceRuleParser
{
    private static readonly IReadOnlyDictionary<string, RecurrenceFrequency> Frequencies =
        new Dictionary<string, RecurrenceFrequency>(StringComparer.OrdinalIgnoreCase)
        {
            ["DAILY"] = RecurrenceFrequency.Daily,
            ["WEEKLY"] = RecurrenceFrequency.Weekly,
            ["MONTHLY"] = RecurrenceFrequency.Monthly,
            ["YEARLY"] = RecurrenceFrequency.Yearly
        };

    private static readonly IReadOnlyDictionary<string, DayOfWeek> Weekdays =
        new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["MO"] = DayOfWeek.Monday,
            ["TU"] = DayOfWeek.Tuesday,
            ["WE"] = DayOfWeek.Wednesday,
            ["TH"] = DayOfWeek.Thursday,
            ["FR"] = DayOfWeek.Friday,
            ["SA"] = DayOfWeek.Saturday,
            ["SU"] = DayOfWeek.Sunday
        };

    private static readonly HashSet<string> SupportedParts =
        new(["FREQ", "INTERVAL", "COUNT", "UNTIL", "BYDAY"], StringComparer.OrdinalIgnoreCase);

    public static ParsedRecurrenceRule Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RecurrenceRuleException("重复规则不能为空。");
        }

        var text = value.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..];
        }

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split(';', StringSplitOptions.None))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || separator == segment.Length - 1)
            {
                throw new RecurrenceRuleException("重复规则包含格式无效的组成部分。");
            }

            var name = segment[..separator].Trim();
            var partValue = segment[(separator + 1)..].Trim();
            if (!SupportedParts.Contains(name))
            {
                throw new RecurrenceRuleException($"暂不支持重复规则组成部分 {name}。");
            }

            if (!parts.TryAdd(name, partValue))
            {
                throw new RecurrenceRuleException($"重复规则组成部分 {name} 不得重复。");
            }
        }

        if (!parts.TryGetValue("FREQ", out var frequencyText) ||
            !Frequencies.TryGetValue(frequencyText, out var frequency))
        {
            throw new RecurrenceRuleException("重复规则缺少受支持的 FREQ。");
        }

        var interval = ReadPositiveInteger(parts, "INTERVAL") ?? 1;
        var count = ReadPositiveInteger(parts, "COUNT");
        var until = parts.TryGetValue("UNTIL", out var untilText) ? ParseUntil(untilText) : null;
        var byDay = parts.TryGetValue("BYDAY", out var byDayText)
            ? ParseByDay(byDayText)
            : Array.Empty<DayOfWeek>();

        if (byDay.Count > 0 && frequency != RecurrenceFrequency.Weekly)
        {
            throw new RecurrenceRuleException("当前版本仅支持 WEEKLY 规则使用 BYDAY。");
        }

        return new ParsedRecurrenceRule(frequency, interval, count, until, byDay);
    }

    private static int? ReadPositiveInteger(IReadOnlyDictionary<string, string> parts, string name)
    {
        if (!parts.TryGetValue(name, out var text))
        {
            return null;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new RecurrenceRuleException($"重复规则 {name} 必须是正整数。");
        }

        return value;
    }

    private static RecurrenceUntil ParseUntil(string text)
    {
        if (DateTime.TryParseExact(
                text,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return new RecurrenceUntil(DateOnly.FromDateTime(date), null);
        }

        if (DateTimeOffset.TryParseExact(
                text,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utc))
        {
            return new RecurrenceUntil(null, utc);
        }

        throw new RecurrenceRuleException("UNTIL 必须是 yyyyMMdd 或 UTC 的 yyyyMMdd'T'HHmmss'Z'。");
    }

    private static IReadOnlyList<DayOfWeek> ParseByDay(string text)
    {
        var weekdays = new HashSet<DayOfWeek>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Weekdays.TryGetValue(token, out var weekday) || !weekdays.Add(weekday))
            {
                throw new RecurrenceRuleException("BYDAY 只能包含不重复的 MO、TU、WE、TH、FR、SA、SU。");
            }
        }

        if (weekdays.Count == 0)
        {
            throw new RecurrenceRuleException("BYDAY 不能为空。");
        }

        return weekdays.OrderBy(ToMondayBasedIndex).ToArray();
    }

    internal static int ToMondayBasedIndex(DayOfWeek day) => ((int)day + 6) % 7;
}

public sealed class RecurrenceRuleException : Exception
{
    public RecurrenceRuleException(string message)
        : base(message)
    {
    }
}
