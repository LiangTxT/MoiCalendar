using Microsoft.JSInterop;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal sealed class SupabaseRealtimeNotifier : IRealtimeNotifier
{
    private const string ModulePath = "./_content/MoiCalendar.Sync/supabaseRealtime.js";
    private readonly IJSRuntime jsRuntime;
    private readonly CloudBackendOptions options;
    private readonly SupabaseBackendOptions supabaseOptions;
    private readonly ISupabaseAccessTokenProvider accessTokenProvider;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private IJSObjectReference? module;
    private IJSObjectReference? subscription;
    private DotNetObjectReference<SupabaseRealtimeNotifier>? dotNetReference;
    private string? accountId;
    private int disposed;

    public SupabaseRealtimeNotifier(
        IJSRuntime jsRuntime,
        CloudBackendOptions options,
        SupabaseBackendOptions supabaseOptions,
        ISupabaseAccessTokenProvider accessTokenProvider)
    {
        this.jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.supabaseOptions = supabaseOptions ??
            throw new ArgumentNullException(nameof(supabaseOptions));
        this.accessTokenProvider = accessTokenProvider ??
            throw new ArgumentNullException(nameof(accessTokenProvider));
        ConnectionState = options.Enabled
            ? RealtimeConnectionState.Disconnected
            : RealtimeConnectionState.Disabled;
    }

    public bool IsAvailable => options.Enabled;

    public RealtimeConnectionState ConnectionState { get; private set; }

    public event EventHandler<RealtimeWakeUpEventArgs>? WakeUp;

    public event EventHandler<RealtimeConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public async Task StartAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) == 1, this);
        if (!IsAvailable)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("云账户 ID 不能为空。", nameof(accountId));
        }

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (subscription is not null &&
                string.Equals(this.accountId, accountId, StringComparison.Ordinal))
            {
                return;
            }

            await StopCoreAsync();
            SetConnectionState(RealtimeConnectionState.Connecting);
            module ??= await jsRuntime.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                ModulePath);
            dotNetReference ??= DotNetObjectReference.Create(this);
            subscription = await module.InvokeAsync<IJSObjectReference>(
                "createSupabaseRealtimeNotifier",
                cancellationToken,
                dotNetReference,
                BuildWebSocketUrl(),
                options.PublicKey!,
                accountId.Trim());
            this.accountId = accountId.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetConnectionState(RealtimeConnectionState.Disconnected);
            throw;
        }
        catch (JSDisconnectedException)
        {
            await StopCoreAsync();
            SetConnectionState(RealtimeConnectionState.Unavailable);
        }
        catch (JSException)
        {
            await StopCoreAsync();
            SetConnectionState(RealtimeConnectionState.Unavailable);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            SetConnectionState(IsAvailable
                ? RealtimeConnectionState.Disconnected
                : RealtimeConnectionState.Disabled);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        await lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (module is not null)
            {
                try
                {
                    await module.DisposeAsync();
                }
                catch (JSDisconnectedException)
                {
                }
                catch (JSException)
                {
                }
            }
            module = null;
            dotNetReference?.Dispose();
            dotNetReference = null;
        }
        finally
        {
            lifecycleGate.Release();
            lifecycleGate.Dispose();
        }
    }

    [JSInvokable]
    public Task<string> GetRealtimeAccessTokenAsync() =>
        accessTokenProvider.GetAccessTokenAsync();

    [JSInvokable]
    public Task OnRealtimeWakeUpAsync(string reason)
    {
        if (Enum.TryParse<RealtimeWakeUpReason>(reason, ignoreCase: false, out var parsed))
        {
            WakeUp?.Invoke(this, new RealtimeWakeUpEventArgs(parsed));
        }
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnRealtimeConnectionStateChangedAsync(string state)
    {
        if (Enum.TryParse<RealtimeConnectionState>(state, ignoreCase: false, out var parsed))
        {
            SetConnectionState(parsed);
        }
        return Task.CompletedTask;
    }

    private Uri BuildWebSocketUrl()
    {
        var baseUri = new Uri(options.BaseUrl!, UriKind.Absolute);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = baseUri.IsDefaultPort ? -1 : baseUri.Port,
            Path = CombinePaths(baseUri.AbsolutePath, supabaseOptions.RealtimePath),
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static string CombinePaths(string basePath, string realtimePath)
    {
        var prefix = basePath.TrimEnd('/');
        var suffix = realtimePath.TrimStart('/');
        return string.IsNullOrEmpty(prefix) ? $"/{suffix}" : $"{prefix}/{suffix}";
    }

    private async Task StopCoreAsync()
    {
        accountId = null;
        if (subscription is null)
        {
            return;
        }

        var current = subscription;
        subscription = null;
        try
        {
            await current.InvokeVoidAsync("stop");
            await current.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }
    }

    private void SetConnectionState(RealtimeConnectionState state)
    {
        if (ConnectionState == state)
        {
            return;
        }
        ConnectionState = state;
        ConnectionStateChanged?.Invoke(
            this,
            new RealtimeConnectionStateChangedEventArgs(state));
    }
}
