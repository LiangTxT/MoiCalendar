using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class LocalBackupServiceTests
{
    private static readonly DateTimeOffset ExportedAt =
        new(2026, 8, 28, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateExportAsync_CreatesVersionedJsonWithExpectedFileName()
    {
        var repository = new InMemoryEventRepository();
        var calendarEvent = CreateEvent(Guid.Parse("10000000-0000-0000-0000-000000000000"));
        await repository.CreateAsync(calendarEvent);
        var service = CreateService(repository, "1.2.3.4");

        var export = await service.CreateExportAsync();
        var backup = Deserialize(export.Json);

        Assert.Equal("mycalendar-backup-2026-08-28.json", export.FileName);
        Assert.Equal(MyCalendarBackup.CurrentSchemaVersion, backup.SchemaVersion);
        Assert.Equal(ExportedAt, backup.ExportedAtUtc);
        Assert.Equal("1.2.3.4", backup.AppVersion);
        Assert.Equal(calendarEvent, Assert.Single(backup.CalendarData.CalendarEvents));
    }

    [Fact]
    public async Task CreateExportAsync_ExportsEmptyCalendar()
    {
        var service = CreateService(new InMemoryEventRepository());

        var backup = Deserialize((await service.CreateExportAsync()).Json);

        Assert.Empty(backup.CalendarData.CalendarEvents);
    }

    [Fact]
    public async Task CreateExportAsync_SortsMultipleEventsDeterministically()
    {
        var repository = new InMemoryEventRepository();
        var laterId = CreateEvent(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var earlierId = CreateEvent(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        await repository.CreateAsync(laterId);
        await repository.CreateAsync(earlierId);
        var service = CreateService(repository);

        var first = await service.CreateExportAsync();
        var second = await service.CreateExportAsync();
        var backup = Deserialize(first.Json);

        Assert.Equal(first.Json, second.Json);
        Assert.Equal([earlierId.Id, laterId.Id],
            backup.CalendarData.CalendarEvents.Select(calendarEvent => calendarEvent.Id));
    }

    [Fact]
    public async Task CreateExportAsync_PreservesAllDayEventAndTimeZone()
    {
        var repository = new InMemoryEventRepository();
        var allDay = CreateEvent(Guid.NewGuid()) with
        {
            IsAllDay = true,
            StartUtc = new DateTimeOffset(2026, 8, 27, 16, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 8, 28, 16, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Asia/Hong_Kong"
        };
        await repository.CreateAsync(allDay);

        var exported = Assert.Single(
            Deserialize((await CreateService(repository).CreateExportAsync()).Json)
                .CalendarData.CalendarEvents);

        Assert.True(exported.IsAllDay);
        Assert.Equal(allDay.StartUtc, exported.StartUtc);
        Assert.Equal(allDay.EndUtc, exported.EndUtc);
        Assert.Equal("Asia/Hong_Kong", exported.TimeZoneId);
    }

    [Fact]
    public async Task CreateExportAsync_PreservesUnicodeText()
    {
        var repository = new InMemoryEventRepository();
        var unicodeEvent = CreateEvent(Guid.NewGuid()) with
        {
            Title = "香港行程 🗓️",
            Description = "早餐：點心；会議：研发团队",
            Location = "九龍・尖沙咀"
        };
        await repository.CreateAsync(unicodeEvent);

        var exported = Assert.Single(
            Deserialize((await CreateService(repository).CreateExportAsync()).Json)
                .CalendarData.CalendarEvents);

        Assert.Equal(unicodeEvent.Title, exported.Title);
        Assert.Equal(unicodeEvent.Description, exported.Description);
        Assert.Equal(unicodeEvent.Location, exported.Location);
    }

    [Fact]
    public async Task CreateExportAsync_PreservesRecurrenceRuleOnMasterEvent()
    {
        var repository = new InMemoryEventRepository();
        var recurring = CreateEvent(Guid.NewGuid()) with
        {
            RecurrenceRule = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=8"
        };
        await repository.CreateAsync(recurring);

        var exported = Assert.Single(
            Deserialize((await CreateService(repository).CreateExportAsync()).Json)
                .CalendarData.CalendarEvents);

        Assert.Equal(recurring.RecurrenceRule, exported.RecurrenceRule);
    }

    [Fact]
    public async Task CreateExportAsync_IncludesDeletionMarkerNeededByCalendarData()
    {
        var repository = new InMemoryEventRepository();
        var deleted = CreateEvent(Guid.NewGuid()) with
        {
            DeletedAtUtc = new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.Zero)
        };
        await repository.CreateAsync(deleted);

        var exported = Assert.Single(
            Deserialize((await CreateService(repository).CreateExportAsync()).Json)
                .CalendarData.CalendarEvents);

        Assert.Equal(deleted.DeletedAtUtc, exported.DeletedAtUtc);
    }

    [Fact]
    public async Task CreateExportAsync_DoesNotIncludeCredentialOrInternalStoreData()
    {
        var repository = new InMemoryEventRepository();
        await repository.CreateAsync(CreateEvent(Guid.NewGuid()));
        var service = CreateService(repository, "access_token=must-not-export");

        var json = (await service.CreateExportAsync()).Json;

        Assert.DoesNotContain("must-not-export", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("syncOperations", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("syncLogs", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Deserialize(json).AppVersion);
    }

    [Fact]
    public async Task CreateExportAsync_WritesExplicitSchemaVersionProperty()
    {
        var json = (await CreateService(new InMemoryEventRepository()).CreateExportAsync()).Json;
        using var document = JsonDocument.Parse(json);

        Assert.Equal(
            MyCalendarBackup.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void BackupFormat_ContainsOnlyWhitelistedTopLevelFields()
    {
        var propertyNames = typeof(MyCalendarBackup)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            ["AppVersion", "CalendarData", "ExportedAtUtc", "SchemaVersion"],
            propertyNames);
        Assert.Equal(
            ["CalendarEvents"],
            typeof(MyCalendarBackupData)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    private static LocalBackupService CreateService(
        IEventRepository repository,
        string? appVersion = null) =>
        new(repository, new FixedTimeProvider(ExportedAt), appVersion);

    private static MyCalendarBackup Deserialize(string json) =>
        JsonSerializer.Deserialize<MyCalendarBackup>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static CalendarEvent CreateEvent(Guid id)
    {
        var start = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = id,
            Title = "备份测试",
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            CreatedAtUtc = start.AddDays(-1),
            UpdatedAtUtc = start
        };
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
