using Microsoft.JSInterop;

namespace MoiCalendar.Storage;

public sealed class IndexedDbConnection(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private const string ModulePath = "./_content/MoiCalendar.Storage/indexedDbEventRepository.js";
    private const string DatabaseName = "MoiCalendar";
    private const int DatabaseVersion = 2;
    private const string EventStoreName = "events";
    private const string OperationStoreName = "syncOperations";
    private const string SettingsStoreName = "settings";

    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private IJSObjectReference? module;
    private bool disposed;

    public async Task<T> InvokeAsync<T>(
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        var initializedModule = await GetInitializedModuleAsync(cancellationToken);
        return await initializedModule.InvokeAsync<T>(identifier, cancellationToken, arguments);
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
                    EventStoreName,
                    OperationStoreName,
                    SettingsStoreName);
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
