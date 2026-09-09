using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.App;

public sealed class BrowserClientPlatformProvider(IJSRuntime jsRuntime)
    : IClientPlatformProvider, IAsyncDisposable
{
    private const string ModulePath = "./clientDiagnostics.js";
    private readonly SemaphoreSlim gate = new(1, 1);
    private IJSObjectReference? module;
    private string? platform;

    public async ValueTask<string> GetPlatformAsync(CancellationToken cancellationToken = default)
    {
        if (platform is not null)
        {
            return platform;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (platform is not null)
            {
                return platform;
            }

            module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                ModulePath);
            var detected = await module.InvokeAsync<string>(
                "getBroadClientPlatform",
                cancellationToken);
            platform = OperationalDiagnosticSanitizer.NormalizePlatform(detected);
            return platform;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
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
            gate.Release();
            gate.Dispose();
        }
    }
}
