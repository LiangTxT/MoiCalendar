namespace MoiCalendar.Core;

public interface ILocalChangeNotifier
{
    event EventHandler? Committed;
    void NotifyCommitted();
}

public interface ICalendarDataChangeNotifier
{
    event EventHandler? Changed;
    void NotifyChanged();
}

/// <summary>Only successful local commits wake background work; network failures cannot undo a save.</summary>
public sealed class NotifyingLocalEventChangeRepository(
    ILocalEventChangeRepository inner,
    ILocalChangeNotifier notifier) : ILocalEventChangeRepository
{
    public async Task<CalendarEvent> CreateEventAsync(CalendarEvent calendarEvent, SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.CreateEventAsync(calendarEvent, operation, cancellationToken);
        Notify();
        return result;
    }

    public async Task<CalendarEvent> UpdateEventAsync(CalendarEvent calendarEvent, SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.UpdateEventAsync(calendarEvent, operation, cancellationToken);
        Notify();
        return result;
    }

    public async Task<bool> DeleteEventAsync(CalendarEvent deletedEvent, SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.DeleteEventAsync(deletedEvent, operation, cancellationToken);
        if (result) Notify();
        return result;
    }

    public async Task ApplyImportAsync(IReadOnlyList<CalendarImportChange> changes,
        CancellationToken cancellationToken = default)
    {
        await inner.ApplyImportAsync(changes, cancellationToken);
        if (changes.Count > 0) Notify();
    }

    private void Notify()
    {
        try { notifier.NotifyCommitted(); }
        catch { /* Background notifications must never turn a committed save into an error. */ }
    }
}
