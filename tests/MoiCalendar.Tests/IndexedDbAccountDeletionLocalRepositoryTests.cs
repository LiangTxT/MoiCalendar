using Microsoft.JSInterop;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class IndexedDbAccountDeletionLocalRepositoryTests
{
    [Fact]
    public void Transaction_ClearsCloudBindingCursorRevisionsAndStaleOutboxButPreservesDeviceIdentity()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MoiCalendar.Storage",
            "wwwroot",
            "indexedDbEventRepository.js"));
        var methodStart = source.IndexOf(
            "export async function resetAfterCloudAccountDeletion",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "export async function applyCloudChangesAndAdvanceCursor",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.Contains("settingsStore.delete(\"cloudSyncBinding\")", method, StringComparison.Ordinal);
        Assert.Contains("configuredCloudSyncStateStoreName).clear()", method, StringComparison.Ordinal);
        Assert.Contains("configuredSyncOutboxStoreName).clear()", method, StringComparison.Ordinal);
        Assert.Contains("configuredCloudEntityStateStoreName).clear()", method, StringComparison.Ordinal);
        Assert.Contains("if (removeLocalData)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("configuredDeviceIdentityStoreName", method, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_UsesSingleTransactionalInteropBoundary(bool removeLocalData)
    {
        var module = new FakeJsModule();
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbAccountDeletionLocalRepository(connection);

        await repository.ResetAfterAccountDeletionAsync("account-a", removeLocalData);

        Assert.Equal("resetAfterCloudAccountDeletion", module.Identifier);
        Assert.Equal("account-a", module.Arguments![0]);
        Assert.Equal(removeLocalData, module.Arguments[1]);
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
                : throw new InvalidOperationException(identifier);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MoiCalendar.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录。");
    }

    private sealed class FakeJsModule : IJSObjectReference
    {
        public string? Identifier { get; private set; }
        public object?[]? Arguments { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (identifier != "initialize")
            {
                Identifier = identifier;
                Arguments = args;
            }
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
