using Microsoft.JSInterop;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class IndexedDbLocalDataSafetyTests
{
    [Fact]
    public async Task AcquireAndDispose_UsesMatchingBrowserWideLease()
    {
        var module = new FakeJsModule();
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var safety = new IndexedDbLocalDataSafety(connection);

        await using (await safety.AcquireAsync())
        {
            Assert.Equal(["acquireExclusiveOperationLock"], module.Calls);
        }

        Assert.Equal(
            ["acquireExclusiveOperationLock", "releaseExclusiveOperationLock"],
            module.Calls);
        Assert.Equal("lease-1", module.LastArguments![0]);
    }

    [Fact]
    public async Task RestoreGuard_CanBeReadAndExplicitlyCleared()
    {
        var module = new FakeJsModule { SyncBlocked = true };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var safety = new IndexedDbLocalDataSafety(connection);

        Assert.True(await safety.IsSyncBlockedAsync());
        await safety.AllowSyncAsync();

        Assert.Equal(
            ["isSyncBlockedAfterRestore", "allowSyncAfterRestore"],
            module.Calls);
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
        public List<string> Calls { get; } = [];

        public object?[]? LastArguments { get; private set; }

        public bool SyncBlocked { get; init; }

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

            Calls.Add(identifier);
            LastArguments = args;
            object? result = identifier switch
            {
                "acquireExclusiveOperationLock" => "lease-1",
                "isSyncBlockedAfterRestore" => SyncBlocked,
                _ => null
            };
            return ValueTask.FromResult(result is null ? default! : (TValue)result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
