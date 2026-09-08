using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal sealed class SupabaseCloudSyncTransport(
    HttpClient httpClient,
    string publicKey,
    ISupabaseAccessTokenProvider accessTokenProvider) : ICloudSyncTransport, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => true;

    public async Task<CloudMutationResponse> PushAsync(
        CloudMutationRequest mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        JsonElement payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(mutation.Payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new CloudSyncTransportException(
                "本地 mutation 负载无效。",
                exception,
                CloudSyncFailureKind.Permanent,
                "invalid_local_payload");
        }

        var response = await SendRpcAsync<SupabaseMutationResponse>(
            "rpc/moicalendar_apply_calendar_mutation",
            new
            {
                p_mutation = new
                {
                    mutation_id = mutation.MutationId,
                    device_id = mutation.DeviceId,
                    entity_id = mutation.EntityId,
                    operation = mutation.Operation.ToString().ToLowerInvariant(),
                    base_revision = mutation.BaseRevision,
                    payload
                }
            },
            cancellationToken);

        return string.Equals(response.Status, "applied", StringComparison.Ordinal)
            ? new CloudMutationResponse(
                true,
                response.MutationId,
                response.EntityId,
                response.ServerRevision)
            : new CloudMutationResponse(
                false,
                response.MutationId,
                response.EntityId,
                null,
                response.Conflict?.Code ?? "unknown_conflict",
                response.Conflict?.CurrentRevision,
                response.Conflict?.CurrentEntity);
    }

    public async Task<CloudChangeBatch> PullAsync(
        long afterRevision,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (afterRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterRevision));
        }
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        var response = await SendRpcAsync<SupabasePullResponse>(
            "rpc/moicalendar_pull_calendar_changes",
            new { p_after_revision = afterRevision, p_limit = maximumCount },
            cancellationToken);
        var changes = response.Changes
            .Select(change => new CloudRemoteCalendarChange(
                change.CalendarEvent ?? throw new CloudSyncTransportException(
                    "云端事件负载缺失。",
                    CloudSyncFailureKind.Permanent,
                    "invalid_server_payload"),
                change.ServerRevision))
            .ToArray();
        return new CloudChangeBatch(changes, response.Cursor, response.HasMore);
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<T> SendRpcAsync<T>(
        string relativeUrl,
        object body,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        }
        catch (AccountServiceException exception)
        {
            throw new CloudSyncTransportException(
                "云账户会话不可用。",
                exception,
                CloudSyncFailureKind.AuthenticationRequired,
                "session_unavailable");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        request.Headers.TryAddWithoutValidation("apikey", publicKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CloudSyncTransportException(
                "云同步请求超时。",
                CloudSyncFailureKind.Transient,
                "request_timeout");
        }
        catch (HttpRequestException exception)
        {
            throw new CloudSyncTransportException(
                "无法连接云同步服务。",
                exception,
                CloudSyncFailureKind.Transient,
                "network_unavailable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var failureKind = statusCode == 401
                    ? CloudSyncFailureKind.AuthenticationRequired
                    : statusCode is 408 or 429 || statusCode >= 500
                        ? CloudSyncFailureKind.Transient
                        : CloudSyncFailureKind.Permanent;
                throw new CloudSyncTransportException(
                    $"云同步请求失败（HTTP {statusCode}）。",
                    failureKind,
                    $"http_{statusCode}",
                    statusCode);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                    ?? throw new CloudSyncTransportException(
                        "云同步服务返回了空响应。",
                        CloudSyncFailureKind.Permanent,
                        "empty_server_response");
            }
            catch (JsonException exception)
            {
                throw new CloudSyncTransportException(
                    "云同步服务返回了无法识别的数据。",
                    exception,
                    CloudSyncFailureKind.Permanent,
                    "invalid_server_payload");
            }
        }
    }

    private sealed record SupabaseMutationResponse
    {
        public string? Status { get; init; }

        [JsonPropertyName("mutation_id")]
        public Guid MutationId { get; init; }

        [JsonPropertyName("entity_id")]
        public Guid EntityId { get; init; }

        [JsonPropertyName("server_revision")]
        public long? ServerRevision { get; init; }

        public SupabaseConflict? Conflict { get; init; }
    }

    private sealed record SupabaseConflict
    {
        public string? Code { get; init; }

        [JsonPropertyName("current_revision")]
        public long? CurrentRevision { get; init; }

        [JsonPropertyName("current_entity")]
        public CalendarEvent? CurrentEntity { get; init; }
    }

    private sealed record SupabasePullResponse
    {
        public long Cursor { get; init; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; init; }

        public IReadOnlyList<SupabaseChange> Changes { get; init; } = [];
    }

    private sealed record SupabaseChange
    {
        [JsonPropertyName("server_revision")]
        public long ServerRevision { get; init; }

        [JsonPropertyName("calendar_event")]
        public CalendarEvent? CalendarEvent { get; init; }
    }
}
