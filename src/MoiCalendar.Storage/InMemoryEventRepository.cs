using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class InMemoryEventRepository : IEventRepository
{
    private readonly Dictionary<Guid, CalendarEvent> events = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CalendarEvent> CreateAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!events.TryAdd(calendarEvent.Id, calendarEvent))
            {
                throw new InvalidOperationException("相同 ID 的日历事件已经存在。");
            }

            return calendarEvent;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CalendarEvent> UpdateAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!events.ContainsKey(calendarEvent.Id))
            {
                throw new KeyNotFoundException("找不到要更新的日历事件。");
            }

            events[calendarEvent.Id] = calendarEvent;
            return calendarEvent;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        Guid id,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!events.TryGetValue(id, out var calendarEvent) || calendarEvent.DeletedAtUtc is not null)
            {
                return false;
            }

            var deletedAt = deletedAtUtc.ToUniversalTime();
            events[id] = calendarEvent with
            {
                DeletedAtUtc = deletedAt,
                UpdatedAtUtc = deletedAt
            };
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CalendarEvent?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return events.TryGetValue(id, out var calendarEvent) && calendarEvent.DeletedAtUtc is null
                ? calendarEvent
                : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CalendarEvent?> GetByIdIncludingDeletedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return events.GetValueOrDefault(id);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CalendarEvent> UpsertAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            events[calendarEvent.Id] = calendarEvent;
            return calendarEvent;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetAllIncludingDeletedAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return events.Values
                .OrderBy(calendarEvent => calendarEvent.Id)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetByRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("查询结束时间必须晚于开始时间。", nameof(endUtc));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            return events.Values
                .Where(calendarEvent =>
                    calendarEvent.DeletedAtUtc is null &&
                    calendarEvent.StartUtc < endUtc &&
                    calendarEvent.EndUtc > startUtc)
                .OrderBy(calendarEvent => calendarEvent.StartUtc)
                .ThenBy(calendarEvent => calendarEvent.Title, StringComparer.CurrentCulture)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetRecurringMastersAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return events.Values
                .Where(calendarEvent =>
                    calendarEvent.DeletedAtUtc is null &&
                    !string.IsNullOrWhiteSpace(calendarEvent.RecurrenceRule))
                .OrderBy(calendarEvent => calendarEvent.StartUtc)
                .ThenBy(calendarEvent => calendarEvent.Id)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }
}
