using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemoryEventChangeRepository(
    IEventRepository eventRepository,
    IOperationRepository operationRepository) : ILocalEventChangeRepository
{
    public async Task ApplyImportAsync(
        IReadOnlyList<CalendarImportChange> changes,
        CancellationToken cancellationToken = default)
    {
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
        return saved;
    }

    public async Task<CalendarEvent> UpdateEventAsync(
        CalendarEvent calendarEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var saved = await eventRepository.UpdateAsync(calendarEvent, cancellationToken);
        await operationRepository.AddAsync(operation, cancellationToken);
        return saved;
    }

    public async Task<bool> DeleteEventAsync(
        CalendarEvent deletedEvent,
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        await eventRepository.UpdateAsync(deletedEvent, cancellationToken);
        await operationRepository.AddAsync(operation, cancellationToken);
        return true;
    }
}
