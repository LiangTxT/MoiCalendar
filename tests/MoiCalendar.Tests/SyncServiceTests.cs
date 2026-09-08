using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;

namespace MoiCalendar.Tests;

public sealed class SyncServiceTests
{
    [Fact]
    public async Task RestoreSyncGuard_BlocksPushPullAndSynchronizationUntilExplicitlyAllowed()
    {
        var storage = new FakeSyncStorageProvider();
        var guard = new FakeRestoreSyncGuard { IsBlocked = true };
        var service = new SyncService(
            new InMemoryOperationRepository(),
            new InMemoryEventRepository(),
            storage,
            restoreSyncGuard: guard);

        await Assert.ThrowsAsync<RestoreSyncBlockedException>(() => service.PushAsync());
        await Assert.ThrowsAsync<RestoreSyncBlockedException>(() => service.PullAsync());
        await Assert.ThrowsAsync<RestoreSyncBlockedException>(() => service.SynchronizeAsync());
        Assert.Equal(0, storage.UploadCount);
        Assert.Equal(0, storage.DownloadCount);

        await guard.AllowSyncAsync();
        var result = await service.SynchronizeAsync();

        Assert.Equal(SyncResult.Empty, result);
    }

    [Fact]
    public async Task Synchronization_HoldsLocalDataOperationLockForEntirePushPullCycle()
    {
        var operationLock = new TrackingOperationLock();
        var service = new SyncService(
            new InMemoryOperationRepository(),
            new InMemoryEventRepository(),
            new FakeSyncStorageProvider(),
            operationLock: operationLock);

        await service.SynchronizeAsync();

        Assert.Equal(1, operationLock.AcquireCount);
        Assert.Equal(1, operationLock.ReleaseCount);
    }

    [Fact]
    public async Task DuplicatePush_UploadsOperationOnlyOnce()
    {
        var operationRepository = new InMemoryOperationRepository();
        var eventRepository = new InMemoryEventRepository();
        var storage = new FakeSyncStorageProvider();
        var calendarEvent = CreateEvent("本地创建", UpdatedAt(1));
        var operation = CreateOperation(calendarEvent, SyncOperationType.Create);
        await operationRepository.AddAsync(operation);
        var service = new SyncService(operationRepository, eventRepository, storage);

        var first = await service.PushAsync();
        var second = await service.PushAsync();

        Assert.Equal(1, first.PushedCount);
        Assert.Equal(0, second.PushedCount);
        Assert.Equal(1, storage.UploadCount);
        Assert.Equal(
            SyncOperationStatus.Uploaded,
            (await operationRepository.GetByIdAsync(operation.OperationId))?.Status);
    }

    [Fact]
    public async Task NetworkFailure_MarksOperationFailedAndRetryable()
    {
        var operationRepository = new InMemoryOperationRepository();
        var calendarEvent = CreateEvent("离线编辑", UpdatedAt(1));
        var operation = CreateOperation(calendarEvent, SyncOperationType.Update);
        await operationRepository.AddAsync(operation);
        var storage = new FakeSyncStorageProvider { FailUpload = true };
        var service = new SyncService(operationRepository, new InMemoryEventRepository(), storage);

        await Assert.ThrowsAsync<SyncStorageException>(() => service.PushAsync());

        Assert.Equal(
            SyncOperationStatus.Failed,
            (await operationRepository.GetByIdAsync(operation.OperationId))?.Status);
    }

    [Fact]
    public async Task RetryAfterNetworkRecovery_UploadsPreviouslyFailedOperation()
    {
        var operationRepository = new InMemoryOperationRepository();
        var calendarEvent = CreateEvent("恢复网络后上传", UpdatedAt(1));
        var operation = CreateOperation(calendarEvent, SyncOperationType.Create);
        await operationRepository.AddAsync(operation);
        var storage = new FakeSyncStorageProvider { FailUpload = true };
        var service = new SyncService(operationRepository, new InMemoryEventRepository(), storage);

        await Assert.ThrowsAsync<SyncStorageException>(() => service.PushAsync());
        storage.FailUpload = false;
        var result = await service.RetryFailedAsync();

        Assert.Equal(1, result.PushedCount);
        Assert.Equal(SyncOperationStatus.Applied,
            (await operationRepository.GetByIdAsync(operation.OperationId))?.Status);
    }

    [Fact]
    public async Task RetryAfterRemoteWriteBeforeLocalStatusUpdate_DoesNotUploadDuplicate()
    {
        var calendarEvent = CreateEvent("幂等重试", UpdatedAt(1));
        var operation = CreateOperation(calendarEvent, SyncOperationType.Create);
        var storage = await CreateRemoteStorageAsync(operation);
        var retryOperations = new InMemoryOperationRepository();
        await retryOperations.AddAsync(operation);
        var retryService = new SyncService(
            retryOperations,
            new InMemoryEventRepository(),
            storage);

        var result = await retryService.PushAsync();

        Assert.Equal(1, result.PushedCount);
        Assert.Equal(1, storage.UploadCount);
        Assert.Equal(SyncOperationStatus.Uploaded,
            (await retryOperations.GetByIdAsync(operation.OperationId))?.Status);
    }

    [Fact]
    public async Task DuplicatePull_AppliesOperationOnlyOnceAndDoesNotDuplicateEvent()
    {
        var remoteEvent = CreateEvent("远端事件", UpdatedAt(2));
        var operation = CreateOperation(remoteEvent, SyncOperationType.Create);
        var storage = await CreateRemoteStorageAsync(operation);
        var operationRepository = new InMemoryOperationRepository();
        var eventRepository = new InMemoryEventRepository();
        var service = new SyncService(operationRepository, eventRepository, storage);

        var first = await service.PullAsync();
        var second = await service.PullAsync();

        Assert.Equal(1, first.DownloadedCount);
        Assert.Equal(1, first.AppliedCount);
        Assert.Equal(SyncResult.Empty, second);
        Assert.Equal(1, storage.DownloadCountAfterSeed);
        Assert.Single(await eventRepository.GetByRangeAsync(
            remoteEvent.StartUtc.AddDays(-1),
            remoteEvent.EndUtc.AddDays(1)));
        Assert.Equal(
            SyncOperationStatus.Applied,
            (await operationRepository.GetByIdAsync(operation.OperationId))?.Status);
    }

    [Fact]
    public async Task Pull_AppliesRemoteCreate()
    {
        var remoteEvent = CreateEvent("远端创建", UpdatedAt(2));
        var operation = CreateOperation(remoteEvent, SyncOperationType.Create);
        var targetEvents = new InMemoryEventRepository();

        var result = await PullAsync(operation, targetEvents);

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(remoteEvent, await targetEvents.GetByIdAsync(remoteEvent.Id));
    }

    [Fact]
    public async Task Pull_PreservesRecurrenceRuleOnMasterEvent()
    {
        var remoteEvent = CreateEvent("远端重复事件", UpdatedAt(2)) with
        {
            RecurrenceRule = "FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10"
        };
        var targetEvents = new InMemoryEventRepository();

        var result = await PullAsync(
            CreateOperation(remoteEvent, SyncOperationType.Create),
            targetEvents);

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(remoteEvent.RecurrenceRule, (await targetEvents.GetByIdAsync(remoteEvent.Id))?.RecurrenceRule);
    }

    [Fact]
    public async Task Push_SerializesRecurrenceRuleOnMasterEvent()
    {
        var remoteEvent = CreateEvent("本地重复事件", UpdatedAt(2)) with
        {
            RecurrenceRule = "FREQ=YEARLY;INTERVAL=2;COUNT=4"
        };
        var operation = CreateOperation(remoteEvent, SyncOperationType.Create);
        var operations = new InMemoryOperationRepository();
        await operations.AddAsync(operation);
        var storage = new FakeSyncStorageProvider();
        var service = new SyncService(operations, new InMemoryEventRepository(), storage);

        await service.PushAsync();
        var remoteFile = await storage.DownloadTextAsync(
            $"{RemoteSyncFormat.OperationsDirectory}/{operation.OperationId:D}.json");

        Assert.NotNull(remoteFile);
        Assert.Contains(remoteEvent.RecurrenceRule, remoteFile.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_RemainsCompatibleWithVersionOneOperationDocuments()
    {
        var remoteEvent = CreateEvent("旧版远端事件", UpdatedAt(2));
        var operation = CreateOperation(remoteEvent, SyncOperationType.Create);
        using var payload = JsonDocument.Parse(operation.Payload);
        var document = new RemoteSyncOperationDocument
        {
            FormatVersion = RemoteSyncFormat.MinimumSupportedVersion,
            OperationId = operation.OperationId,
            DeviceId = operation.DeviceId,
            EntityId = operation.EntityId,
            OperationType = operation.OperationType,
            TimestampUtc = operation.TimestampUtc,
            Payload = payload.RootElement.Clone()
        };
        var storage = new FakeSyncStorageProvider();
        await storage.UploadTextAsync(
            RemoteSyncFormat.GetOperationPath(operation.OperationId),
            JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        storage.MarkSeedComplete();
        var events = new InMemoryEventRepository();
        var service = new SyncService(new InMemoryOperationRepository(), events, storage);

        var result = await service.PullAsync();

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(remoteEvent, await events.GetByIdAsync(remoteEvent.Id));
    }

    [Fact]
    public async Task Pull_AppliesNewerRemoteUpdateUsingLastWriteWins()
    {
        var localEvent = CreateEvent("本地旧标题", UpdatedAt(1));
        var remoteEvent = localEvent with
        {
            Title = "远端新标题",
            UpdatedAtUtc = UpdatedAt(3)
        };
        var targetEvents = new InMemoryEventRepository();
        await targetEvents.UpsertAsync(localEvent);

        var result = await PullAsync(
            CreateOperation(remoteEvent, SyncOperationType.Update),
            targetEvents);

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal("远端新标题", (await targetEvents.GetByIdAsync(localEvent.Id))?.Title);
    }

    [Fact]
    public async Task Pull_IgnoresOlderRemoteUpdateUsingLastWriteWins()
    {
        var localEvent = CreateEvent("本地较新标题", UpdatedAt(4));
        var remoteEvent = localEvent with
        {
            Title = "远端旧标题",
            UpdatedAtUtc = UpdatedAt(2)
        };
        var targetEvents = new InMemoryEventRepository();
        await targetEvents.UpsertAsync(localEvent);

        var result = await PullAsync(
            CreateOperation(remoteEvent, SyncOperationType.Update),
            targetEvents);

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal("本地较新标题", (await targetEvents.GetByIdAsync(localEvent.Id))?.Title);
    }

    [Fact]
    public async Task Pull_AppliesRemoteDeleteAsTombstone()
    {
        var localEvent = CreateEvent("待删除", UpdatedAt(1));
        var remoteDelete = localEvent with
        {
            UpdatedAtUtc = UpdatedAt(3),
            DeletedAtUtc = UpdatedAt(3)
        };
        var targetEvents = new InMemoryEventRepository();
        await targetEvents.UpsertAsync(localEvent);

        var result = await PullAsync(
            CreateOperation(remoteDelete, SyncOperationType.Delete),
            targetEvents);

        Assert.Equal(1, result.AppliedCount);
        Assert.Null(await targetEvents.GetByIdAsync(localEvent.Id));
        Assert.Equal(
            remoteDelete.DeletedAtUtc,
            (await targetEvents.GetByIdIncludingDeletedAsync(localEvent.Id))?.DeletedAtUtc);
    }

    [Fact]
    public async Task Pull_DoesNotResurrectTombstoneFromOlderRemoteEvent()
    {
        var staleRemoteEvent = CreateEvent("过期远端事件", UpdatedAt(2));
        var localTombstone = staleRemoteEvent with
        {
            UpdatedAtUtc = UpdatedAt(4),
            DeletedAtUtc = UpdatedAt(4)
        };
        var targetEvents = new InMemoryEventRepository();
        await targetEvents.UpsertAsync(localTombstone);

        var result = await PullAsync(
            CreateOperation(staleRemoteEvent, SyncOperationType.Update),
            targetEvents);

        Assert.Equal(0, result.AppliedCount);
        Assert.Null(await targetEvents.GetByIdAsync(staleRemoteEvent.Id));
        Assert.Equal(
            localTombstone.DeletedAtUtc,
            (await targetEvents.GetByIdIncludingDeletedAsync(staleRemoteEvent.Id))?.DeletedAtUtc);
    }

    [Fact]
    public async Task Pull_MalformedRemoteOperation_DoesNotBlockLaterValidOperation()
    {
        var storage = new FakeSyncStorageProvider();
        var malformedId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await storage.UploadTextAsync(RemoteSyncFormat.GetOperationPath(malformedId), "{not-json");
        var validEvent = CreateEvent("后续有效事件", UpdatedAt(2));
        var validOperation = CreateOperation(validEvent, SyncOperationType.Create) with
        {
            OperationId = Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        await SeedOperationAsync(storage, validOperation);
        storage.MarkSeedComplete();
        var events = new InMemoryEventRepository();
        var service = new SyncService(new InMemoryOperationRepository(), events, storage);

        var result = await service.PullAsync();

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(validEvent, await events.GetByIdAsync(validEvent.Id));
    }

    [Fact]
    public async Task Pull_InvalidRemoteEvent_DoesNotPersistItOrBlockLaterValidOperation()
    {
        var storage = new FakeSyncStorageProvider();
        var invalidEvent = CreateEvent("无效时间", UpdatedAt(2)) with
        {
            EndUtc = new DateTimeOffset(2026, 8, 27, 8, 0, 0, TimeSpan.Zero)
        };
        var invalidOperation = CreateOperation(invalidEvent, SyncOperationType.Create) with
        {
            OperationId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };
        await SeedOperationAsync(storage, invalidOperation);
        var validEvent = CreateEvent("有效时间", UpdatedAt(3));
        var validOperation = CreateOperation(validEvent, SyncOperationType.Create) with
        {
            OperationId = Guid.Parse("00000000-0000-0000-0000-000000000002")
        };
        await SeedOperationAsync(storage, validOperation);
        storage.MarkSeedComplete();
        var events = new InMemoryEventRepository();
        var service = new SyncService(new InMemoryOperationRepository(), events, storage);

        var result = await service.PullAsync();

        Assert.Equal(1, result.AppliedCount);
        Assert.Null(await events.GetByIdIncludingDeletedAsync(invalidEvent.Id));
        Assert.Equal(validEvent, await events.GetByIdAsync(validEvent.Id));
    }

    [Fact]
    public async Task Pull_AppliesEventAndOperationThroughAtomicRepository()
    {
        var remoteEvent = CreateEvent("原子应用", UpdatedAt(2));
        var remoteOperation = CreateOperation(remoteEvent, SyncOperationType.Create);
        var storage = await CreateRemoteStorageAsync(remoteOperation);
        var atomicRepository = new RecordingRemoteSyncApplyRepository();
        var service = new SyncService(
            new InMemoryOperationRepository(),
            new InMemoryEventRepository(),
            storage,
            remoteSyncApplyRepository: atomicRepository);

        var result = await service.PullAsync();

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(remoteEvent, atomicRepository.CalendarEvent);
        Assert.Equal(remoteOperation.OperationId, atomicRepository.Operation?.OperationId);
        Assert.Equal(SyncOperationStatus.Applied, atomicRepository.Operation?.Status);
        Assert.False(atomicRepository.OperationAlreadyExists);
    }

    [Fact]
    public async Task Pull_SkipsOperationWhoseListedSizeExceedsLimit()
    {
        var remoteEvent = CreateEvent("过大文件", UpdatedAt(2));
        var remoteOperation = CreateOperation(remoteEvent, SyncOperationType.Create);
        var storage = await CreateRemoteStorageAsync(remoteOperation);
        var path = RemoteSyncFormat.GetOperationPath(remoteOperation.OperationId);
        storage.ListedSizeOverrides[path] = RemoteSyncFormat.MaximumOperationFileBytes + 1L;
        var service = new SyncService(
            new InMemoryOperationRepository(),
            new InMemoryEventRepository(),
            storage);

        var result = await service.PullAsync();

        Assert.Equal(0, result.DownloadedCount);
        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(0, storage.DownloadCountAfterSeed);
    }

    [Fact]
    public async Task Pull_LocalOperationIdCollisionWithDifferentRemoteContent_IsNotApplied()
    {
        var operationId = Guid.NewGuid();
        var localEvent = CreateEvent("本地待上传", UpdatedAt(2));
        var localOperation = CreateOperation(localEvent, SyncOperationType.Create) with { OperationId = operationId };
        var operations = new InMemoryOperationRepository();
        await operations.AddAsync(localOperation);
        var remoteEvent = CreateEvent("冲突远端内容", UpdatedAt(3));
        var remoteOperation = CreateOperation(remoteEvent, SyncOperationType.Create) with { OperationId = operationId };
        var storage = new FakeSyncStorageProvider();
        await SeedOperationAsync(storage, remoteOperation);
        storage.MarkSeedComplete();
        var events = new InMemoryEventRepository();
        var service = new SyncService(operations, events, storage);

        var result = await service.PullAsync();

        Assert.Equal(0, result.AppliedCount);
        Assert.Null(await events.GetByIdIncludingDeletedAsync(remoteEvent.Id));
        Assert.Equal(SyncOperationStatus.Pending, (await operations.GetByIdAsync(operationId))?.Status);
    }

    [Fact]
    public async Task Pull_EqualTimestampDivergentUpdates_ConvergeDeterministically()
    {
        var first = CreateEvent("甲版本", UpdatedAt(3));
        var second = first with { Title = "乙版本" };
        var firstRepository = new InMemoryEventRepository();
        var secondRepository = new InMemoryEventRepository();
        await firstRepository.UpsertAsync(first);
        await secondRepository.UpsertAsync(second);

        await PullAsync(CreateOperation(second, SyncOperationType.Update), firstRepository);
        await PullAsync(CreateOperation(first, SyncOperationType.Update), secondRepository);

        Assert.Equal(
            (await firstRepository.GetByIdAsync(first.Id))?.Title,
            (await secondRepository.GetByIdAsync(first.Id))?.Title);
    }

    [Fact]
    public async Task Pull_EqualTimestampTombstone_WinsOverLiveEvent()
    {
        var live = CreateEvent("同时编辑", UpdatedAt(3));
        var tombstone = live with { DeletedAtUtc = UpdatedAt(3) };
        var events = new InMemoryEventRepository();
        await events.UpsertAsync(live);

        var result = await PullAsync(CreateOperation(tombstone, SyncOperationType.Delete), events);

        Assert.Equal(1, result.AppliedCount);
        Assert.Null(await events.GetByIdAsync(live.Id));
    }

    [Fact]
    public async Task Push_FailedOperation_DoesNotPreventLaterPendingOperationUpload()
    {
        var failedEvent = CreateEvent("失败项", UpdatedAt(1));
        var successfulEvent = CreateEvent("后续项", UpdatedAt(2));
        var failedOperation = CreateOperation(failedEvent, SyncOperationType.Create);
        var successfulOperation = CreateOperation(successfulEvent, SyncOperationType.Create);
        var operations = new InMemoryOperationRepository();
        await operations.AddAsync(failedOperation);
        await operations.AddAsync(successfulOperation);
        var storage = new FakeSyncStorageProvider();
        storage.FailUploadPaths.Add(RemoteSyncFormat.GetOperationPath(failedOperation.OperationId));
        var service = new SyncService(operations, new InMemoryEventRepository(), storage);

        await Assert.ThrowsAsync<SyncStorageException>(() => service.PushAsync());

        Assert.Equal(SyncOperationStatus.Failed, (await operations.GetByIdAsync(failedOperation.OperationId))?.Status);
        Assert.Equal(SyncOperationStatus.Uploaded, (await operations.GetByIdAsync(successfulOperation.OperationId))?.Status);
    }

    [Fact]
    public async Task Push_OversizedLocalOperation_IsFailedWithoutUpload()
    {
        var calendarEvent = CreateEvent("过大本地操作", UpdatedAt(2));
        var operation = CreateOperation(calendarEvent, SyncOperationType.Create) with
        {
            Payload = JsonSerializer.Serialize(calendarEvent with
            {
                Description = new string('大', RemoteSyncFormat.MaximumOperationFileBytes)
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var operations = new InMemoryOperationRepository();
        await operations.AddAsync(operation);
        var storage = new FakeSyncStorageProvider();
        var service = new SyncService(operations, new InMemoryEventRepository(), storage);

        var exception = await Assert.ThrowsAsync<SyncStorageException>(() => service.PushAsync());

        Assert.Contains("未上传", exception.Message);
        Assert.Equal(0, storage.UploadCount);
        Assert.Equal(SyncOperationStatus.Failed, (await operations.GetByIdAsync(operation.OperationId))?.Status);
    }

    private static async Task<SyncResult> PullAsync(
        SyncOperation operation,
        InMemoryEventRepository targetEvents)
    {
        var storage = await CreateRemoteStorageAsync(operation);
        var service = new SyncService(
            new InMemoryOperationRepository(),
            targetEvents,
            storage);
        return await service.PullAsync();
    }

    private static async Task<FakeSyncStorageProvider> CreateRemoteStorageAsync(SyncOperation operation)
    {
        var storage = new FakeSyncStorageProvider();
        var sourceOperations = new InMemoryOperationRepository();
        await sourceOperations.AddAsync(operation);
        var source = new SyncService(sourceOperations, new InMemoryEventRepository(), storage);
        await source.PushAsync();
        storage.MarkSeedComplete();
        return storage;
    }

    private static async Task SeedOperationAsync(FakeSyncStorageProvider storage, SyncOperation operation)
    {
        var operations = new InMemoryOperationRepository();
        await operations.AddAsync(operation);
        await new SyncService(operations, new InMemoryEventRepository(), storage).PushAsync();
    }

    private static SyncOperation CreateOperation(
        CalendarEvent calendarEvent,
        SyncOperationType operationType) => new()
    {
        OperationId = Guid.NewGuid(),
        DeviceId = "remote-device",
        EntityId = calendarEvent.Id,
        OperationType = operationType,
        TimestampUtc = calendarEvent.UpdatedAtUtc,
        Payload = JsonSerializer.Serialize(calendarEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        Status = SyncOperationStatus.Pending
    };

    private static CalendarEvent CreateEvent(string title, DateTimeOffset updatedAtUtc)
    {
        var start = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            CreatedAtUtc = UpdatedAt(0),
            UpdatedAtUtc = updatedAtUtc
        };
    }

    private static DateTimeOffset UpdatedAt(int hour) =>
        new(2026, 8, 27, hour, 0, 0, TimeSpan.Zero);

    private sealed class FakeSyncStorageProvider : ISyncStorageProvider
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);
        private int seedDownloadCount;

        public bool FailUpload { get; set; }

        public HashSet<string> FailUploadPaths { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long?> ListedSizeOverrides { get; } = new(StringComparer.Ordinal);

        public int UploadCount { get; private set; }

        public int DownloadCount { get; private set; }

        public int DownloadCountAfterSeed => DownloadCount - seedDownloadCount;

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task EnsureDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(files.ContainsKey(path));

        public Task<SyncFileMetadata> UploadTextAsync(
            string path,
            string content,
            string? expectedVersionToken = null,
            CancellationToken cancellationToken = default)
        {
            if (FailUpload || FailUploadPaths.Contains(path))
            {
                throw new SyncStorageException("模拟网络失败。");
            }

            files[path] = content;
            UploadCount++;
            return Task.FromResult(new SyncFileMetadata(path, "v1", content.Length, null));
        }

        public Task<SyncTextFile?> DownloadTextAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            return Task.FromResult(files.TryGetValue(path, out var content)
                ? new SyncTextFile(path, content, "v1", null)
                : null);
        }

        public Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(
            string directoryPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncFileMetadata>>(files.Keys
                .Where(path => path.StartsWith($"{directoryPath}/", StringComparison.Ordinal))
                .Select(path => new SyncFileMetadata(
                    path,
                    "v1",
                    ListedSizeOverrides.GetValueOrDefault(path, files[path].Length),
                    null))
                .ToArray());

        public Task<bool> DeleteAsync(
            string path,
            string? expectedVersionToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(files.Remove(path));

        public void MarkSeedComplete() => seedDownloadCount = DownloadCount;
    }

    private sealed class RecordingRemoteSyncApplyRepository : IRemoteSyncApplyRepository
    {
        public CalendarEvent? CalendarEvent { get; private set; }

        public SyncOperation? Operation { get; private set; }

        public bool OperationAlreadyExists { get; private set; }

        public Task ApplyAsync(
            CalendarEvent? calendarEvent,
            SyncOperation operation,
            bool operationAlreadyExists,
            CancellationToken cancellationToken = default)
        {
            CalendarEvent = calendarEvent;
            Operation = operation;
            OperationAlreadyExists = operationAlreadyExists;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRestoreSyncGuard : IRestoreSyncGuard
    {
        public bool IsBlocked { get; set; }

        public Task<bool> IsSyncBlockedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsBlocked);

        public Task AllowSyncAsync(CancellationToken cancellationToken = default)
        {
            IsBlocked = false;
            return Task.CompletedTask;
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
