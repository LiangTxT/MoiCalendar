using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class ICalendarImportParserTests
{
    private readonly ICalendarImportParser parser = new CalendarImportParser();

    [Fact]
    public void Parse_StandardEvent_ReturnsCandidateWithoutSideEffects()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:external-1@example.com
            SUMMARY:项目会议
            DESCRIPTION:第一行\n第二行
            LOCATION:会议室\, A
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            END:VEVENT
            """), "standard.ics");

        var candidate = Assert.Single(result.CandidateEvents);
        Assert.Equal("standard.ics", result.SourceName);
        Assert.Equal(1, result.TotalEventCount);
        Assert.Equal(1, result.ValidEventCount);
        Assert.Equal("external-1@example.com", candidate.ExternalUid);
        Assert.Equal("项目会议", candidate.Title);
        Assert.Equal("第一行\n第二行", candidate.Description);
        Assert.Equal("会议室, A", candidate.Location);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero), candidate.StartUtc);
        Assert.Equal("UTC", candidate.TimeZoneId);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void Parse_AllDayEvent_UsesExclusiveEndDate()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:all-day
            SUMMARY:假期
            DTSTART;VALUE=DATE:20260903
            DTEND;VALUE=DATE:20260905
            END:VEVENT
            """));

        var candidate = Assert.Single(result.CandidateEvents);
        Assert.True(candidate.IsAllDay);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), candidate.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), candidate.EndUtc);
    }

    [Fact]
    public void Parse_RecurringEvent_PreservesSupportedRule()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:recurring
            SUMMARY:训练
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8
            END:VEVENT
            """));

        Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8", Assert.Single(result.CandidateEvents).RecurrenceRule);
    }

    [Fact]
    public void Parse_UnknownProperty_AddsWarningButKeepsEvent()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:unknown-property
            SUMMARY:带附件
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            ATTACH:https://example.com/file
            END:VEVENT
            """));

        Assert.Single(result.CandidateEvents);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("UNSUPPORTED_PROPERTY", warning.Code);
    }

    [Fact]
    public void Parse_UnsupportedRecurrence_AddsWarningAndIgnoresRule()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:complex-rule
            SUMMARY:复杂规则
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            RRULE:FREQ=MONTHLY;BYMONTHDAY=1
            END:VEVENT
            """));

        Assert.Null(Assert.Single(result.CandidateEvents).RecurrenceRule);
        Assert.Contains(result.Warnings, warning => warning.Code == "UNSUPPORTED_RRULE");
    }

    [Fact]
    public void Parse_RecurrenceException_WarnsAndSkipsEventInsteadOfChangingSchedule()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:series-with-exception
            SUMMARY:带例外的重复事件
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            RRULE:FREQ=DAILY;COUNT=5
            EXDATE:20260904T010000Z
            END:VEVENT
            """));

        Assert.Empty(result.CandidateEvents);
        Assert.Contains(result.Warnings, warning => warning.Code == "UNSUPPORTED_RECURRENCE_FEATURE");
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void Parse_MalformedEvent_ReportsErrorAndContinuesWithOtherEvents()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:broken
            SUMMARY:缺少结束时间
            DTSTART:20260903T010000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:valid
            SUMMARY:有效事件
            DTSTART:20260904T010000Z
            DTEND:20260904T020000Z
            END:VEVENT
            """));

        Assert.Equal(2, result.TotalEventCount);
        Assert.Equal(1, result.ValidEventCount);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal("valid", Assert.Single(result.CandidateEvents).ExternalUid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a calendar")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nEND:VCALENDAR")]
    public void Parse_MalformedFile_ThrowsCleanException(string content)
    {
        var exception = Assert.Throws<ICalendarImportException>(() => parser.Parse(content));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void Parse_FoldedUnicodeText_UnfoldsCorrectly()
    {
        var result = parser.Parse("""
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:unicode
            SUMMARY:你好，
             世界 🌏
            DTSTART:20260903T010000Z
            DTEND:20260903T020000Z
            END:VEVENT
            END:VCALENDAR

            """);

        Assert.Equal("你好，世界 🌏", Assert.Single(result.CandidateEvents).Title);
    }

    [Fact]
    public void Parse_FloatingTime_ReturnsDeterministicUtcAndWarning()
    {
        var result = parser.Parse(Calendar("""
            BEGIN:VEVENT
            UID:floating
            SUMMARY:浮动时间
            DTSTART:20260903T090000
            DTEND:20260903T100000
            END:VEVENT
            """));

        var candidate = Assert.Single(result.CandidateEvents);
        Assert.Equal(TimeSpan.Zero, candidate.StartUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero), candidate.StartUtc);
        Assert.Equal(2, result.WarningCount);
    }

    private static string Calendar(string events) => $$"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        {{events}}
        END:VCALENDAR
        """;
}
