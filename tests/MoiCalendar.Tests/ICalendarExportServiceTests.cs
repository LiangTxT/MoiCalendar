using System.Text;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class ICalendarExportServiceTests
{
    private static readonly DateTimeOffset ExportedAt =
        new(2026, 9, 3, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SimpleEvent_ExportsDeterministicCalendarAndEventFields()
    {
        var calendarEvent = CreateEvent(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var export = await ExportAsync(calendarEvent);

        Assert.Equal("moicalendar-calendar-2026-09-03.ics", export.FileName);
        Assert.Equal("text/calendar;charset=utf-8", export.MediaType);
        Assert.Contains("BEGIN:VCALENDAR\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("VERSION:2.0\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("UID:11111111-1111-1111-1111-111111111111@moicalendar.local\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:普通事件\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20260903T090000Z\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("DTEND:20260903T100000Z\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("CREATED:20260901T090000Z\r\n", export.Content, StringComparison.Ordinal);
        Assert.Contains("LAST-MODIFIED:20260902T090000Z\r\n", export.Content, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnicodeMultilineAndReservedText_AreEscapedWithoutLosingUtf8Text()
    {
        var calendarEvent = CreateEvent(Guid.NewGuid()) with
        {
            Title = "香港，会議;计划\\安排",
            Description = "第一行\r\n第二行 🗓️",
            Location = "九龍, 尖沙咀;A座"
        };

        var content = (await ExportAsync(calendarEvent)).Content;

        Assert.Contains("SUMMARY:香港，会議\\;计划\\\\安排", content, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:第一行\\n第二行 🗓️", content, StringComparison.Ordinal);
        Assert.Contains("LOCATION:九龍\\, 尖沙咀\\;A座", content, StringComparison.Ordinal);
        Assert.Equal(content, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public async Task AllDayEvent_UsesExclusiveDateValuesInEventTimeZone()
    {
        var calendarEvent = CreateEvent(Guid.NewGuid()) with
        {
            IsAllDay = true,
            TimeZoneId = "Asia/Hong_Kong",
            StartUtc = new DateTimeOffset(2026, 9, 2, 16, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.Zero)
        };

        var content = (await ExportAsync(calendarEvent)).Content;

        Assert.Contains("DTSTART;VALUE=DATE:20260903\r\n", content, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20260904\r\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZonedTimedEvent_UsesTzIdAndLocalWallClockTime()
    {
        var calendarEvent = CreateEvent(Guid.NewGuid()) with
        {
            TimeZoneId = "Asia/Hong_Kong",
            StartUtc = new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero)
        };

        var content = (await ExportAsync(calendarEvent)).Content;

        Assert.Contains("DTSTART;TZID=Asia/Hong_Kong:20260903T090000\r\n", content, StringComparison.Ordinal);
        Assert.Contains("DTEND;TZID=Asia/Hong_Kong:20260903T100000\r\n", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecurringEvent_PreservesSupportedRule()
    {
        var calendarEvent = CreateEvent(Guid.NewGuid()) with
        {
            RecurrenceRule = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8"
        };

        var content = (await ExportAsync(calendarEvent)).Content;

        Assert.Contains("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8\r\n", content, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(content, "BEGIN:VEVENT"));
    }

    [Fact]
    public async Task LongUnicodeLines_AreFoldedAtUtf8OctetBoundary()
    {
        var calendarEvent = CreateEvent(Guid.NewGuid()) with { Title = new string('日', 80) };

        var content = (await ExportAsync(calendarEvent)).Content;

        Assert.All(
            content.Split("\r\n", StringSplitOptions.None).Where(line => line.Length > 0),
            line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.Contains("\r\n ", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletedEvents_AreNotExported()
    {
        var active = CreateEvent(Guid.NewGuid());
        var deleted = CreateEvent(Guid.NewGuid()) with
        {
            DeletedAtUtc = ExportedAt,
            UpdatedAtUtc = ExportedAt
        };
        var repository = new InMemoryEventRepository();
        await repository.CreateAsync(active);
        await repository.CreateAsync(deleted);

        var content = (await new CalendarExportService(
            repository,
            new FixedTimeProvider(ExportedAt)).CreateExportAsync()).Content;

        Assert.Equal(1, CountOccurrences(content, "BEGIN:VEVENT"));
        Assert.Contains(active.Id.ToString("D"), content, StringComparison.Ordinal);
        Assert.DoesNotContain(deleted.Id.ToString("D"), content, StringComparison.Ordinal);
    }

    private static async Task<ICalendarExport> ExportAsync(CalendarEvent calendarEvent)
    {
        var repository = new InMemoryEventRepository();
        await repository.CreateAsync(calendarEvent);
        return await new CalendarExportService(
            repository,
            new FixedTimeProvider(ExportedAt)).CreateExportAsync();
    }

    private static CalendarEvent CreateEvent(Guid id) => new()
    {
        Id = id,
        Title = "普通事件",
        Description = "说明",
        Location = "地点",
        StartUtc = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
        TimeZoneId = TimeZoneInfo.Utc.Id,
        IsAllDay = false,
        CreatedAtUtc = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero)
    };

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
