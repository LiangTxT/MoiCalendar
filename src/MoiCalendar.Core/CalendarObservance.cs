using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MoiCalendar.Core;

public enum CalendarObservanceKind
{
    Holiday,
    SolarTerm
}

public sealed record CalendarObservance(
    Guid Id,
    DateOnly Date,
    string Title,
    CalendarObservanceKind Kind);

public interface ICalendarObservanceProvider
{
    IReadOnlyList<CalendarObservance> GetObservances(DateOnly startDate, DateOnly endDateExclusive);

    string GetLunarLabel(DateOnly date);
}

public sealed class EmptyCalendarObservanceProvider : ICalendarObservanceProvider
{
    public static EmptyCalendarObservanceProvider Instance { get; } = new();

    public IReadOnlyList<CalendarObservance> GetObservances(DateOnly startDate, DateOnly endDateExclusive) =>
        Array.Empty<CalendarObservance>();

    public string GetLunarLabel(DateOnly date) => string.Empty;
}

public sealed class ChineseCalendarObservanceProvider : ICalendarObservanceProvider
{
    private static readonly ChineseLunisolarCalendar LunarCalendar = new();
    private static readonly string[] LunarDays =
    [
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    ];
    private static readonly string[] LunarMonths =
        ["", "正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];
    private static readonly (int Month, string Name, double Constant)[] SolarTerms =
    [
        (1, "小寒", 5.4055), (1, "大寒", 20.12), (2, "立春", 3.87), (2, "雨水", 18.73),
        (3, "惊蛰", 5.63), (3, "春分", 20.646), (4, "清明", 4.81), (4, "谷雨", 20.1),
        (5, "立夏", 5.52), (5, "小满", 21.04), (6, "芒种", 5.678), (6, "夏至", 21.37),
        (7, "小暑", 7.108), (7, "大暑", 22.83), (8, "立秋", 7.5), (8, "处暑", 23.13),
        (9, "白露", 7.646), (9, "秋分", 23.042), (10, "寒露", 8.318), (10, "霜降", 23.438),
        (11, "立冬", 7.438), (11, "小雪", 22.36), (12, "大雪", 7.18), (12, "冬至", 21.94)
    ];

    public IReadOnlyList<CalendarObservance> GetObservances(DateOnly startDate, DateOnly endDateExclusive)
    {
        if (endDateExclusive <= startDate)
        {
            throw new ArgumentException("结束日期必须晚于开始日期。", nameof(endDateExclusive));
        }

        var result = new List<CalendarObservance>();
        for (var date = startDate; date < endDateExclusive; date = date.AddDays(1))
        {
            AddFixedHoliday(date, result);
            AddLunarHoliday(date, result);
            AddSolarTerm(date, result);
        }

        return result;
    }

    public string GetLunarLabel(DateOnly date)
    {
        if (!IsLunarDateSupported(date))
        {
            return string.Empty;
        }

        var lunar = GetLunarDate(date);
        return lunar.Day == 1 ? LunarMonths[lunar.Month] : LunarDays[lunar.Day];
    }

    private static void AddFixedHoliday(DateOnly date, ICollection<CalendarObservance> result)
    {
        var title = (date.Month, date.Day) switch
        {
            (1, 1) => "元旦",
            (5, 1) => "劳动节",
            (10, 1) => "国庆节",
            _ => null
        };
        if (title is not null)
        {
            result.Add(Create(date, title, CalendarObservanceKind.Holiday));
        }
    }

    private static void AddLunarHoliday(DateOnly date, ICollection<CalendarObservance> result)
    {
        if (!IsLunarDateSupported(date))
        {
            return;
        }

        var lunar = GetLunarDate(date);
        if (lunar.IsLeapMonth)
        {
            return;
        }

        var title = (lunar.Month, lunar.Day) switch
        {
            (1, 1) => "春节",
            (1, 15) => "元宵节",
            (5, 5) => "端午节",
            (8, 15) => "中秋节",
            _ => null
        };
        if (title is not null)
        {
            result.Add(Create(date, title, CalendarObservanceKind.Holiday));
        }
    }

    private static void AddSolarTerm(DateOnly date, ICollection<CalendarObservance> result)
    {
        if (date.Year is < 2001 or > 2099)
        {
            return;
        }

        var year = date.Year % 100;
        foreach (var term in SolarTerms.Where(item => item.Month == date.Month))
        {
            var day = (int)Math.Floor(year * 0.2422 + term.Constant) - (int)Math.Floor((year - 1) / 4d);
            if (date.Day == day)
            {
                result.Add(Create(date, term.Name, CalendarObservanceKind.SolarTerm));
            }
        }
    }

    private static (int Month, int Day, bool IsLeapMonth) GetLunarDate(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        var lunarYear = LunarCalendar.GetYear(value);
        var month = LunarCalendar.GetMonth(value);
        var leapMonth = LunarCalendar.GetLeapMonth(lunarYear);
        var isLeapMonth = leapMonth > 0 && month == leapMonth;
        if (leapMonth > 0 && month >= leapMonth)
        {
            month--;
        }
        return (month, LunarCalendar.GetDayOfMonth(value), isLeapMonth);
    }

    private static bool IsLunarDateSupported(DateOnly date)
    {
        var value = date.ToDateTime(TimeOnly.MinValue);
        return value >= LunarCalendar.MinSupportedDateTime && value <= LunarCalendar.MaxSupportedDateTime;
    }

    private static CalendarObservance Create(
        DateOnly date,
        string title,
        CalendarObservanceKind kind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"moi-observance:{date:yyyy-MM-dd}:{title}"));
        return new CalendarObservance(new Guid(hash.AsSpan(0, 16)), date, title, kind);
    }
}
