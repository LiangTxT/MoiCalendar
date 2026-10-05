using MoiCalendar.App.Components;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class QuickCreateScheduleTests
{
    [Fact]
    public void ArbitraryMinuteInput_PreservesExactTimesWithoutQuarterHourRounding()
    {
        var draft = Draft();
        var fields = new QuickCreateSchedule(draft);
        fields.StartTimeInput = "08:34";
        fields.EndTimeInput = "11:19";
        Assert.Equal(new DateTime(2026, 10, 2, 8, 34, 0), draft.StartLocal);
        Assert.Equal(new DateTime(2026, 10, 2, 11, 19, 0), draft.EndLocal);
        Assert.Equal("持续 2 小时 45 分钟", fields.Summary);
    }

    [Theory]
    [InlineData("23:00")]
    [InlineData("23:00:00")]
    [InlineData("23:00:00.000")]
    public void BrowserNormalizedTime_UpdatesDraftAndKeepsDuration(string value)
    {
        var draft = Draft();
        var fields = new QuickCreateSchedule(draft);

        fields.StartTimeInput = value;
        fields.EndTimeInput = "01:30:00";

        Assert.Equal(new DateTime(2026, 10, 2, 23, 0, 0), draft.StartLocal);
        Assert.Equal(new DateTime(2026, 10, 3, 1, 30, 0), draft.EndLocal);
        Assert.Equal("持续 2 小时 30 分钟 · 次日结束", fields.Summary);
    }

    [Fact]
    public void AllDayEnd_ShowsInclusiveDateButKeepsExclusiveDomainBoundary()
    {
        var draft = Draft();
        draft.SetAllDay(true);
        var fields = new QuickCreateSchedule(draft);

        Assert.Equal("2026-10-02", fields.EndDateInput);
        fields.EndDateInput = "2026-10-04";

        Assert.Equal(new DateTime(2026, 10, 5), draft.EndLocal);
        Assert.Equal("全天 · 共 3 天", fields.Summary);
    }

    [Fact]
    public void ChangingEndTime_PreservesMultiDaySelection()
    {
        var draft = Draft();
        draft.EndLocal = new DateTime(2026, 10, 4, 11, 0, 0);
        var fields = new QuickCreateSchedule(draft);

        fields.EndTimeInput = "12:30";

        Assert.Equal(new DateTime(2026, 10, 4, 12, 30, 0), draft.EndLocal);
        Assert.Contains("2 天后结束", fields.Summary);
    }

    [Fact]
    public void EarlierEndTime_RollsOverAndExplainsNextDay()
    {
        var draft = Draft();
        var fields = new QuickCreateSchedule(draft);

        fields.EndTimeInput = "08:00";

        Assert.Equal(new DateTime(2026, 10, 3, 8, 0, 0), draft.EndLocal);
        Assert.True(fields.CrossesMidnight);
        Assert.Equal("持续 23 小时 · 次日结束", fields.Summary);
    }

    [Fact]
    public void AllDayToggle_RestoresEnteredTimesAndKeepsChangedDateAndTitle()
    {
        var draft = Draft();
        draft.Title = "保留标题";
        var fields = new QuickCreateSchedule(draft);
        fields.StartTimeInput = "14:15";
        fields.EndTimeInput = "15:45";
        fields.SetAllDay(true);
        fields.DateInput = "2026-10-08";

        fields.SetAllDay(false);

        Assert.Equal(new DateTime(2026, 10, 8, 14, 15, 0), draft.StartLocal);
        Assert.Equal(new DateTime(2026, 10, 8, 15, 45, 0), draft.EndLocal);
        Assert.Equal("保留标题", draft.Title);
        Assert.Equal("持续 1 小时 30 分钟", fields.Summary);
    }

    [Theory]
    [InlineData(1, 0, 4)]
    [InlineData(0, 0, 3)]
    public void CrossMidnightToAllDay_IncludesLastDayOnlyIfItContainsTime(int hour, int minute, int exclusiveDay)
    {
        var draft = Draft();
        draft.StartLocal = new DateTime(2026, 10, 2, 23, 0, 0);
        draft.EndLocal = new DateTime(2026, 10, 3, hour, minute, 0);
        var fields = new QuickCreateSchedule(draft);

        fields.SetAllDay(true);

        Assert.Equal(new DateTime(2026, 10, exclusiveDay), draft.EndLocal);
    }

    [Fact]
    public void EditingDateAndStart_PreservesDuration()
    {
        var draft = Draft();
        draft.EndLocal = draft.StartLocal.AddMinutes(90);
        var fields = new QuickCreateSchedule(draft);

        fields.DateInput = "2026-11-01";
        fields.StartTimeInput = "23:15";

        Assert.Equal(new DateTime(2026, 11, 1, 23, 15, 0), draft.StartLocal);
        Assert.Equal(new DateTime(2026, 11, 2, 0, 45, 0), draft.EndLocal);
    }

    [Fact]
    public void InvalidInput_PreservesDraft()
    {
        var draft = Draft();
        var fields = new QuickCreateSchedule(draft);

        fields.DateInput = "";
        fields.StartTimeInput = "25:00";
        fields.EndDateInput = "2026-02-30";

        Assert.Equal(new DateTime(2026, 10, 2, 9, 0, 0), draft.StartLocal);
        Assert.Equal(new DateTime(2026, 10, 2, 10, 0, 0), draft.EndLocal);
    }

    private static CalendarEventDraft Draft() => new()
    {
        StartLocal = new DateTime(2026, 10, 2, 9, 0, 0),
        EndLocal = new DateTime(2026, 10, 2, 10, 0, 0),
        TimeZoneId = TimeZoneInfo.Utc.Id
    };
}
