using System.Text.Json;
using System.Text.Json.Nodes;
using MoiCalendar.Core;

namespace MoiCalendar.Tests;

public sealed class LocalBackupRestoreServiceTests
{
    private static readonly DateTimeOffset ExportedAtUtc =
        new(2026, 8, 28, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuccessfulRestore_ReplacesEventsAndResetsSyncWorkOnlyAfterConfirmation()
    {
        var existing = CreateEvent(Guid.NewGuid(), "现有事件");
        var restored = CreateEvent(Guid.NewGuid(), "恢复事件");
        var repository = new FakeRestoreRepository([existing], syncOperationCount: 3);
        var operationLock = new TrackingOperationLock();
        var service = new LocalBackupRestoreService(repository, operationLock);

        var preview = service.PrepareRestore(CreateJson([restored]));

        Assert.Equal([existing], repository.Events);
        Assert.Equal(0, repository.ReplaceCount);
        Assert.Equal(ExportedAtUtc, preview.ExportedAtUtc);
        Assert.Equal(1, preview.SchemaVersion);
        Assert.Equal(1, preview.EventCount);

        var result = await service.RestorePreparedAsync(preview.RestoreId);

        Assert.Equal(1, result.EventCount);
        Assert.Equal([restored], repository.Events);
        Assert.Equal(0, repository.SyncOperationCount);
        Assert.Equal(1, repository.ReplaceCount);
        Assert.NotNull(await service.GetSafetySnapshotAsync());
        Assert.Equal(1, operationLock.AcquireCount);
        Assert.Equal(1, operationLock.ReleaseCount);
    }

    [Fact]
    public void CorruptJson_IsRejectedWithoutChangingData()
    {
        var repository = new FakeRestoreRepository([CreateEvent(Guid.NewGuid(), "保留")]);
        var service = new LocalBackupRestoreService(repository);

        var exception = Assert.Throws<LocalBackupRestoreException>(
            () => service.PrepareRestore("{not-json"));

        Assert.Contains("有效 JSON", exception.Message);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public void UnsupportedSchema_IsRejectedWithoutChangingData()
    {
        var repository = new FakeRestoreRepository([]);
        var service = new LocalBackupRestoreService(repository);
        var root = JsonNode.Parse(CreateJson([]))!.AsObject();
        root["schemaVersion"] = 99;

        var exception = Assert.Throws<LocalBackupRestoreException>(
            () => service.PrepareRestore(root.ToJsonString()));

        Assert.Contains("不支持", exception.Message);
        Assert.Contains("99", exception.Message);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public void MissingCalendarEvents_IsRejectedWithoutChangingData()
    {
        var repository = new FakeRestoreRepository([]);
        var service = new LocalBackupRestoreService(repository);
        var root = JsonNode.Parse(CreateJson([]))!.AsObject();
        root["calendarData"]!.AsObject().Remove("calendarEvents");

        var exception = Assert.Throws<LocalBackupRestoreException>(
            () => service.PrepareRestore(root.ToJsonString()));

        Assert.Contains("calendarEvents", exception.Message);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public void MissingRequiredEventField_IsRejectedWithoutChangingData()
    {
        var repository = new FakeRestoreRepository([]);
        var service = new LocalBackupRestoreService(repository);
        var root = JsonNode.Parse(CreateJson([CreateEvent(Guid.NewGuid(), "事件")]))!.AsObject();
        var eventObject = root["calendarData"]!["calendarEvents"]![0]!.AsObject();
        eventObject.Remove("title");

        var exception = Assert.Throws<LocalBackupRestoreException>(
            () => service.PrepareRestore(root.ToJsonString()));

        Assert.Contains("title", exception.Message);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task CancelledRestore_PreservesExistingData()
    {
        var existing = CreateEvent(Guid.NewGuid(), "保留事件");
        var repository = new FakeRestoreRepository([existing]);
        var service = new LocalBackupRestoreService(repository);
        var preview = service.PrepareRestore(CreateJson([CreateEvent(Guid.NewGuid(), "不恢复")]));

        service.CancelPreparedRestore(preview.RestoreId);

        await Assert.ThrowsAsync<LocalBackupRestoreException>(
            () => service.RestorePreparedAsync(preview.RestoreId));
        Assert.Equal([existing], repository.Events);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public async Task FailedRestore_PreservesExistingData()
    {
        var existing = CreateEvent(Guid.NewGuid(), "保留事件");
        var repository = new FakeRestoreRepository([existing]) { FailRestore = true };
        var service = new LocalBackupRestoreService(repository);
        var preview = service.PrepareRestore(CreateJson([CreateEvent(Guid.NewGuid(), "失败事件")]));

        var exception = await Assert.ThrowsAsync<LocalBackupRestoreException>(
            () => service.RestorePreparedAsync(preview.RestoreId));

        Assert.Contains("事务已中止", exception.Message);
        Assert.Equal([existing], repository.Events);
        Assert.Equal(1, repository.ReplaceCount);
    }

    [Fact]
    public async Task EmptyBackup_ClearsEventsAndSyncWork()
    {
        var repository = new FakeRestoreRepository(
            [CreateEvent(Guid.NewGuid(), "将被清除")],
            syncOperationCount: 2);
        var service = new LocalBackupRestoreService(repository);
        var preview = service.PrepareRestore(CreateJson([]));

        var result = await service.RestorePreparedAsync(preview.RestoreId);

        Assert.Equal(0, result.EventCount);
        Assert.Empty(repository.Events);
        Assert.Equal(0, repository.SyncOperationCount);
    }

    [Fact]
    public async Task UnicodeContent_IsRestoredExactly()
    {
        var unicode = CreateEvent(Guid.NewGuid(), "香港行程 🗓️") with
        {
            Description = "早餐：點心；会议：研发团队",
            Location = "九龍・尖沙咀"
        };
        var repository = new FakeRestoreRepository([]);
        var service = new LocalBackupRestoreService(repository);
        var preview = service.PrepareRestore(CreateJson([unicode]));

        await service.RestorePreparedAsync(preview.RestoreId);

        Assert.Equal(unicode, Assert.Single(repository.Events));
    }

    [Fact]
    public async Task AllDayEventAndTimeZone_AreRestoredExactly()
    {
        var allDay = CreateEvent(Guid.NewGuid(), "香港公众假期") with
        {
            IsAllDay = true,
            StartUtc = new DateTimeOffset(2026, 8, 27, 16, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 8, 28, 16, 0, 0, TimeSpan.Zero),
            TimeZoneId = "Asia/Hong_Kong"
        };
        var repository = new FakeRestoreRepository([]);
        var service = new LocalBackupRestoreService(repository);
        var preview = service.PrepareRestore(CreateJson([allDay]));

        await service.RestorePreparedAsync(preview.RestoreId);

        var restored = Assert.Single(repository.Events);
        Assert.True(restored.IsAllDay);
        Assert.Equal(allDay.StartUtc, restored.StartUtc);
        Assert.Equal(allDay.EndUtc, restored.EndUtc);
        Assert.Equal(allDay.TimeZoneId, restored.TimeZoneId);
    }

    [Fact]
    public async Task UndoLastRestore_RestoresEventsAndOriginalSyncQueue()
    {
        var existing = CreateEvent(Guid.NewGuid(), "恢复前事件");
        var repository = new FakeRestoreRepository([existing], syncOperationCount: 4);
        var service = new LocalBackupRestoreService(repository);
        var preview = service.PrepareRestore(
            CreateJson([CreateEvent(Guid.NewGuid(), "备份内事件")]));
        await service.RestorePreparedAsync(preview.RestoreId);

        var result = await service.UndoLastRestoreAsync();

        Assert.Equal(1, result.EventCount);
        Assert.Equal([existing], repository.Events);
        Assert.Equal(4, repository.SyncOperationCount);
        Assert.Null(await service.GetSafetySnapshotAsync());
    }

    [Theory]
    [InlineData("accessToken")]
    [InlineData("refreshToken")]
    [InlineData("webDavPassword")]
    [InlineData("authorization")]
    [InlineData("authenticationState")]
    public void CredentialOrAuthenticationFields_AreRejectedAndNeverRestored(string propertyName)
    {
        var repository = new FakeRestoreRepository([CreateEvent(Guid.NewGuid(), "保留")]);
        var service = new LocalBackupRestoreService(repository);
        var root = JsonNode.Parse(CreateJson([]))!.AsObject();
        root[propertyName] = "must-not-restore";

        var exception = Assert.Throws<LocalBackupRestoreException>(
            () => service.PrepareRestore(root.ToJsonString()));

        Assert.Contains("不允许", exception.Message);
        Assert.Equal(0, repository.ReplaceCount);
    }

    [Fact]
    public void DuplicateJsonProperty_IsRejectedAsMalformedWithoutChangingData()
    {
        var repository = new FakeRestoreRepository([CreateEvent(Guid.NewGuid(), "保留")]);
        var service = new LocalBackupRestoreService(repository);
        var json = CreateJson([]).Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":1,\"schemaVersion\":1",
            StringComparison.Ordinal);

        var exception = Assert.Throws<LocalBackupRestoreException>(
            () => service.PrepareRestore(json));

        Assert.Contains("重复字段", exception.Message);
        Assert.Equal(0, repository.ReplaceCount);
    }

    private static string CreateJson(IReadOnlyList<CalendarEvent> events) =>
        JsonSerializer.Serialize(
            new MyCalendarBackup
            {
                SchemaVersion = MyCalendarBackup.CurrentSchemaVersion,
                ExportedAtUtc = ExportedAtUtc,
                AppVersion = "1.0.0",
                CalendarData = new MyCalendarBackupData
                {
                    CalendarEvents = events
                }
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CalendarEvent CreateEvent(Guid id, string title)
    {
        var start = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = id,
            Title = title,
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

    private sealed class FakeRestoreRepository(
        IReadOnlyList<CalendarEvent> initialEvents,
        int syncOperationCount = 0) : IBackupRestoreRepository
    {
        public IReadOnlyList<CalendarEvent> Events { get; private set; } = initialEvents.ToArray();

        public int SyncOperationCount { get; private set; } = syncOperationCount;

        public int ReplaceCount { get; private set; }

        public bool FailRestore { get; init; }

        private IReadOnlyList<CalendarEvent>? snapshotEvents;

        private int snapshotSyncOperationCount;

        private static readonly DateTimeOffset SnapshotCreatedAtUtc =
            new(2026, 8, 28, 13, 0, 0, TimeSpan.Zero);

        public Task<BackupRestoreSafetySnapshot> ReplaceAllEventsAndResetSyncAsync(
            IReadOnlyList<CalendarEvent> calendarEvents,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCount++;
            if (FailRestore)
            {
                return Task.FromException<BackupRestoreSafetySnapshot>(new BackupRestoreRepositoryException(
                    "模拟事务失败。",
                    new InvalidOperationException("failure")));
            }

            snapshotEvents = Events.ToArray();
            snapshotSyncOperationCount = SyncOperationCount;
            Events = calendarEvents.ToArray();
            SyncOperationCount = 0;
            return Task.FromResult(new BackupRestoreSafetySnapshot(
                SnapshotCreatedAtUtc,
                snapshotEvents.Count,
                snapshotSyncOperationCount));
        }

        public Task<BackupRestoreSafetySnapshot?> GetSafetySnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshotEvents is null
                ? null
                : new BackupRestoreSafetySnapshot(
                    SnapshotCreatedAtUtc,
                    snapshotEvents.Count,
                    snapshotSyncOperationCount));
        }

        public Task<BackupRestoreResult> RestoreSafetySnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshotEvents is null)
            {
                return Task.FromException<BackupRestoreResult>(new BackupRestoreRepositoryException(
                    "没有快照。",
                    new InvalidOperationException("missing")));
            }

            Events = snapshotEvents;
            SyncOperationCount = snapshotSyncOperationCount;
            snapshotEvents = null;
            return Task.FromResult(new BackupRestoreResult(
                Events.Count,
                SnapshotCreatedAtUtc));
        }
    }

    private sealed class TrackingOperationLock : ILocalDataOperationLock
    {
        public int AcquireCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return Task.FromResult<IAsyncDisposable>(new Lease(this));
        }

        private sealed class Lease(TrackingOperationLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.ReleaseCount++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
