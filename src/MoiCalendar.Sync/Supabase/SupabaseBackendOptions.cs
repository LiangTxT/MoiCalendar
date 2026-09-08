namespace MoiCalendar.Sync.Supabase;

/// <summary>
/// Supabase transport details that must not leak into cloud/domain abstractions.
/// </summary>
public sealed record SupabaseBackendOptions
{
    /// <summary>
    /// WebSocket path exposed by the configured Supabase gateway. Managed Supabase and the CLI
    /// use /realtime/v1/websocket. A directly exposed self-hosted Realtime service can use
    /// /socket/websocket.
    /// </summary>
    public string RealtimePath { get; init; } = "/realtime/v1/websocket";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RealtimePath) ||
            !RealtimePath.StartsWith("/", StringComparison.Ordinal) ||
            Uri.TryCreate(RealtimePath, UriKind.Absolute, out _) ||
            RealtimePath.Contains("?", StringComparison.Ordinal) ||
            RealtimePath.Contains("#", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MoiCalendar:CloudBackend:Supabase:RealtimePath 必须是以 / 开头且不含查询参数或片段的路径。");
        }
    }
}
