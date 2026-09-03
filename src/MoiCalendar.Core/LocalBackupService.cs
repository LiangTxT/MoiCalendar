using System.Text.Json;

namespace MoiCalendar.Core;

public sealed record MyCalendarBackup
{
    public const int MinimumSupportedSchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;

    public required int SchemaVersion { get; init; }

    public required DateTimeOffset ExportedAtUtc { get; init; }

    public string? AppVersion { get; init; }

    public required MyCalendarBackupData CalendarData { get; init; }
}

public sealed record MyCalendarBackupData
{
    public required IReadOnlyList<CalendarEvent> CalendarEvents { get; init; }
}

public sealed record LocalBackupExport(string FileName, string Json);

public interface ILocalBackupService
{
    Task<LocalBackupExport> CreateExportAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalBackupService(
    IEventRepository eventRepository,
    TimeProvider timeProvider,
    string? appVersion = null) : ILocalBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string? safeAppVersion = NormalizeAppVersion(appVersion);

    public async Task<LocalBackupExport> CreateExportAsync(
        CancellationToken cancellationToken = default)
    {
        var events = await eventRepository.GetAllIncludingDeletedAsync(cancellationToken);
        var exportedAtUtc = timeProvider.GetUtcNow().ToUniversalTime();
        var backup = new MyCalendarBackup
        {
            SchemaVersion = MyCalendarBackup.CurrentSchemaVersion,
            ExportedAtUtc = exportedAtUtc,
            AppVersion = safeAppVersion,
            CalendarData = new MyCalendarBackupData
            {
                CalendarEvents = events
                    .OrderBy(calendarEvent => calendarEvent.Id)
                    .ToArray()
            }
        };

        string json;
        try
        {
            json = JsonSerializer.Serialize(backup, JsonOptions);
            using var validationDocument = JsonDocument.Parse(json);
            var root = validationDocument.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                schemaVersion.GetInt32() != MyCalendarBackup.CurrentSchemaVersion ||
                !root.TryGetProperty("calendarData", out var calendarData) ||
                !calendarData.TryGetProperty("calendarEvents", out var calendarEvents) ||
                calendarEvents.ValueKind != JsonValueKind.Array ||
                calendarEvents.GetArrayLength() != events.Count)
            {
                throw new JsonException("备份序列化验证失败。");
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LocalBackupException("无法创建本地备份：日历数据无法序列化。", exception);
        }

        return new LocalBackupExport(
            $"mycalendar-backup-{exportedAtUtc:yyyy-MM-dd}.json",
            json);
    }

    private static string? NormalizeAppVersion(string? appVersion) =>
        Version.TryParse(appVersion, out var version) ? version.ToString() : null;
}

public sealed class LocalBackupException : Exception
{
    public LocalBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
