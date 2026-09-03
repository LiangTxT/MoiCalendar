using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class RecurrenceExpansionServiceTests
{
    private readonly RecurrenceExpansionService service = new();

    [Fact]
    public void Daily_ExpandsOnlyRequestedRange()
    {
        var occurrences = Expand("FREQ=DAILY", Utc(2026, 1, 3), Utc(2026, 1, 6));

        Assert.Equal(
            [Utc(2026, 1, 3, 9), Utc(2026, 1, 4, 9), Utc(2026, 1, 5, 9)],
            occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void DailyInterval_UsesEveryNthDay()
    {
        var occurrences = Expand("FREQ=DAILY;INTERVAL=3", Utc(2026, 1, 1), Utc(2026, 1, 12));

        Assert.Equal(
            [Utc(2026, 1, 1, 9), Utc(2026, 1, 4, 9), Utc(2026, 1, 7, 9), Utc(2026, 1, 10, 9)],
            occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void Weekly_UsesMasterWeekdayWhenByDayIsAbsent()
    {
        var occurrences = Expand("FREQ=WEEKLY", Utc(2026, 1, 1), Utc(2026, 1, 20));

        Assert.Equal(
            [Utc(2026, 1, 1, 9), Utc(2026, 1, 8, 9), Utc(2026, 1, 15, 9)],
            occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void WeeklyByDay_OrdersMultipleWeekdaysChronologically()
    {
        var master = CreateMaster("FREQ=WEEKLY;BYDAY=MO,WE,FR") with
        {
            StartUtc = Utc(2026, 1, 5, 9),
            EndUtc = Utc(2026, 1, 5, 10)
        };

        var occurrences = service.Expand([master], Utc(2026, 1, 5), Utc(2026, 1, 13));

        Assert.Equal(
            [Utc(2026, 1, 5, 9), Utc(2026, 1, 7, 9), Utc(2026, 1, 9, 9), Utc(2026, 1, 12, 9)],
            occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void Monthly_SkipsMonthsWithoutMasterDayAndCrossesMonthBoundary()
    {
        var master = CreateMaster("FREQ=MONTHLY") with
        {
            StartUtc = Utc(2026, 1, 31, 9),
            EndUtc = Utc(2026, 1, 31, 10)
        };

        var occurrences = service.Expand([master], Utc(2026, 1, 1), Utc(2026, 6, 1));

        Assert.Equal(
            [Utc(2026, 1, 31, 9), Utc(2026, 3, 31, 9), Utc(2026, 5, 31, 9)],
            occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void Yearly_LeapDayOccursOnlyInLeapYears()
    {
        var master = CreateMaster("FREQ=YEARLY") with
        {
            StartUtc = Utc(2024, 2, 29, 9),
            EndUtc = Utc(2024, 2, 29, 10)
        };

        var occurrences = service.Expand([master], Utc(2024, 1, 1), Utc(2033, 1, 1));

        Assert.Equal(
            [Utc(2024, 2, 29, 9), Utc(2028, 2, 29, 9), Utc(2032, 2, 29, 9)],
            occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void Count_LimitsEntireSeriesRatherThanOnlyVisibleOccurrences()
    {
        var occurrences = Expand("FREQ=DAILY;COUNT=3", Utc(2026, 1, 3), Utc(2026, 1, 10));

        Assert.Single(occurrences);
        Assert.Equal(Utc(2026, 1, 3, 9), occurrences[0].StartUtc);
    }

    [Fact]
    public void UntilDate_IsInclusiveInEventTimeZone()
    {
        var occurrences = Expand("FREQ=DAILY;UNTIL=20260103", Utc(2026, 1, 1), Utc(2026, 1, 8));

        Assert.Equal(3, occurrences.Count);
        Assert.Equal(Utc(2026, 1, 3, 9), occurrences[^1].StartUtc);
    }

    [Fact]
    public void UntilUtc_IsInclusiveForOccurrenceStart()
    {
        var occurrences = Expand(
            "RRULE:FREQ=DAILY;UNTIL=20260102T090000Z",
            Utc(2026, 1, 1),
            Utc(2026, 1, 8));

        Assert.Equal(2, occurrences.Count);
        Assert.Equal(Utc(2026, 1, 2, 9), occurrences[^1].StartUtc);
    }

    [Fact]
    public void UnboundedRule_StillUsesFiniteQueryRange()
    {
        var occurrences = Expand("FREQ=DAILY", Utc(2036, 12, 30), Utc(2037, 1, 3));

        Assert.Equal(4, occurrences.Count);
        Assert.Equal(Utc(2036, 12, 30, 9), occurrences[0].StartUtc);
        Assert.Equal(Utc(2037, 1, 2, 9), occurrences[^1].StartUtc);
    }

    [Fact]
    public void RangeBounds_AreStrictAndOverlappingOccurrenceIsIncluded()
    {
        var master = CreateMaster("FREQ=DAILY") with
        {
            StartUtc = Utc(2026, 1, 1, 23),
            EndUtc = Utc(2026, 1, 2, 1)
        };

        var occurrences = service.Expand([master], Utc(2026, 1, 2), Utc(2026, 1, 3, 1));

        Assert.Equal([Utc(2026, 1, 1, 23), Utc(2026, 1, 2, 23)], occurrences.Select(item => item.StartUtc));
    }

    [Fact]
    public void GeneratedOccurrences_KeepMasterIdentityAndAreNotIndependentEvents()
    {
        var master = CreateMaster("FREQ=DAILY;COUNT=2");

        var occurrences = service.Expand([master], Utc(2026, 1, 1), Utc(2026, 1, 4));

        Assert.All(occurrences, occurrence => Assert.Equal(master.Id, occurrence.Id));
        Assert.All(occurrences, occurrence => Assert.Equal(master.RecurrenceRule, occurrence.RecurrenceRule));
    }

    [Fact]
    public void Count_DoesNotCountNonexistentLocalTimeDuringDstTransition()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var localStart = new DateTime(2026, 3, 7, 2, 30, 0, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);
        var master = CreateMaster("FREQ=DAILY;COUNT=2") with
        {
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = timeZone.Id
        };

        var occurrences = service.Expand(
            [master],
            new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(
            [new DateOnly(2026, 3, 7), new DateOnly(2026, 3, 9)],
            occurrences.Select(item => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.StartUtc, timeZone).DateTime)));
    }

    [Fact]
    public void AllDayRecurrence_PreservesLocalCalendarDatesAcrossDst()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var localStart = new DateTime(2026, 3, 7, 0, 0, 0, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        var master = CreateMaster("FREQ=DAILY;COUNT=3") with
        {
            StartUtc = ToUtc(localStart, timeZone),
            EndUtc = ToUtc(localEnd, timeZone),
            TimeZoneId = timeZone.Id,
            IsAllDay = true
        };

        var occurrences = service.Expand(
            [master],
            Utc(2026, 3, 6),
            Utc(2026, 3, 12));

        Assert.Equal(3, occurrences.Count);
        Assert.All(occurrences, occurrence =>
        {
            var startLocal = TimeZoneInfo.ConvertTime(occurrence.StartUtc, timeZone);
            var endLocal = TimeZoneInfo.ConvertTime(occurrence.EndUtc, timeZone);
            Assert.Equal(startLocal.Date.AddDays(1), endLocal.Date);
            Assert.Equal(TimeSpan.Zero, startLocal.TimeOfDay);
            Assert.Equal(TimeSpan.Zero, endLocal.TimeOfDay);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=DAILY;COUNT=-1")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO")]
    [InlineData("FREQ=WEEKLY;BYDAY=1MO")]
    [InlineData("FREQ=DAILY;BYMONTH=1")]
    [InlineData("FREQ=DAILY;FREQ=WEEKLY")]
    [InlineData("FREQ=DAILY;UNTIL=2026-01-01")]
    public void Parser_RejectsMalformedOrUnsupportedRules(string rule)
    {
        Assert.Throws<RecurrenceRuleException>(() => RecurrenceRuleParser.Parse(rule));
    }

    private IReadOnlyList<CalendarEvent> Expand(
        string rule,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd) =>
        service.Expand([CreateMaster(rule)], rangeStart, rangeEnd);

    private static CalendarEvent CreateMaster(string recurrenceRule)
    {
        var start = Utc(2026, 1, 1, 9);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "重复事件",
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            RecurrenceRule = recurrenceRule,
            CreatedAtUtc = start.AddDays(-1),
            UpdatedAtUtc = start
        };
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo timeZone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
}
