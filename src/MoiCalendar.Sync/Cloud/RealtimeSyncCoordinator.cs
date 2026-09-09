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
    IOperationalDiagnosticsSink? diagnostics = null) : IRealtimeSyncCoordinator
{
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
                    var result = await cloudSyncService.SynchronizeAsync(cancellationToken);
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
                    // Realtime-triggered sync is best effort. Outbox and cursor preserve retry safety.
                }
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
}
