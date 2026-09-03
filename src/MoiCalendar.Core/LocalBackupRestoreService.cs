using System.Text.Json;

namespace MoiCalendar.Core;

public sealed record BackupRestorePreview(
    Guid RestoreId,
    DateTimeOffset ExportedAtUtc,
    int SchemaVersion,
    int EventCount);

public sealed record BackupRestoreResult(int EventCount, DateTimeOffset SafetySnapshotCreatedAtUtc);

public sealed record BackupRestoreSafetySnapshot(
    DateTimeOffset CreatedAtUtc,
    int EventCount,
    int SyncOperationCount);

public interface ILocalBackupRestoreService
{
    BackupRestorePreview PrepareRestore(string json);

    Task<BackupRestoreResult> RestorePreparedAsync(
        Guid restoreId,
        CancellationToken cancellationToken = default);

    Task<BackupRestoreSafetySnapshot?> GetSafetySnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<BackupRestoreResult> UndoLastRestoreAsync(
        CancellationToken cancellationToken = default);

    Task<bool> IsSyncBlockedAfterRestoreAsync(
        CancellationToken cancellationToken = default);

    Task AllowSyncAfterRestoreAsync(
        CancellationToken cancellationToken = default);

    void CancelPreparedRestore(Guid restoreId);
}

public interface IBackupRestoreRepository
{
    Task<BackupRestoreSafetySnapshot> ReplaceAllEventsAndResetSyncAsync(
        IReadOnlyList<CalendarEvent> calendarEvents,
        CancellationToken cancellationToken = default);

    Task<BackupRestoreSafetySnapshot?> GetSafetySnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<BackupRestoreResult> RestoreSafetySnapshotAsync(
        CancellationToken cancellationToken = default);
}

public sealed class LocalBackupRestoreService(
    IBackupRestoreRepository restoreRepository,
    ILocalDataOperationLock? operationLock = null,
    IRestoreSyncGuard? restoreSyncGuard = null)
    : ILocalBackupRestoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> TopLevelProperties =
        ["schemaVersion", "exportedAtUtc", "appVersion", "calendarData"];
    private static readonly HashSet<string> CalendarDataProperties = ["calendarEvents"];
    private static readonly HashSet<string> EventProperties =
    [
        "id",
        "title",
        "description",
        "location",
        "startUtc",
        "endUtc",
        "timeZoneId",
        "isAllDay",
        "recurrenceRule",
        "createdAtUtc",
        "updatedAtUtc",
        "deletedAtUtc"
    ];
    private static readonly string[] RequiredEventProperties =
    [
        "id",
        "title",
        "description",
        "location",
        "startUtc",
        "endUtc",
        "timeZoneId",
        "isAllDay",
        "createdAtUtc",
        "updatedAtUtc"
    ];

    private readonly object stateGate = new();
    private readonly SemaphoreSlim restoreGate = new(1, 1);
    private readonly ILocalDataOperationLock operationLock =
        operationLock ?? NoOpLocalDataOperationLock.Instance;
    private readonly IRestoreSyncGuard? restoreSyncGuard = restoreSyncGuard;
    private PreparedRestore? preparedRestore;

    public BackupRestorePreview PrepareRestore(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LocalBackupRestoreException("备份文件为空或不是有效 JSON。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new LocalBackupRestoreException("备份文件不是有效 JSON，未修改本地数据。", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LocalBackupRestoreException("备份根节点必须是对象，未修改本地数据。");
            }

            ValidateAllowedProperties(root, TopLevelProperties, "备份根节点");
            var schemaVersion = ReadSchemaVersion(root);
            if (schemaVersion < MyCalendarBackup.MinimumSupportedSchemaVersion ||
                schemaVersion > MyCalendarBackup.CurrentSchemaVersion)
            {
                throw new LocalBackupRestoreException(
                    $"不支持备份 SchemaVersion {schemaVersion}；当前支持版本 " +
                    $"{MyCalendarBackup.MinimumSupportedSchemaVersion}–{MyCalendarBackup.CurrentSchemaVersion}。未修改本地数据。");
            }

            var exportedAtUtc = ReadExportedAtUtc(root);
            ValidateOptionalAppVersion(root);
            var calendarData = RequireProperty(root, "calendarData", JsonValueKind.Object);
            ValidateAllowedProperties(calendarData, CalendarDataProperties, "calendarData");
            var eventArray = RequireProperty(calendarData, "calendarEvents", JsonValueKind.Array);
            foreach (var eventElement in eventArray.EnumerateArray())
            {
                if (eventElement.ValueKind != JsonValueKind.Object)
                {
                    throw new LocalBackupRestoreException("calendarEvents 中包含非对象记录，未修改本地数据。");
                }

                ValidateAllowedProperties(eventElement, EventProperties, "日历事件");
                foreach (var propertyName in RequiredEventProperties)
                {
                    _ = RequireProperty(eventElement, propertyName);
                }
            }

            MyCalendarBackup backup;
            try
            {
                backup = root.Deserialize<MyCalendarBackup>(JsonOptions)
                    ?? throw new JsonException("备份内容为空。");
            }
            catch (JsonException exception)
            {
                throw new LocalBackupRestoreException("备份字段缺失或格式无效，未修改本地数据。", exception);
            }

            var events = backup.CalendarData.CalendarEvents.ToArray();
            ValidateEvents(events);
            var restoreId = Guid.NewGuid();
            lock (stateGate)
            {
                preparedRestore = new PreparedRestore(restoreId, events);
            }

            return new BackupRestorePreview(
                restoreId,
                exportedAtUtc,
                schemaVersion,
                events.Length);
        }
    }

    public async Task<BackupRestoreResult> RestorePreparedAsync(
        Guid restoreId,
        CancellationToken cancellationToken = default)
    {
        await restoreGate.WaitAsync(cancellationToken);
        try
        {
            PreparedRestore restore;
            lock (stateGate)
            {
                restore = preparedRestore is { } candidate && candidate.RestoreId == restoreId
                    ? candidate
                    : throw new LocalBackupRestoreException("恢复确认已失效，请重新选择备份文件。");
            }

            try
            {
                await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
                var snapshot = await restoreRepository.ReplaceAllEventsAndResetSyncAsync(
                    restore.CalendarEvents,
                    cancellationToken);

                lock (stateGate)
                {
                    if (preparedRestore?.RestoreId == restoreId)
                    {
                        preparedRestore = null;
                    }
                }

                return new BackupRestoreResult(
                    restore.CalendarEvents.Count,
                    snapshot.CreatedAtUtc);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BackupRestoreRepositoryException exception)
            {
                throw new LocalBackupRestoreException(
                    "恢复未完成；本地事务已中止，原有数据应保持不变。",
                    exception);
            }

        }
        finally
        {
            restoreGate.Release();
        }
    }

    public async Task<BackupRestoreSafetySnapshot?> GetSafetySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await restoreRepository.GetSafetySnapshotAsync(cancellationToken);
        }
        catch (BackupRestoreRepositoryException exception)
        {
            throw new LocalBackupRestoreException("无法读取最近一次恢复的本地安全快照。", exception);
        }
    }

    public async Task<BackupRestoreResult> UndoLastRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await restoreGate.WaitAsync(cancellationToken);
        try
        {
            await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
            try
            {
                return await restoreRepository.RestoreSafetySnapshotAsync(cancellationToken);
            }
            catch (BackupRestoreRepositoryException exception)
            {
                throw new LocalBackupRestoreException(
                    "无法撤销最近一次恢复；本地事务已中止，当前数据应保持不变。",
                    exception);
            }
        }
        finally
        {
            restoreGate.Release();
        }
    }

    public Task<bool> IsSyncBlockedAfterRestoreAsync(
        CancellationToken cancellationToken = default) =>
        restoreSyncGuard?.IsSyncBlockedAsync(cancellationToken) ?? Task.FromResult(false);

    public Task AllowSyncAfterRestoreAsync(
        CancellationToken cancellationToken = default) =>
        restoreSyncGuard?.AllowSyncAsync(cancellationToken) ?? Task.CompletedTask;

    public void CancelPreparedRestore(Guid restoreId)
    {
        lock (stateGate)
        {
            if (preparedRestore?.RestoreId == restoreId)
            {
                preparedRestore = null;
            }
        }
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        var property = RequireProperty(root, "schemaVersion", JsonValueKind.Number);
        if (!property.TryGetInt32(out var schemaVersion))
        {
            throw new LocalBackupRestoreException("SchemaVersion 必须是整数，未修改本地数据。");
        }

        return schemaVersion;
    }

    private static DateTimeOffset ReadExportedAtUtc(JsonElement root)
    {
        var property = RequireProperty(root, "exportedAtUtc", JsonValueKind.String);
        if (!property.TryGetDateTimeOffset(out var exportedAtUtc))
        {
            throw new LocalBackupRestoreException("备份日期无效，未修改本地数据。");
        }

        return exportedAtUtc.ToUniversalTime();
    }

    private static void ValidateOptionalAppVersion(JsonElement root)
    {
        if (root.TryGetProperty("appVersion", out var appVersion) &&
            appVersion.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            throw new LocalBackupRestoreException("appVersion 字段格式无效，未修改本地数据。");
        }
    }

    private static JsonElement RequireProperty(
        JsonElement element,
        string propertyName,
        JsonValueKind? expectedKind = null)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new LocalBackupRestoreException($"备份缺少必需字段 {propertyName}，未修改本地数据。");
        }

        if (expectedKind is { } kind && property.ValueKind != kind)
        {
            throw new LocalBackupRestoreException($"备份字段 {propertyName} 格式无效，未修改本地数据。");
        }

        return property;
    }

    private static void ValidateAllowedProperties(
        JsonElement element,
        IReadOnlySet<string> allowedProperties,
        string objectName)
    {
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seenProperties.Add(property.Name))
            {
                throw new LocalBackupRestoreException(
                    $"{objectName}包含重复字段 {property.Name}，未修改本地数据。");
            }

            if (!allowedProperties.Contains(property.Name))
            {
                throw new LocalBackupRestoreException(
                    $"{objectName}包含不允许的字段 {property.Name}，未修改本地数据。");
            }
        }
    }

    private static void ValidateEvents(IReadOnlyList<CalendarEvent> events)
    {
        var ids = new HashSet<Guid>();
        foreach (var calendarEvent in events)
        {
            if (calendarEvent.Id == Guid.Empty || !ids.Add(calendarEvent.Id))
            {
                throw new LocalBackupRestoreException("备份包含无效或重复的事件 ID，未修改本地数据。");
            }

            if (string.IsNullOrWhiteSpace(calendarEvent.Title) || calendarEvent.Title.Length > 200 ||
                calendarEvent.Description is null || calendarEvent.Description.Length > 4000 ||
                calendarEvent.Location is null || calendarEvent.Location.Length > 300 ||
                string.IsNullOrWhiteSpace(calendarEvent.TimeZoneId))
            {
                throw new LocalBackupRestoreException("备份包含无效的事件文本字段，未修改本地数据。");
            }

            if (calendarEvent.EndUtc <= calendarEvent.StartUtc ||
                calendarEvent.UpdatedAtUtc < calendarEvent.CreatedAtUtc ||
                calendarEvent.DeletedAtUtc > calendarEvent.UpdatedAtUtc)
            {
                throw new LocalBackupRestoreException("备份包含无效的事件时间范围，未修改本地数据。");
            }

            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(calendarEvent.TimeZoneId);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new LocalBackupRestoreException("备份包含当前设备无法识别的事件时区，未修改本地数据。", exception);
            }

            if (calendarEvent.RecurrenceRule is not null)
            {
                try
                {
                    _ = RecurrenceRuleParser.Parse(calendarEvent.RecurrenceRule);
                }
                catch (RecurrenceRuleException exception)
                {
                    throw new LocalBackupRestoreException("备份包含无效或不受支持的重复规则，未修改本地数据。", exception);
                }
            }
        }
    }

    private sealed record PreparedRestore(
        Guid RestoreId,
        IReadOnlyList<CalendarEvent> CalendarEvents);
}

public sealed class LocalBackupRestoreException : Exception
{
    public LocalBackupRestoreException(string message)
        : base(message)
    {
    }

    public LocalBackupRestoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BackupRestoreRepositoryException : Exception
{
    public BackupRestoreRepositoryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
