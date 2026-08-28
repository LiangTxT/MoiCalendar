using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class IndexedDbBackupRestoreRepositoryTests
{
    [Fact]
    public async Task ReplaceAll_UsesDedicatedTransactionalInteropBoundary()
    {
        var module = new FakeJsModule();
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbBackupRestoreRepository(connection);
        var events = new[] { CreateEvent() };

        await repository.ReplaceAllEventsAndResetSyncAsync(events);

        Assert.Equal("replaceAllEventsAndResetSync", module.LastIdentifier);
        Assert.Same(events, module.LastArguments![0]);
    }

    [Fact]
    public async Task ReplaceAll_WrapsIndexedDbFailure()
    {
        var module = new FakeJsModule { Failure = new JSException("事务失败") };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbBackupRestoreRepository(connection);

        var exception = await Assert.ThrowsAsync<BackupRestoreRepositoryException>(
            () => repository.ReplaceAllEventsAndResetSyncAsync([CreateEvent()]));

        Assert.Contains("事务未完成", exception.Message);
        Assert.Same(module.Failure, exception.InnerException);
    }

    private static CalendarEvent CreateEvent()
    {
        var start = new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);
        return new CalendarEvent
        {
            Id = Guid.NewGuid(),
            Title = "IndexedDB 恢复测试",
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

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            identifier == "import"
                ? ValueTask.FromResult((TValue)module)
                : throw new InvalidOperationException($"意外的 JS 调用：{identifier}");
    }

    private sealed class FakeJsModule : IJSObjectReference
    {
        public string? LastIdentifier { get; private set; }

        public object?[]? LastArguments { get; private set; }

        public Exception? Failure { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier == "initialize")
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            if (Failure is not null)
            {
                return ValueTask.FromException<TValue>(Failure);
            }

            LastIdentifier = identifier;
            LastArguments = args;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
