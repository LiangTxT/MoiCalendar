using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbCalendarViewPreferenceStore(IndexedDbConnection connection)
    : ICalendarViewPreferenceStore
{
    public async Task<CalendarViewMode?> GetAsync(CancellationToken cancellationToken = default)
    {
        var value = await InvokeAsync<string?>(
            "读取日历视图偏好",
            "getCalendarViewPreference",
            cancellationToken);
        return Enum.TryParse<CalendarViewMode>(value, ignoreCase: true, out var viewMode) &&
               Enum.IsDefined(viewMode)
            ? viewMode
            : null;
    }

    public Task SaveAsync(
        CalendarViewMode viewMode,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            "保存日历视图偏好",
            "saveCalendarViewPreference",
            cancellationToken,
            viewMode.ToString());

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
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new EventRepositoryException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}
