using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

public interface IRealtimeSyncCoordinator : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Coalesces best-effort wake-ups into serialized calls to the existing authoritative sync path.
/// </summary>
internal sealed class RealtimeSyncCoordinator(
    IRealtimeNotifier notifier,
    ICloudSyncService cloudSyncService,
    IAccountService accountService,
    IOperationalDiagnosticsSink? diagnostics = null,
    AutoSyncSignal? autoSyncSignal = null,
    TimeProvider? timeProvider = null) : IRealtimeSyncCoordinator
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaximumBatchWait = TimeSpan.FromSeconds(2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object timerGate = new();
    private ITimer? localTimer;
    private ITimer? retryTimer;
    private DateTimeOffset? batchStartedAt;
    private DateTimeOffset localDueAt;
    private int syncing;
    private int failures;
    private readonly IOperationalDiagnosticsSink diagnosticSink =
        diagnostics ?? DisabledOperationalDiagnosticsSink.Instance;
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private Task? worker;
    private int pending;
    private int initialized;
    private int disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref initialized) == 1)
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized == 1)
            {
                return;
            }

            localTimer = clock.CreateTimer(_ => FlushLocalChanges(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            retryTimer = clock.CreateTimer(_ => RequestSync(), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (autoSyncSignal is not null)
            {
                autoSyncSignal.Committed += OnLocalCommitted;
                autoSyncSignal.AccountReady += OnAccountReady;
            }
            notifier.WakeUp += OnWakeUp;
            notifier.ConnectionStateChanged += OnConnectionStateChanged;
            worker = RunWorkerAsync(lifetime.Token);
            initialized = 1;

            CloudAccount? account = null;
            try
            {
                account = await accountService.RestoreSessionAsync(cancellationToken);
                if (account is not null && notifier.IsAvailable)
                {
                    await notifier.StartAsync(account.Id, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AccountServiceException)
            {
                // A transient authentication failure cannot block local calendar startup.
            }
            catch
            {
                // Realtime startup is optional; cursor-based sync remains independently usable.
            }

            if (account is not null)
            {
                RequestSync();
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        notifier.WakeUp -= OnWakeUp;
        notifier.ConnectionStateChanged -= OnConnectionStateChanged;
        if (autoSyncSignal is not null)
        {
            autoSyncSignal.Committed -= OnLocalCommitted;
            autoSyncSignal.AccountReady -= OnAccountReady;
        }
        lock (timerGate)
        {
            localTimer?.Dispose();
            retryTimer?.Dispose();
        }
        try
        {
            await notifier.StopAsync();
        }
        catch
        {
            // Disposal remains best effort when the browser transport has already disappeared.
        }

        lifetime.Cancel();
        try
        {
            wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        if (worker is not null)
        {
            try
            {
                await worker;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetime.Dispose();
        wakeSignal.Dispose();
        initializationGate.Dispose();
    }

    private void OnWakeUp(object? sender, RealtimeWakeUpEventArgs eventArgs) => RequestSync();

    private void OnAccountReady(object? sender, EventArgs eventArgs) => RequestSync();

    private void OnLocalCommitted(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref syncing) == 1)
        {
            // A mutation committed during an active pass must get a follow-up pass immediately.
            RequestSync();
            return;
        }
        lock (timerGate)
        {
            if (disposed == 1) return;
            var now = clock.GetUtcNow();
            batchStartedAt ??= now;
            localDueAt = now + QuietPeriod;
            var deadline = batchStartedAt.Value + MaximumBatchWait;
            if (localDueAt > deadline) localDueAt = deadline;
            localTimer?.Change(localDueAt > now ? localDueAt - now : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushLocalChanges()
    {
        lock (timerGate)
        {
            if (disposed == 1 || batchStartedAt is null) return;
            var remaining = localDueAt - clock.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                localTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            RequestSync();
        }
    }

    private void ScheduleRetry(CloudSyncResult? result)
    {
        lock (timerGate)
        {
            if (disposed == 1) return;
            retryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (result is not null && result.Status is not
                (CloudSyncStatus.Offline or CloudSyncStatus.RetryScheduled))
            {
                failures = 0;
                return;
            }
            failures = Math.Min(failures + 1, 6);
            var delay = result?.NextRetryAtUtc is { } next
                ? next - clock.GetUtcNow()
                : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - 1)));
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            retryTimer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnConnectionStateChanged(
        object? sender,
        RealtimeConnectionStateChangedEventArgs eventArgs)
    {
        if (eventArgs.State != RealtimeConnectionState.Unavailable)
        {
            return;
        }

        _ = TryRecordRealtimeFailureAsync();
    }

    private async Task TryRecordRealtimeFailureAsync()
    {
        try
        {
            await diagnosticSink.RecordAsync(new OperationalDiagnosticDraft
            {
                Kind = OperationalDiagnosticKind.RealtimeConnectionFailure,
                Outcome = OperationalDiagnosticOutcome.Unavailable,
                FailureCategory = OperationalFailureCategory.Network,
                ErrorCode = "realtime_unavailable"
            });
        }
        catch
        {
            // Diagnostics are optional and cannot affect the Realtime lifecycle.
        }
    }

    private void RequestSync()
    {
        lock (timerGate)
        {
            if (disposed == 1) return;
            batchStartedAt = null;
            localTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            retryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        if (Volatile.Read(ref disposed) == 1 ||
            Interlocked.Exchange(ref pending, 1) == 1)
        {
            return;
        }

        try
        {
            wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await wakeSignal.WaitAsync(cancellationToken);
            while (Interlocked.Exchange(ref pending, 0) == 1)
            {
                try
                {
                    Interlocked.Exchange(ref syncing, 1);
                    var result = await cloudSyncService.SynchronizeAsync(cancellationToken);
                    ScheduleRetry(result);
                    if (result.Status == CloudSyncStatus.AuthenticationRequired)
                    {
                        await notifier.StopAsync(cancellationToken);
                        Interlocked.Exchange(ref pending, 0);
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    ScheduleRetry(null);
                }
                finally
                {
                    Interlocked.Exchange(ref syncing, 0);
                }
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
}
