using System.Text.Json;
using Microsoft.JSInterop;

namespace MoiCalendar.Sync.Supabase;

internal interface ISupabaseSessionStore
{
    Task<SupabaseStoredSession?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(SupabaseStoredSession session, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);

    Task<SupabaseAuthCallback?> ReadCallbackAsync(CancellationToken cancellationToken = default);
}

internal sealed record SupabaseStoredSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() =>
        $"SupabaseStoredSession {{ AccessToken = ***, RefreshToken = ***, ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

internal sealed record SupabaseAuthCallback(
    string? AccessToken,
    string? RefreshToken,
    string? TokenType,
    int? ExpiresIn,
    string? Type,
    string? ErrorCode,
    string? ErrorDescription)
{
    public override string ToString() =>
        $"SupabaseAuthCallback {{ AccessToken = ***, RefreshToken = ***, Type = {Type}, ErrorCode = {ErrorCode} }}";
}

internal sealed class SupabaseBrowserSessionStore(IJSRuntime jsRuntime)
    : ISupabaseSessionStore, IAsyncDisposable
{
    private const string ModulePath = "./_content/MoiCalendar.Sync/supabaseAuthSession.js";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private IJSObjectReference? module;
    private bool disposed;

    public async Task<SupabaseStoredSession?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await (await GetModuleAsync(cancellationToken))
            .InvokeAsync<string?>("readSession", cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var session = JsonSerializer.Deserialize<SupabaseStoredSession>(json, JsonOptions);
            return IsValid(session) ? session : null;
        }
        catch (JsonException)
        {
            await ClearAsync(cancellationToken);
            return null;
        }
    }

    public async Task WriteAsync(
        SupabaseStoredSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsValid(session))
        {
            throw new ArgumentException("认证会话无效。", nameof(session));
        }

        var json = JsonSerializer.Serialize(session, JsonOptions);
        await (await GetModuleAsync(cancellationToken))
            .InvokeVoidAsync("writeSession", cancellationToken, json);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync(cancellationToken))
            .InvokeVoidAsync("clearSession", cancellationToken);

    public async Task<SupabaseAuthCallback?> ReadCallbackAsync(
        CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync(cancellationToken))
            .InvokeAsync<SupabaseAuthCallback?>("readAuthCallback", cancellationToken);

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

    private async Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (module is not null)
        {
            return module;
        }

        await initializationGate.WaitAsync(cancellationToken);
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
            initializationGate.Release();
        }
    }

    private static bool IsValid(SupabaseStoredSession? session) =>
        session is not null &&
        !string.IsNullOrWhiteSpace(session.AccessToken) &&
        !string.IsNullOrWhiteSpace(session.RefreshToken) &&
        session.ExpiresAtUtc > DateTimeOffset.UnixEpoch;
}
