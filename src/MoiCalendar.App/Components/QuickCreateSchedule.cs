using System.Globalization;
using MoiCalendar.Core;

namespace MoiCalendar.App.Components;

/// <summary>快速新建表单的日期显示适配；全天结束日期在 UI 中包含当天。</summary>
public sealed class QuickCreateSchedule(CalendarEventDraft draft)
{
    private TimeOnly timedStart = draft.IsAllDay ? new TimeOnly(9, 0) : TimeOnly.FromDateTime(draft.StartLocal);
    private TimeSpan timedDuration = draft.IsAllDay ? TimeSpan.FromHours(1) : draft.EndLocal - draft.StartLocal;

    public string DateInput
    {
        get => FormatDate(draft.StartLocal);
        set
        {
            if (!TryDate(value, out var date)) return;
            var duration = draft.EndLocal - draft.StartLocal;
            draft.StartLocal = date.ToDateTime(TimeOnly.FromDateTime(draft.StartLocal));
            draft.EndLocal = draft.StartLocal + duration;
        }
    }

    public string EndDateInput
    {
        get => FormatDate(draft.IsAllDay ? draft.EndLocal.AddDays(-1) : draft.EndLocal);
        set
        {
            if (!TryDate(value, out var date)) return;
            draft.EndLocal = draft.IsAllDay
                ? date.AddDays(1).ToDateTime(TimeOnly.MinValue)
                : date.ToDateTime(TimeOnly.FromDateTime(draft.EndLocal));
        }
    }

    public string StartTimeInput
    {
        get => draft.StartLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            if (!TryTime(value, out var time)) return;
            var duration = draft.EndLocal - draft.StartLocal;
            draft.StartLocal = DateOnly.FromDateTime(draft.StartLocal).ToDateTime(time);
            draft.EndLocal = draft.StartLocal + duration;
        }
    }

    public string EndTimeInput
    {
        get => draft.EndLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            if (!TryTime(value, out var time)) return;
            // 保留范围选择或用户指定的结束日期，避免跨日草稿被压缩到一天。
            var end = DateOnly.FromDateTime(draft.EndLocal).ToDateTime(time);
            draft.EndLocal = end <= draft.StartLocal ? end.AddDays(1) : end;
        }
    }

    public bool CrossesMidnight => !draft.IsAllDay && draft.EndLocal.Date > draft.StartLocal.Date;

    public string Summary
    {
        get
        {
            var duration = draft.EndLocal - draft.StartLocal;
            if (duration <= TimeSpan.Zero) return "结束时间需要晚于开始时间";
            if (draft.IsAllDay) return $"全天 · 共 {duration.Days} 天";
            var hours = (int)duration.TotalHours;
            var parts = hours == 0 ? $"{duration.Minutes} 分钟"
                : duration.Minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {duration.Minutes} 分钟";
            var days = (draft.EndLocal.Date - draft.StartLocal.Date).Days;
            return $"持续 {parts}" + (days == 0 ? string.Empty : days == 1 ? " · 次日结束" : $" · {days} 天后结束");
        }
    }

    public void SetAllDay(bool isAllDay)
    {
        if (draft.IsAllDay == isAllDay) return;
        if (isAllDay)
        {
            timedStart = TimeOnly.FromDateTime(draft.StartLocal);
            timedDuration = draft.EndLocal - draft.StartLocal;
            var endDate = draft.EndLocal.Date;
            // 定时跨日事件末日只要包含时间，就应计入全天范围。
            if (draft.EndLocal.TimeOfDay != TimeSpan.Zero) endDate = endDate.AddDays(1);
            draft.SetAllDay(true);
            draft.EndLocal = endDate > draft.StartLocal ? endDate : draft.StartLocal.AddDays(1);
        }
        else
        {
            draft.SetAllDay(false);
            draft.StartLocal = DateOnly.FromDateTime(draft.StartLocal).ToDateTime(timedStart);
            draft.EndLocal = draft.StartLocal + (timedDuration > TimeSpan.Zero ? timedDuration : TimeSpan.FromHours(1));
        }
    }

    private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool TryDate(string value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    // Blazor 的 input/change 事件会将 HTML time 值规范化为含秒格式。
    private static bool TryTime(string value, out TimeOnly time) => TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
