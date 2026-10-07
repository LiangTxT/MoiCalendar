using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemoryEventChangeRepository(
    IEventRepository eventRepository,
    IOperationRepository operationRepository,
    InMemorySyncOutboxRepository? syncOutboxRepository = null) : ILocalEventChangeRepository
{
    private readonly InMemorySyncOutboxRepository syncOutboxRepository =
        syncOutboxRepository ?? new InMemorySyncOutboxRepository();

    public async Task ApplyImportAsync(
        IReadOnlyList<CalendarImportChange> changes,
        CancellationToken cancellationToken = default)
    {
        // Validate the complete batch before making any changes, just like IndexedDB.
        foreach (var change in changes)
        {
            var existing = await eventRepository.GetByIdIncludingDeletedAsync(change.CalendarEvent.Id, cancellationToken);
            if (change.ExpectedExistingEventId is null ? existing is not null :
                existing is null || existing.DeletedAtUtc is not null || existing.Id != change.ExpectedExistingEventId ||
                existing.UpdatedAtUtc != change.ExpectedExistingUpdatedAtUtc)
                throw new EventRepositoryException("日历已变化，请刷新后重试。", new InvalidOperationException("事件版本不匹配。"));
        }
        foreach (var change in changes)
        {
            if (change.ExpectedExistingEventId is null)
            {
                await CreateEventAsync(change.CalendarEvent, change.Operation, cancellationToken);
            }
            else
            {
                await UpdateEventAsync(change.CalendarEvent, change.Operation, cancellationToken);
            }
        }
    }

    public async Task<CalendarEvent> CreateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var saved = await eventRepository.CreateAsync(calendarEvent, cancellationToken);
        await operationRepository.AddAsync(operation, cancellationToken);
        await syncOutboxRepository.AddLocalOperationAsync(operation, cancellationToken);
        return saved;
    }

    public async Task<CalendarEvent> UpdateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var saved = await eventRepository.UpdateAsync(calendarEvent, cancellationToken);
        await operationRepository.AddAsync(operation, cancellationToken);
        await syncOutboxRepository.AddLocalOperationAsync(operation, cancellationToken);
        return saved;
    }

    public async Task<bool> DeleteEventAsync(
        CalendarEvent deletedEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        await eventRepository.UpdateAsync(deletedEvent, cancellationToken);
        await operationRepository.AddAsync(operation, cancellationToken);
        await syncOutboxRepository.AddLocalOperationAsync(operation, cancellationToken);
        return true;
    }
}
