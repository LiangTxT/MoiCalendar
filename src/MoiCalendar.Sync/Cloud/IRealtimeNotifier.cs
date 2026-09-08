namespace MoiCalendar.Sync.Cloud;

public enum RealtimeConnectionState
{
    Disabled,
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Unavailable
}

public enum RealtimeWakeUpReason
{
    InitialConnection,
    ChangeNotification,
    Reconnected,
    ForegroundResume
}

/// <summary>
/// Provider-independent wake-up signal. It deliberately carries no remote entity payload or
/// revision; consumers must use the authoritative incremental synchronization protocol.
/// </summary>
public sealed class RealtimeWakeUpEventArgs(RealtimeWakeUpReason reason) : EventArgs
{
    public RealtimeWakeUpReason Reason { get; } = reason;
}

public sealed class RealtimeConnectionStateChangedEventArgs(
    RealtimeConnectionState state) : EventArgs
{
    public RealtimeConnectionState State { get; } = state;
}

public interface IRealtimeNotifier : IAsyncDisposable
{
    bool IsAvailable { get; }

    RealtimeConnectionState ConnectionState { get; }

    event EventHandler<RealtimeWakeUpEventArgs>? WakeUp;

    event EventHandler<RealtimeConnectionStateChangedEventArgs>? ConnectionStateChanged;

    Task StartAsync(string accountId, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
