using Microsoft.JSInterop;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class SupabaseRealtimeNotifierTests
{
    [Theory]
    [InlineData("https://project.supabase.co", "/realtime/v1/websocket", "wss://project.supabase.co/realtime/v1/websocket")]
    [InlineData("http://127.0.0.1:54321", "/realtime/v1/websocket", "ws://127.0.0.1:54321/realtime/v1/websocket")]
    [InlineData("https://cloud.example.com/supabase", "/socket/websocket", "wss://cloud.example.com/supabase/socket/websocket")]
    public async Task Start_UsesConfiguredManagedOrSelfHostedEndpoint(
        string baseUrl,
        string realtimePath,
        string expectedUrl)
    {
        var runtime = new RecordingJsRuntime();
        await using var notifier = new SupabaseRealtimeNotifier(
            runtime,
            new CloudBackendOptions
            {
                Enabled = true,
                BaseUrl = baseUrl,
                PublicKey = "sb_publishable_test"
            },
            new SupabaseBackendOptions { RealtimePath = realtimePath },
            new FakeAccessTokenProvider());

        await notifier.StartAsync("user-id");

        Assert.Equal(1, runtime.Module.CreateCount);
        Assert.Equal(expectedUrl, Assert.IsType<Uri>(runtime.Module.CreateArguments![1]).AbsoluteUri.TrimEnd('/'));
        Assert.Equal("sb_publishable_test", runtime.Module.CreateArguments[2]);
        Assert.Equal("user-id", runtime.Module.CreateArguments[3]);
    }

    [Fact]
    public async Task Start_IsIdempotentAndReplacingAccountStopsPreviousSubscription()
    {
        var runtime = new RecordingJsRuntime();
        await using var notifier = new SupabaseRealtimeNotifier(
            runtime,
            new CloudBackendOptions
            {
                Enabled = true,
                BaseUrl = "https://project.supabase.co",
                PublicKey = "sb_publishable_test"
            },
            new SupabaseBackendOptions(),
            new FakeAccessTokenProvider());

        await notifier.StartAsync("first-user");
        await notifier.StartAsync("first-user");
        await notifier.StartAsync("second-user");

        Assert.Equal(2, runtime.Module.CreateCount);
        Assert.Equal(1, runtime.Subscriptions[0].StopCount);
        Assert.Equal(0, runtime.Subscriptions[1].StopCount);
    }

    [Fact]
    public async Task ProviderCallback_EmitsReasonOnly()
    {
        var runtime = new RecordingJsRuntime();
        await using var notifier = new SupabaseRealtimeNotifier(
            runtime,
            new CloudBackendOptions
            {
                Enabled = true,
                BaseUrl = "https://project.supabase.co",
                PublicKey = "sb_publishable_test"
            },
            new SupabaseBackendOptions(),
            new FakeAccessTokenProvider());
        RealtimeWakeUpEventArgs? received = null;
        notifier.WakeUp += (_, eventArgs) => received = eventArgs;

        await notifier.OnRealtimeWakeUpAsync("ChangeNotification");

        Assert.NotNull(received);
        Assert.Equal(RealtimeWakeUpReason.ChangeNotification, received.Reason);
        Assert.Single(received.GetType().GetProperties());
    }

    private sealed class FakeAccessTokenProvider : ISupabaseAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-access-token");
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public RecordingJsModule Module { get; } = new();

        public List<RecordingSubscription> Subscriptions => Module.Subscriptions;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("import", identifier);
            return ValueTask.FromResult((TValue)(object)Module);
        }
    }

    private sealed class RecordingJsModule : IJSObjectReference
    {
        public int CreateCount { get; private set; }
        public object?[]? CreateArguments { get; private set; }
        public List<RecordingSubscription> Subscriptions { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("createSupabaseRealtimeNotifier", identifier);
            CreateCount++;
            CreateArguments = args;
            var subscription = new RecordingSubscription();
            Subscriptions.Add(subscription);
            return ValueTask.FromResult((TValue)(object)subscription);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSubscription : IJSObjectReference
    {
        public int StopCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            Assert.Equal("stop", identifier);
            StopCount++;
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
