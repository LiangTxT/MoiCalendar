using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;

namespace MoiCalendar.Tests;

public sealed class SyncServiceTests
{
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
    public async Task NetworkFailure_KeepsOperationPending()
    {
        var operationRepository = new InMemoryOperationRepository();
        var calendarEvent = CreateEvent("离线编辑", UpdatedAt(1));
        var operation = CreateOperation(calendarEvent, SyncOperationType.Update);
        await operationRepository.AddAsync(operation);
        var storage = new FakeSyncStorageProvider { FailUpload = true };
        var service = new SyncService(operationRepository, new InMemoryEventRepository(), storage);

        await Assert.ThrowsAsync<SyncStorageException>(() => service.PushAsync());

        Assert.Equal(
            SyncOperationStatus.Pending,
            (await operationRepository.GetByIdAsync(operation.OperationId))?.Status);
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

        public bool FailUpload { get; init; }

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
            if (FailUpload)
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
                .Select(path => new SyncFileMetadata(path, "v1", files[path].Length, null))
                .ToArray());

        public Task<bool> DeleteAsync(
            string path,
            string? expectedVersionToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(files.Remove(path));

        public void MarkSeedComplete() => seedDownloadCount = DownloadCount;
    }
}
