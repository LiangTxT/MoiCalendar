using System.Globalization;

namespace MoiCalendar.Core;

public enum CalendarEventRepeatOption
{
    Never,
    Daily,
    Weekly,
    Monthly,
    Yearly,
    Custom
}

public enum RecurrenceEndOption
{
    Never,
    OnDate,
    AfterCount
}

public sealed class CalendarEventRecurrenceDraft
{
    private readonly HashSet<DayOfWeek> selectedWeekdays = [];
    private string? originalRule;
    private string? originalSettingsSignature;

    public CalendarEventRepeatOption RepeatOption { get; set; }

    public RecurrenceFrequency CustomFrequency { get; set; } = RecurrenceFrequency.Weekly;

    public int Interval { get; set; } = 1;

    public RecurrenceEndOption EndOption { get; set; }

    public DateOnly? UntilDate { get; set; }

    public int OccurrenceCount { get; set; } = 10;

    public IReadOnlySet<DayOfWeek> SelectedWeekdays => selectedWeekdays;

    public string UntilDateInput
    {
        get => UntilDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        set => UntilDate = DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
                ? date
                : null;
    }

    public bool IsWeekdaySelected(DayOfWeek weekday) => selectedWeekdays.Contains(weekday);

    public void SetWeekdaySelected(DayOfWeek weekday, bool selected)
    {
        if (selected)
        {
            selectedWeekdays.Add(weekday);
        }
        else
        {
            selectedWeekdays.Remove(weekday);
        }
    }

    public string? ToRecurrenceRule(DateTime startLocal)
    {
        if (RepeatOption == CalendarEventRepeatOption.Never)
        {
            return null;
        }

        var frequency = RepeatOption switch
        {
            CalendarEventRepeatOption.Daily => RecurrenceFrequency.Daily,
            CalendarEventRepeatOption.Weekly => RecurrenceFrequency.Weekly,
            CalendarEventRepeatOption.Monthly => RecurrenceFrequency.Monthly,
            CalendarEventRepeatOption.Yearly => RecurrenceFrequency.Yearly,
            CalendarEventRepeatOption.Custom => CustomFrequency,
            _ => throw new ArgumentOutOfRangeException(nameof(RepeatOption), "重复选项无效。")
        };

        if (RepeatOption != CalendarEventRepeatOption.Custom)
        {
            return PreserveOriginalRuleWhenUnchanged($"FREQ={ToRRuleFrequency(frequency)}");
        }

        if (Interval <= 0)
        {
            throw new ArgumentException("重复间隔必须大于 0。");
        }

        var parts = new List<string> { $"FREQ={ToRRuleFrequency(frequency)}" };
        if (Interval != 1)
        {
            parts.Add($"INTERVAL={Interval.ToString(CultureInfo.InvariantCulture)}");
        }

        if (frequency == RecurrenceFrequency.Weekly)
        {
            if (selectedWeekdays.Count == 0)
            {
                throw new ArgumentException("每周重复至少需要选择一个星期。");
            }

            parts.Add("BYDAY=" + string.Join(
                ',',
                selectedWeekdays
                    .OrderBy(RecurrenceRuleParser.ToMondayBasedIndex)
                    .Select(ToRRuleWeekday)));
        }

        switch (EndOption)
        {
            case RecurrenceEndOption.Never:
                break;
            case RecurrenceEndOption.OnDate:
                if (UntilDate is not { } untilDate)
                {
                    throw new ArgumentException("请选择重复结束日期。");
                }

                if (untilDate < DateOnly.FromDateTime(startLocal))
                {
                    throw new ArgumentException("重复结束日期不能早于事件开始日期。");
                }

                parts.Add($"UNTIL={untilDate:yyyyMMdd}");
                break;
            case RecurrenceEndOption.AfterCount:
                if (OccurrenceCount <= 0)
                {
                    throw new ArgumentException("重复次数必须大于 0。");
                }

                parts.Add($"COUNT={OccurrenceCount.ToString(CultureInfo.InvariantCulture)}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(EndOption), "重复结束选项无效。");
        }

        return PreserveOriginalRuleWhenUnchanged(string.Join(';', parts));
    }

    internal static CalendarEventRecurrenceDraft FromRule(
        string? recurrenceRule,
        DateTime startLocal,
        TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(recurrenceRule))
        {
            return new CalendarEventRecurrenceDraft();
        }

        var parsed = RecurrenceRuleParser.Parse(recurrenceRule);
        var isSimple = parsed.Interval == 1 &&
                       parsed.Count is null &&
                       parsed.Until is null &&
                       parsed.ByDay.Count == 0;
        var draft = new CalendarEventRecurrenceDraft
        {
            RepeatOption = isSimple ? ToRepeatOption(parsed.Frequency) : CalendarEventRepeatOption.Custom,
            CustomFrequency = parsed.Frequency,
            Interval = parsed.Interval,
            EndOption = parsed.Count is not null
                ? RecurrenceEndOption.AfterCount
                : parsed.Until is not null
                    ? RecurrenceEndOption.OnDate
                    : RecurrenceEndOption.Never,
            OccurrenceCount = parsed.Count ?? 10,
            UntilDate = ToUntilDate(parsed.Until, startLocal, timeZone)
        };

        foreach (var weekday in parsed.ByDay)
        {
            draft.SetWeekdaySelected(weekday, true);
        }

        if (parsed.Frequency == RecurrenceFrequency.Weekly && draft.selectedWeekdays.Count == 0)
        {
            draft.SetWeekdaySelected(startLocal.DayOfWeek, true);
        }

        draft.originalRule = recurrenceRule;
        draft.originalSettingsSignature = draft.GetSettingsSignature();

        return draft;
    }

    private string PreserveOriginalRuleWhenUnchanged(string generatedRule) =>
        originalRule is not null && originalSettingsSignature == GetSettingsSignature()
            ? originalRule
            : generatedRule;

    private string GetSettingsSignature() => string.Join(
        '|',
        RepeatOption,
        CustomFrequency,
        Interval.ToString(CultureInfo.InvariantCulture),
        EndOption,
        UntilDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
        OccurrenceCount.ToString(CultureInfo.InvariantCulture),
        string.Join(',', selectedWeekdays.OrderBy(RecurrenceRuleParser.ToMondayBasedIndex)));

    private static DateOnly? ToUntilDate(
        RecurrenceUntil? until,
        DateTime startLocal,
        TimeZoneInfo timeZone)
    {
        if (until?.Date is { } date)
        {
            return date;
        }

        return until?.Utc is { } utc
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, timeZone).DateTime)
            : DateOnly.FromDateTime(startLocal);
    }

    private static CalendarEventRepeatOption ToRepeatOption(RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Daily => CalendarEventRepeatOption.Daily,
        RecurrenceFrequency.Weekly => CalendarEventRepeatOption.Weekly,
        RecurrenceFrequency.Monthly => CalendarEventRepeatOption.Monthly,
        RecurrenceFrequency.Yearly => CalendarEventRepeatOption.Yearly,
        _ => throw new ArgumentOutOfRangeException(nameof(frequency))
    };

    private static string ToRRuleFrequency(RecurrenceFrequency frequency) => frequency switch
    {
        RecurrenceFrequency.Daily => "DAILY",
        RecurrenceFrequency.Weekly => "WEEKLY",
        RecurrenceFrequency.Monthly => "MONTHLY",
        RecurrenceFrequency.Yearly => "YEARLY",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency))
    };

    private static string ToRRuleWeekday(DayOfWeek weekday) => weekday switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        DayOfWeek.Sunday => "SU",
        _ => throw new ArgumentOutOfRangeException(nameof(weekday))
    };
}
