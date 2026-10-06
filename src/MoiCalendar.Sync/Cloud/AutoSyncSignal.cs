using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

/// <summary>Payload-free, scoped wake-ups shared by local commits and successful authentication.</summary>
public sealed class AutoSyncSignal : ILocalChangeNotifier, ICalendarDataChangeNotifier
{
    public event EventHandler? Committed;
    public event EventHandler? AccountReady;
    public event EventHandler? Changed;
    public void NotifyCommitted() => Committed?.Invoke(this, EventArgs.Empty);
    public void NotifyAccountReady() => AccountReady?.Invoke(this, EventArgs.Empty);
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
