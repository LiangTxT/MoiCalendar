using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class RealtimeSyncCoordinatorTests
{
    private static readonly CloudAccount Account = new(
        "2f713c55-3811-49b2-aa5e-64e02af02f2d",
        "测试用户",
        "test@example.com",
        true);

    [Fact]
    public async Task Notification_TriggersAuthoritativeSynchronization()
    {
        var notifier = new FakeRealtimeNotifier();
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService());
        await coordinator.InitializeAsync();

        notifier.Notify(RealtimeWakeUpReason.ChangeNotification);

        await sync.WaitForCallAsync(1);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public void WakeUpContract_CannotCarryPayloadOrAdvanceCursor()
    {
        var properties = typeof(RealtimeWakeUpEventArgs).GetProperties();
        var dependencies = typeof(RealtimeSyncCoordinator)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.Collection(properties, property => Assert.Equal("Reason", property.Name));
        Assert.Equal(
            new[]
            {
                typeof(IRealtimeNotifier),
                typeof(ICloudSyncService),
                typeof(IAccountService),
                typeof(IOperationalDiagnosticsSink),
                typeof(AutoSyncSignal),
                typeof(TimeProvider)
            },
            dependencies);
        Assert.DoesNotContain(dependencies, type =>
            type.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase) ||
            type.Name.Contains("IndexedDb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NotificationStorm_IsCoalescedWithoutOverlappingSyncs()
    {
        var notifier = new FakeRealtimeNotifier();
        var sync = new ControlledCloudSyncService(blockFirstCall: true);
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService());
        await coordinator.InitializeAsync();

        notifier.Notify(RealtimeWakeUpReason.ChangeNotification);
        await sync.WaitForCallAsync(1);
        for (var index = 0; index < 10; index++)
        {
            notifier.Notify(RealtimeWakeUpReason.ChangeNotification);
        }
        sync.ReleaseFirstCall();

        await sync.WaitForCallAsync(2);
        Assert.Equal(2, sync.CallCount);
        Assert.Equal(1, sync.MaximumConcurrency);
    }

    [Fact]
    public async Task NotificationDuringActiveSync_SchedulesFollowUpPass()
    {
        var notifier = new FakeRealtimeNotifier();
        var sync = new ControlledCloudSyncService(blockFirstCall: true);
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService());
        await coordinator.InitializeAsync();

        notifier.Notify(RealtimeWakeUpReason.ChangeNotification);
        await sync.WaitForCallAsync(1);
        notifier.Notify(RealtimeWakeUpReason.ChangeNotification);
        sync.ReleaseFirstCall();

        await sync.WaitForCallAsync(2);
        Assert.Equal(1, sync.MaximumConcurrency);
    }

    [Fact]
    public async Task Reconnect_TriggersIncrementalSynchronization()
    {
        var notifier = new FakeRealtimeNotifier();
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService());
        await coordinator.InitializeAsync();

        notifier.Notify(RealtimeWakeUpReason.Reconnected);

        await sync.WaitForCallAsync(1);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task InvalidAuthentication_StopsRealtimeSubscription()
    {
        var notifier = new FakeRealtimeNotifier();
        await notifier.StartAsync(Account.Id);
        var sync = new AuthenticationRequiredCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService());
        await coordinator.InitializeAsync();

        notifier.Notify(RealtimeWakeUpReason.ChangeNotification);

        await notifier.WaitForStopAsync();
        Assert.Null(notifier.AccountId);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task AuthenticatedStartup_PullsByCursorEvenWhenRealtimeMessageWasMissed()
    {
        var notifier = new FakeRealtimeNotifier();
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService(Account));

        await coordinator.InitializeAsync();

        await sync.WaitForCallAsync(1);
        Assert.Equal(Account.Id, notifier.AccountId);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task RealtimeFailure_DoesNotBreakLoginOrLocalFirstUse()
    {
        var notifier = new FakeRealtimeNotifier { ThrowOnStart = true };
        var accounts = new RealtimeAwareAccountService(
            new FakeAccountService(Account),
            notifier);

        var account = await accounts.LoginAsync("test@example.com", "password123");

        Assert.Equal(Account, account);
        Assert.Equal(1, notifier.StartCount);
    }

    [Fact]
    public async Task LoginStartsAndLogoutDisposesSubscription()
    {
        var notifier = new FakeRealtimeNotifier();
        var accounts = new RealtimeAwareAccountService(
            new FakeAccountService(Account),
            notifier);

        _ = await accounts.LoginAsync("test@example.com", "password123");
        await accounts.LogoutAsync();

        Assert.Equal(1, notifier.StartCount);
        Assert.Equal(1, notifier.StopCount);
        Assert.Null(notifier.AccountId);
    }

    [Fact]
    public async Task RepeatedLoginLogout_DoesNotLeakSubscriptions()
    {
        var notifier = new FakeRealtimeNotifier();
        var accounts = new RealtimeAwareAccountService(
            new FakeAccountService(Account),
            notifier);

        for (var index = 0; index < 3; index++)
        {
            _ = await accounts.LoginAsync("test@example.com", "password123");
            _ = await accounts.GetCurrentAccountAsync();
            await accounts.LogoutAsync();
        }

        Assert.Equal(3, notifier.CreatedSubscriptionCount);
        Assert.Equal(0, notifier.ActiveSubscriptionCount);
        Assert.Equal(1, notifier.MaximumActiveSubscriptionCount);
    }

    [Fact]
    public async Task OwnDeviceWakeUp_RemainsIdempotentlySafe()
    {
        var notifier = new FakeRealtimeNotifier();
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(
            notifier,
            sync,
            new FakeAccountService());
        await coordinator.InitializeAsync();

        notifier.Notify(RealtimeWakeUpReason.ChangeNotification);

        await sync.WaitForCallAsync(1);
        await coordinator.DisposeAsync();
        Assert.Equal(CloudSyncOutcome.Succeeded, sync.LastResult!.Outcome);
        Assert.Equal(0, sync.LastResult.PulledCount);
    }

    [Fact]
    public async Task LocalCommits_DebounceUntilQuietFor300Milliseconds()
    {
        var clock = new ManualSyncTimeProvider();
        var signals = new AutoSyncSignal();
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(new FakeRealtimeNotifier(), sync,
            new FakeAccountService(), autoSyncSignal: signals, timeProvider: clock);
        await coordinator.InitializeAsync();
        signals.NotifyCommitted();
        clock.Advance(200);
        signals.NotifyCommitted();
        clock.Advance(299);
        Assert.Equal(0, sync.CallCount);
        clock.Advance(1);
        await sync.WaitForCallAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task ContinuousEditing_FlushesAtTwoSecondDeadline()
    {
        var clock = new ManualSyncTimeProvider();
        var signals = new AutoSyncSignal();
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(new FakeRealtimeNotifier(), sync,
            new FakeAccountService(), autoSyncSignal: signals, timeProvider: clock);
        await coordinator.InitializeAsync();
        signals.NotifyCommitted();
        for (var index = 0; index < 9; index++)
        {
            clock.Advance(200);
            signals.NotifyCommitted();
        }
        Assert.Equal(0, sync.CallCount);
        clock.Advance(200);
        await sync.WaitForCallAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LocalCommitDuringSync_ImmediatelyRunsOneFollowUpWithoutOverlap()
    {
        var signals = new AutoSyncSignal();
        var sync = new ControlledCloudSyncService(blockFirstCall: true);
        await using var coordinator = new RealtimeSyncCoordinator(new FakeRealtimeNotifier(), sync,
            new FakeAccountService(), autoSyncSignal: signals);
        await coordinator.InitializeAsync();
        signals.NotifyAccountReady();
        await sync.WaitForCallAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 10; i++) signals.NotifyCommitted();
        sync.ReleaseFirstCall();
        await sync.WaitForCallAsync(2).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sync.CallCount);
        Assert.Equal(1, sync.MaximumConcurrency);
    }

    [Fact]
    public async Task Login_SyncsEvenWhenRealtimeCannotStart()
    {
        var notifier = new FakeRealtimeNotifier { ThrowOnStart = true };
        var signals = new AutoSyncSignal();
        var accounts = new RealtimeAwareAccountService(new FakeAccountService(), notifier, signals);
        var sync = new ControlledCloudSyncService();
        await using var coordinator = new RealtimeSyncCoordinator(notifier, sync, accounts,
            autoSyncSignal: signals);
        await coordinator.InitializeAsync();
        await accounts.LoginAsync("test@example.com", "password123");
        await sync.WaitForCallAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FailedSync_RetriesAtReportedDeadlineAndCancelsTimerAfterSuccess()
    {
        var clock = new ManualSyncTimeProvider();
        var signals = new AutoSyncSignal();
        var sync = new ControlledCloudSyncService(response: call => call == 1
            ? new(CloudSyncOutcome.Failed, CloudSyncStatus.RetryScheduled, 0, 0, 0,
                NextRetryAtUtc: clock.GetUtcNow().AddSeconds(3))
            : new(CloudSyncOutcome.Succeeded, CloudSyncStatus.Idle, 0, 0, 0));
        await using var coordinator = new RealtimeSyncCoordinator(new FakeRealtimeNotifier(), sync,
            new FakeAccountService(), autoSyncSignal: signals, timeProvider: clock);
        await coordinator.InitializeAsync();
        signals.NotifyAccountReady();
        await WaitForCompletedAsync(sync, 1);
        clock.Advance(2999);
        Assert.Equal(1, sync.CallCount);
        clock.Advance(1);
        await WaitForCompletedAsync(sync, 2);
        clock.Advance(60000);
        Assert.Equal(2, sync.CallCount);
    }

    [Theory]
    [InlineData(CloudSyncStatus.Conflict)]
    [InlineData(CloudSyncStatus.Error)]
    [InlineData(CloudSyncStatus.AuthenticationRequired)]
    public async Task BlockedSync_DoesNotAutomaticallyRetry(CloudSyncStatus status)
    {
        var clock = new ManualSyncTimeProvider();
        var signals = new AutoSyncSignal();
        var sync = new ControlledCloudSyncService(response: _ =>
            new(CloudSyncOutcome.Failed, status, 0, 0, 0));
        await using var coordinator = new RealtimeSyncCoordinator(new FakeRealtimeNotifier(), sync,
            new FakeAccountService(), autoSyncSignal: signals, timeProvider: clock);
        await coordinator.InitializeAsync();
        signals.NotifyAccountReady();
        await WaitForCompletedAsync(sync, 1);
        clock.Advance(60000);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task Dispose_CancelsDebounceAndUnsubscribesLocalSignals()
    {
        var clock = new ManualSyncTimeProvider();
        var signals = new AutoSyncSignal();
        var sync = new ControlledCloudSyncService();
        var coordinator = new RealtimeSyncCoordinator(new FakeRealtimeNotifier(), sync,
            new FakeAccountService(), autoSyncSignal: signals, timeProvider: clock);
        await coordinator.InitializeAsync();
        signals.NotifyCommitted();
        await coordinator.DisposeAsync();
        signals.NotifyCommitted();
        signals.NotifyAccountReady();
        clock.Advance(60000);
        Assert.Equal(0, sync.CallCount);
    }

    private static async Task WaitForCompletedAsync(ControlledCloudSyncService sync, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (sync.CompletedCallCount < count) await Task.Delay(1, timeout.Token);
        // Let the coordinator finish processing the result and scheduling its timer.
        await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeRealtimeNotifier : IRealtimeNotifier
    {
        private readonly TaskCompletionSource stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;
        public RealtimeConnectionState ConnectionState { get; private set; } =
            RealtimeConnectionState.Disconnected;
        public string? AccountId { get; private set; }
        public bool ThrowOnStart { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int CreatedSubscriptionCount { get; private set; }
        public int ActiveSubscriptionCount { get; private set; }
        public int MaximumActiveSubscriptionCount { get; private set; }

        public event EventHandler<RealtimeWakeUpEventArgs>? WakeUp;
        public event EventHandler<RealtimeConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public Task StartAsync(string accountId, CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("Realtime unavailable");
            }
            if (AccountId == accountId)
            {
                return Task.CompletedTask;
            }
            if (AccountId is not null)
            {
                ActiveSubscriptionCount--;
            }
            AccountId = accountId;
            CreatedSubscriptionCount++;
            ActiveSubscriptionCount++;
            MaximumActiveSubscriptionCount = Math.Max(
                MaximumActiveSubscriptionCount,
                ActiveSubscriptionCount);
            ConnectionState = RealtimeConnectionState.Connected;
            ConnectionStateChanged?.Invoke(
                this,
                new RealtimeConnectionStateChangedEventArgs(ConnectionState));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (AccountId is not null)
            {
                ActiveSubscriptionCount--;
            }
            AccountId = null;
            ConnectionState = RealtimeConnectionState.Disconnected;
            stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public void Notify(RealtimeWakeUpReason reason) =>
            WakeUp?.Invoke(this, new RealtimeWakeUpEventArgs(reason));

        public Task WaitForStopAsync() => stopped.Task;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AuthenticationRequiredCloudSyncService : ICloudSyncService
    {
        public bool IsAvailable => true;
        public CloudSyncStatus CurrentStatus => CloudSyncStatus.AuthenticationRequired;
        public int CallCount { get; private set; }

        public Task<CloudSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new CloudSyncResult(
                CloudSyncOutcome.NotAuthenticated,
                CloudSyncStatus.AuthenticationRequired,
                0,
                0,
                0));
        }
    }

    private sealed class ControlledCloudSyncService(bool blockFirstCall = false,
        Func<int, CloudSyncResult>? response = null) : ICloudSyncService
    {
        private readonly TaskCompletionSource firstCallRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<int, TaskCompletionSource> callSignals = [];
        private readonly object gate = new();
        private int concurrency;
        private int completedCallCount;
        public int CompletedCallCount => Volatile.Read(ref completedCallCount);

        public bool IsAvailable => true;
        public CloudSyncStatus CurrentStatus { get; private set; } = CloudSyncStatus.Idle;
        public int CallCount { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public CloudSyncResult? LastResult { get; private set; }

        public async Task<CloudSyncResult> SynchronizeAsync(
            CancellationToken cancellationToken = default)
        {
            TaskCompletionSource signal;
            int call;
            lock (gate)
            {
                call = ++CallCount;
                concurrency++;
                MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
                if (!callSignals.TryGetValue(call, out signal!))
                {
                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    callSignals[call] = signal;
                }
                signal.TrySetResult();
            }

            try
            {
                CurrentStatus = CloudSyncStatus.Syncing;
                if (blockFirstCall && call == 1)
                {
                    await firstCallRelease.Task.WaitAsync(cancellationToken);
                }
                LastResult = response?.Invoke(call) ?? new CloudSyncResult(
                    CloudSyncOutcome.Succeeded,
                    CloudSyncStatus.Idle,
                    0,
                    0,
                    42);
                CurrentStatus = CloudSyncStatus.Idle;
                return LastResult;
            }
            finally
            {
                lock (gate)
                {
                    concurrency--;
                    Interlocked.Increment(ref completedCallCount);
                }
            }
        }

        public void ReleaseFirstCall() => firstCallRelease.TrySetResult();

        public Task WaitForCallAsync(int call)
        {
            lock (gate)
            {
                if (CallCount >= call)
                {
                    return Task.CompletedTask;
                }
                if (!callSignals.TryGetValue(call, out var signal))
                {
                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    callSignals[call] = signal;
                }
                return signal.Task;
            }
        }
    }

    private sealed class FakeAccountService(CloudAccount? account = null) : IAccountService
    {
        private CloudAccount? current = account;

        public bool IsAvailable => true;
        public bool IsPasswordRecovery => false;
        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);
        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);
        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(current);
        public Task<AccountRegistrationResult> RegisterAsync(
            string emailAddress,
            string password,
            string emailRedirectUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountRegistrationResult(
                current ?? Account,
                current is not null,
                false));
        public Task<CloudAccount> LoginAsync(
            string emailAddress,
            string password,
            CancellationToken cancellationToken = default)
        {
            current = Account;
            return Task.FromResult(current);
        }
        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            current = null;
            return Task.CompletedTask;
        }
        public Task RequestPasswordResetAsync(
            string emailAddress,
            string redirectUrl,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CloudAccount> CompletePasswordResetAsync(
            string newPassword,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(current ?? Account);
    }
}
