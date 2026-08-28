using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Storage;
using MoiCalendar.Sync;

namespace MoiCalendar.Tests;

public sealed class SyncDiagnosticsTests
{
    [Fact]
    public async Task SuccessfulSync_UpdatesStatusAndWritesLog()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.SynchronizeAsync();
        var status = await fixture.Service.GetStatusAsync();
        var log = Assert.Single(await fixture.Service.GetLogEntriesAsync());

        Assert.Equal(1, result.PushedCount);
        Assert.Equal("OneDrive", status.ActiveProvider);
        Assert.False(status.IsSyncing);
        Assert.NotNull(status.LastSyncStartedAtUtc);
        Assert.NotNull(status.LastSuccessfulSyncAtUtc);
        Assert.Null(status.LastFailedSyncAtUtc);
        Assert.Equal(0, status.PendingOperationCount);
        Assert.Equal(0, status.FailedOperationCount);
        Assert.Null(status.LastErrorSummary);
        Assert.Equal(SyncLogSeverity.Information, log.Severity);
    }

    [Fact]
    public async Task Status_ReportsSyncInProgress()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Storage.BlockDirectoryRequest = true;

        var syncTask = fixture.Service.SynchronizeAsync();
        await fixture.Storage.DirectoryRequestStarted.Task;
        var status = await fixture.Service.GetStatusAsync();

        Assert.True(status.IsSyncing);

        fixture.Storage.ReleaseDirectoryRequest();
        await syncTask;
    }

    [Fact]
    public async Task FailedSync_TracksFailureCountsAndSanitizesLog()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Storage.FailUpload = true;
        fixture.Storage.FailureMessage =
            "Authorization: Bearer secret-token password=hunter2 access_token=abc123";

        await Assert.ThrowsAsync<SyncStorageException>(() => fixture.Service.SynchronizeAsync());
        var status = await fixture.Service.GetStatusAsync();
        var log = Assert.Single(await fixture.Service.GetLogEntriesAsync());

        Assert.Null(status.LastSuccessfulSyncAtUtc);
        Assert.NotNull(status.LastFailedSyncAtUtc);
        Assert.Equal(0, status.PendingOperationCount);
        Assert.Equal(1, status.FailedOperationCount);
        Assert.DoesNotContain("secret-token", status.LastErrorSummary);
        Assert.DoesNotContain("hunter2", status.LastErrorSummary);
        Assert.DoesNotContain("abc123", status.LastErrorSummary);
        Assert.Equal(SyncLogSeverity.Error, log.Severity);
        Assert.Equal(SyncLogStage.Push, log.Stage);
        Assert.NotNull(log.OperationId);
        Assert.DoesNotContain("secret-token", log.Message);
        Assert.DoesNotContain("hunter2", log.Message);
        Assert.DoesNotContain("abc123", log.Message);
    }

    [Fact]
    public async Task FailedHttpResponse_DoesNotPersistUntrustedResponseBody()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Storage.FailUpload = true;
        fixture.Storage.FailureMessage = "上传失败（HTTP 500）：opaque-response-secret";

        await Assert.ThrowsAsync<SyncStorageException>(() => fixture.Service.SynchronizeAsync());
        var status = await fixture.Service.GetStatusAsync();
        var log = Assert.Single(await fixture.Service.GetLogEntriesAsync());

        Assert.Equal("HTTP_500", log.ErrorCode);
        Assert.DoesNotContain("opaque-response-secret", log.Message);
        Assert.DoesNotContain("opaque-response-secret", status.LastErrorSummary);
    }

    [Fact]
    public async Task RetryFailed_AfterNetworkRecovery_Succeeds()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Storage.FailUpload = true;
        await Assert.ThrowsAsync<SyncStorageException>(() => fixture.Service.SynchronizeAsync());

        fixture.Storage.FailUpload = false;
        var result = await fixture.Service.RetryFailedAsync();
        var status = await fixture.Service.GetStatusAsync();

        Assert.Equal(1, result.PushedCount);
        Assert.Equal(0, status.FailedOperationCount);
        Assert.Equal(0, status.PendingOperationCount);
        Assert.NotNull(status.LastSuccessfulSyncAtUtc);
        Assert.Null(status.LastErrorSummary);
    }

    [Fact]
    public async Task RetryFailed_WhenRemoteOperationAlreadyExists_DoesNotUploadDuplicate()
    {
        var operation = CreateOperation() with { Status = SyncOperationStatus.Failed };
        var storage = new FakeSyncStorageProvider();
        await storage.SeedAsync(operation);
        var operationRepository = new InMemoryOperationRepository();
        await operationRepository.AddAsync(operation);
        var eventRepository = new InMemoryEventRepository();
        var service = CreateService(operationRepository, eventRepository, storage);

        var result = await service.RetryFailedAsync();

        Assert.Equal(1, result.PushedCount);
        Assert.Equal(0, storage.UploadCount);
        Assert.Single(await eventRepository.GetByRangeAsync(
            new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Status_CountsPendingAndFailedOperations()
    {
        var operations = new InMemoryOperationRepository();
        await operations.AddAsync(CreateOperation());
        await operations.AddAsync(CreateOperation() with { Status = SyncOperationStatus.Failed });
        var service = CreateService(operations, new InMemoryEventRepository(), new FakeSyncStorageProvider());

        var status = await service.GetStatusAsync();

        Assert.Equal(1, status.PendingOperationCount);
        Assert.Equal(1, status.FailedOperationCount);
    }

    [Theory]
    [InlineData("Bearer token-value", "token-value")]
    [InlineData("Basic dXNlcjpwYXNz", "dXNlcjpwYXNz")]
    [InlineData("refresh_token=refresh-secret", "refresh-secret")]
    [InlineData("password: super-secret", "super-secret")]
    [InlineData("{\"access_token\":\"json-secret\"}", "json-secret")]
    [InlineData("https://user:password@example.test/path", "user:password")]
    public void LogSanitizer_RemovesSensitiveValues(string message, string secret)
    {
        var sanitized = SyncLogSanitizer.Sanitize(message);

        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.Contains("[已隐藏]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogRepository_EnforcesRetentionLimitAndCanClear()
    {
        var repository = new InMemorySyncLogRepository(retentionLimit: 3);
        for (var index = 0; index < 5; index++)
        {
            await repository.AddAsync(new SyncLogEntry
            {
                Id = Guid.NewGuid(),
                TimestampUtc = new DateTimeOffset(2026, 8, 28, index, 0, 0, TimeSpan.Zero),
                Severity = SyncLogSeverity.Information,
                Stage = SyncLogStage.Synchronize,
                Provider = "OneDrive",
                Message = $"entry-{index}"
            });
        }

        var entries = await repository.GetRecentAsync();
        Assert.Equal(["entry-4", "entry-3", "entry-2"], entries.Select(entry => entry.Message));

        await repository.ClearAsync();
        Assert.Empty(await repository.GetRecentAsync());
    }

    [Fact]
    public async Task LogRepository_SanitizesEntriesAtPersistenceBoundary()
    {
        var repository = new InMemorySyncLogRepository();
        await repository.AddAsync(new SyncLogEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero),
            Severity = SyncLogSeverity.Error,
            Stage = SyncLogStage.Push,
            Provider = "WebDAV",
            Message = "password=must-not-persist"
        });

        var saved = Assert.Single(await repository.GetRecentAsync());
        Assert.DoesNotContain("must-not-persist", saved.Message);
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var operationRepository = new InMemoryOperationRepository();
        await operationRepository.AddAsync(CreateOperation());
        var storage = new FakeSyncStorageProvider();
        var service = CreateService(
            operationRepository,
            new InMemoryEventRepository(),
            storage);
        return new Fixture(service, storage);
    }

    private static SyncService CreateService(
        IOperationRepository operationRepository,
        IEventRepository eventRepository,
        FakeSyncStorageProvider storage) =>
        new(
            operationRepository,
            eventRepository,
            storage,
            new InMemorySyncProviderSelection(new SyncProviderConfiguration(SyncProviderType.OneDrive)),
            new InMemorySyncLogRepository(),
            new InMemorySyncStatusRepository());

    private static SyncOperation CreateOperation()
    {
        var calendarEvent = CreateEvent();
        return new SyncOperation
        {
            OperationId = Guid.NewGuid(),
            DeviceId = "test-device",
            EntityId = calendarEvent.Id,
            OperationType = SyncOperationType.Create,
            TimestampUtc = calendarEvent.UpdatedAtUtc,
            Payload = JsonSerializer.Serialize(
                calendarEvent,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Status = SyncOperationStatus.Pending
        };
    }

    private static CalendarEvent CreateEvent()
    {
        var start = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "同步诊断测试",
            Description = string.Empty,
            Location = string.Empty,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            TimeZoneId = TimeZoneInfo.Utc.Id,
            IsAllDay = false,
            CreatedAtUtc = start,
            UpdatedAtUtc = start
        };
    }

    private sealed record Fixture(SyncService Service, FakeSyncStorageProvider Storage);

    private sealed class FakeSyncStorageProvider : ISyncStorageProvider
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

        public bool FailUpload { get; set; }

        public string FailureMessage { get; set; } = "模拟网络失败。";

        public int UploadCount { get; private set; }

        public bool BlockDirectoryRequest { get; set; }

        public TaskCompletionSource DirectoryRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource directoryRequestRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public async Task EnsureDirectoryAsync(
            string directoryPath,
            CancellationToken cancellationToken = default)
        {
            if (!BlockDirectoryRequest)
            {
                return;
            }

            DirectoryRequestStarted.TrySetResult();
            await directoryRequestRelease.Task.WaitAsync(cancellationToken);
            BlockDirectoryRequest = false;
        }

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
                throw new SyncStorageException(FailureMessage);
            }

            files[path] = content;
            UploadCount++;
            return Task.FromResult(new SyncFileMetadata(path, "v1", content.Length, null));
        }

        public Task<SyncTextFile?> DownloadTextAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(files.TryGetValue(path, out var content)
                ? new SyncTextFile(path, content, "v1", null)
                : null);

        public Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(
            string directoryPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncFileMetadata>>(files
                .Where(item => item.Key.StartsWith($"{directoryPath}/", StringComparison.Ordinal))
                .Select(item => new SyncFileMetadata(item.Key, "v1", item.Value.Length, null))
                .ToArray());

        public Task<bool> DeleteAsync(
            string path,
            string? expectedVersionToken = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(files.Remove(path));

        public async Task SeedAsync(SyncOperation operation)
        {
            var repository = new InMemoryOperationRepository();
            await repository.AddAsync(operation with { Status = SyncOperationStatus.Pending });
            var service = new SyncService(repository, new InMemoryEventRepository(), this);
            await service.PushAsync();
            UploadCount = 0;
        }


        public void ReleaseDirectoryRequest() => directoryRequestRelease.TrySetResult();
    }
}
