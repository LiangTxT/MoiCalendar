using Microsoft.JSInterop;
using MoiCalendar.Core;
using MoiCalendar.Storage;

namespace MoiCalendar.Tests;

public sealed class IndexedDbOperationalDiagnosticsTests
{
    [Fact]
    public async Task Repository_UsesDedicatedBoundedDiagnosticStoreBoundary()
    {
        var module = new FakeJsModule();
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var repository = new IndexedDbOperationalDiagnosticRepository(connection);
        var diagnosticEvent = new OperationalDiagnosticEvent
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            ApplicationVersion = "2.0.28.0",
            ClientPlatform = "Windows",
            Kind = OperationalDiagnosticKind.SyncCompleted,
            Outcome = OperationalDiagnosticOutcome.Succeeded
        };

        await repository.AddAsync(diagnosticEvent, 200);

        Assert.Equal("addOperationalDiagnosticEvent", module.Identifier);
        Assert.Equal(200, module.Arguments![1]);
        Assert.IsType<OperationalDiagnosticEvent>(module.Arguments[0]);
    }

    [Fact]
    public async Task HealthProbe_ReturnsUnavailableWithoutLeakingStorageException()
    {
        var module = new FakeJsModule { Failure = new JSException("private storage detail") };
        await using var connection = new IndexedDbConnection(new FakeJsRuntime(module));
        var health = new IndexedDbLocalStorageHealthService(connection);

        Assert.False(await health.IsAvailableAsync());
    }

    private sealed class FakeJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            identifier == "import" ? ValueTask.FromResult((TValue)module) : throw new InvalidOperationException(identifier);
    }

    private sealed class FakeJsModule : IJSObjectReference
    {
        public string? Identifier { get; private set; }
        public object?[]? Arguments { get; private set; }
        public Exception? Failure { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "initialize")
            {
                return ValueTask.FromResult(default(TValue)!);
            }
            if (Failure is not null)
            {
                return ValueTask.FromException<TValue>(Failure);
            }
            Identifier = identifier;
            Arguments = args;
            return ValueTask.FromResult(identifier == "checkIndexedDbHealth" ? (TValue)(object)true : default!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
