using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbEventRepository(IndexedDbConnection connection) : IEventRepository
{
    public Task<CalendarEvent> CreateAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CalendarEvent>("创建事件", "createEvent", cancellationToken, calendarEvent);

    public Task<CalendarEvent> UpdateAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CalendarEvent>("更新事件", "updateEvent", cancellationToken, calendarEvent);

    public Task<bool> DeleteAsync(
        Guid id,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<bool>(
            "删除事件",
            "deleteEvent",
            cancellationToken,
            id,
            deletedAtUtc.ToUniversalTime());

    public Task<CalendarEvent?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CalendarEvent?>("读取事件", "getEventById", cancellationToken, id);

    public Task<CalendarEvent?> GetByIdIncludingDeletedAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CalendarEvent?>("读取事件（含删除标记）", "getEventByIdIncludingDeleted", cancellationToken, id);

    public Task<CalendarEvent> UpsertAsync(
        CalendarEvent calendarEvent,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CalendarEvent>("应用同步事件", "upsertEvent", cancellationToken, calendarEvent);

    public async Task<IReadOnlyList<CalendarEvent>> GetByRangeAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("查询结束时间必须晚于开始时间。", nameof(endUtc));
        }

        var events = await InvokeAsync<CalendarEvent[]>(
            "查询事件",
            "getEventsByRange",
            cancellationToken,
            startUtc.ToUniversalTime(),
            endUtc.ToUniversalTime());
        return events;
    }

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            return await connection.InvokeAsync<T>(identifier, cancellationToken, arguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new EventRepositoryException($"{operation}失败：本地事件数据无法序列化或读取。", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new EventRepositoryException($"{operation}失败：事件包含不受支持的数据。", exception);
        }
        catch (JSException exception)
        {
            throw new EventRepositoryException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }

}
