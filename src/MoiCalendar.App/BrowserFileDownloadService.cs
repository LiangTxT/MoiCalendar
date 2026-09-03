using Microsoft.JSInterop;

namespace MoiCalendar.App;

public interface IBrowserFileDownloadService
{
    Task DownloadJsonAsync(
        string fileName,
        string json,
        CancellationToken cancellationToken = default);

    Task DownloadICalendarAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default);
}

public sealed class BrowserFileDownloadService(IJSRuntime jsRuntime)
    : IBrowserFileDownloadService, IAsyncDisposable
{
    private const string ModulePath = "./fileDownload.js";
    private readonly SemaphoreSlim moduleGate = new(1, 1);
    private IJSObjectReference? module;

    public async Task DownloadJsonAsync(
        string fileName,
        string json,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var loadedModule = await GetModuleAsync(cancellationToken);
            await loadedModule.InvokeVoidAsync(
                "downloadJson",
                cancellationToken,
                fileName,
                json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JSException exception)
        {
            throw new LocalBackupDownloadException("浏览器无法下载备份文件。", exception);
        }
    }

    public async Task DownloadICalendarAsync(
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var loadedModule = await GetModuleAsync(cancellationToken);
            await loadedModule.InvokeVoidAsync(
                "downloadICalendar",
                cancellationToken,
                fileName,
                content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JSException exception)
        {
            throw new ICalendarDownloadException("浏览器无法下载 iCalendar 文件。", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await moduleGate.WaitAsync();
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
            moduleGate.Release();
            moduleGate.Dispose();
        }
    }

    private async Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        if (module is not null)
        {
            return module;
        }

        await moduleGate.WaitAsync(cancellationToken);
        try
        {
            module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                ModulePath);
            return module;
        }
        finally
        {
            moduleGate.Release();
        }
    }
}

public sealed class LocalBackupDownloadException : Exception
{
    public LocalBackupDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ICalendarDownloadException : Exception
{
    public ICalendarDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
