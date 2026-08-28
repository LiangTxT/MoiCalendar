using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class SyncOperationTests
{
    [Fact]
    public async Task DeviceService_ReturnsStableDeviceId()
    {
        var service = new InMemoryDeviceService();

        var first = await service.GetDeviceIdAsync();
        var second = await service.GetDeviceIdAsync();

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task OperationRepository_StoresAndUpdatesProviderIndependentOperation()
    {
        var repository = new InMemoryOperationRepository();
        var operation = CreateOperation();

        await repository.AddAsync(operation);
        var pending = await repository.GetByStatusAsync(SyncOperationStatus.Pending);
        var updated = await repository.UpdateStatusAsync(
            operation.OperationId,
            SyncOperationStatus.Uploaded);

        Assert.Equal(operation, Assert.Single(pending));
        Assert.Equal(SyncOperationStatus.Uploaded, updated.Status);
        Assert.Empty(await repository.GetByStatusAsync(SyncOperationStatus.Pending));
    }

    [Fact]
    public void SyncOperation_ContainsOnlyProviderIndependentFields()
    {
        var propertyNames = typeof(SyncOperation)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "DeviceId",
                "EntityId",
                "OperationId",
                "OperationType",
                "Payload",
                "Status",
                "TimestampUtc"
            },
            propertyNames);
    }

    [Fact]
    public async Task IndexedDbServices_UsePersistentDeviceSettingAndAtomicEventOperationCall()
    {
        var module = new FakeJsModule();
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var deviceService = new IndexedDbDeviceService(connection);
        var syncService = new IndexedDbEventChangeRepository(connection);
        var calendarEvent = CreateEvent();
        var operation = CreateOperation(calendarEvent.Id);

        var firstDeviceId = await deviceService.GetDeviceIdAsync();
        var secondDeviceId = await deviceService.GetDeviceIdAsync();
        var saved = await syncService.CreateEventAsync(calendarEvent, operation);

        Assert.Equal("persistent-device", firstDeviceId);
        Assert.Equal(firstDeviceId, secondDeviceId);
        Assert.Equal(calendarEvent, saved);
        Assert.Equal(1, module.InitializationCount);
        Assert.Equal(2, module.DeviceIdReadCount);
        Assert.Equal(1, module.AtomicCreateCount);
    }

    [Fact]
    public async Task IndexedDbSyncLogRepository_PersistsReadsAndClearsLogs()
    {
        var module = new FakeJsModule();
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbSyncLogRepository(connection);
        var entry = new SyncLogEntry
        {
            Id = Guid.NewGuid(),
            TimestampUtc = new DateTimeOffset(2026, 8, 28, 1, 0, 0, TimeSpan.Zero),
            Severity = SyncLogSeverity.Error,
            Stage = SyncLogStage.Push,
            Provider = "OneDrive",
            Message = "测试错误"
        };

        await repository.AddAsync(entry);
        var saved = Assert.Single(await repository.GetRecentAsync());
        await repository.ClearAsync();

        Assert.Equal(entry, saved);
        Assert.Empty(await repository.GetRecentAsync());
        Assert.Equal(200, module.LastLogRetentionLimit);
        Assert.Equal(1, module.LogClearCount);
    }

    private static SyncOperation CreateOperation(Guid? entityId = null) => new()
    {
        OperationId = Guid.NewGuid(),
        DeviceId = "test-device",
        EntityId = entityId ?? Guid.NewGuid(),
        OperationType = SyncOperationType.Create,
        TimestampUtc = new DateTimeOffset(2026, 8, 27, 1, 0, 0, TimeSpan.Zero),
        Payload = "{}",
        Status = SyncOperationStatus.Pending
    };

    private static CalendarEvent CreateEvent()
    {
        var start = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "同步操作测试",
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

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ValueTask.FromResult((TValue)module);
    }

    private sealed class FakeJsModule : IJSObjectReference
    {
        public int InitializationCount { get; private set; }

        public int DeviceIdReadCount { get; private set; }

        public int AtomicCreateCount { get; private set; }

        public int LastLogRetentionLimit { get; private set; }

        public int LogClearCount { get; private set; }

        private readonly List<SyncLogEntry> logEntries = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            object? result = identifier switch
            {
                "initialize" => CountInitialization(),
                "getOrCreateDeviceId" => GetDeviceId(),
                "createEventWithSyncOperation" => SaveEvent(args!),
                "addSyncLogEntry" => SaveLogEntry(args!),
                "getSyncLogEntries" => logEntries.ToArray(),
                "clearSyncLogEntries" => ClearLogEntries(),
                _ => default(TValue)
            };
            return ValueTask.FromResult((TValue)result!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private object? CountInitialization()
        {
            InitializationCount++;
            return null;
        }

        private string GetDeviceId()
        {
            DeviceIdReadCount++;
            return "persistent-device";
        }

        private object SaveEvent(object?[] args)
        {
            AtomicCreateCount++;
            return args[0]!;
        }

        private object? SaveLogEntry(object?[] args)
        {
            logEntries.Add((SyncLogEntry)args[0]!);
            LastLogRetentionLimit = (int)args[1]!;
            return null;
        }

        private object? ClearLogEntries()
        {
            logEntries.Clear();
            LogClearCount++;
            return null;
        }
    }
}
