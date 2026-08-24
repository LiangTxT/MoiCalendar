using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbEventRepository : IEventRepository, IAsyncDisposable
{
    private const string ModulePath = "./_content/MoiCalendar.Storage/indexedDbEventRepository.js";
    private const string DatabaseName = "MoiCalendar";
    private const int DatabaseVersion = 1;
    private const string EventStoreName = "events";

    private readonly IJSRuntime jsRuntime;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private IJSObjectReference? module;
    private bool disposed;

    public IndexedDbEventRepository(IJSRuntime jsRuntime)
    {
        this.jsRuntime = jsRuntime;
    }

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

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await initializationGate.WaitAsync();
        try
        {
            if (module is not null)
            {
                await module.DisposeAsync();
                module = null;
            }
        }
        finally
        {
            initializationGate.Release();
            initializationGate.Dispose();
        }
    }

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            var initializedModule = await GetInitializedModuleAsync(cancellationToken);
            return await initializedModule.InvokeAsync<T>(identifier, cancellationToken, arguments);
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

    private async Task<IJSObjectReference> GetInitializedModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (module is not null)
        {
            return module;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (module is not null)
            {
                return module;
            }

            IJSObjectReference? importedModule = null;
            try
            {
                importedModule = await jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    cancellationToken,
                    ModulePath);
                await importedModule.InvokeVoidAsync(
                    "initialize",
                    cancellationToken,
                    DatabaseName,
                    DatabaseVersion,
                    EventStoreName);
                module = importedModule;
                return module;
            }
            catch
            {
                if (importedModule is not null)
                {
                    await importedModule.DisposeAsync();
                }

                throw;
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }
}
