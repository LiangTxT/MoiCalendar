using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal sealed class SupabaseCloudDeviceTransport(
    HttpClient httpClient,
    string publicKey,
    ISupabaseAccessTokenProvider accessTokenProvider) : ICloudDeviceTransport, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => true;

    public async Task<CloudDeviceRecord> RegisterAsync(
        CloudDeviceRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return ToRecord(await SendRpcAsync<SupabaseDevice>(
            "rpc/moicalendar_register_device",
            new
            {
                p_device_id = registration.DeviceId,
                p_name = registration.Name,
                p_platform = registration.Platform
            },
            cancellationToken));
    }

    public async Task<IReadOnlyList<CloudDeviceRecord>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await SendRpcAsync<IReadOnlyList<SupabaseDevice>>(
            "rpc/moicalendar_list_devices",
            new { },
            cancellationToken);
        return devices.Select(ToRecord).ToArray();
    }

    public async Task<CloudDeviceRecord> RenameAsync(
        Guid deviceId,
        string name,
        CancellationToken cancellationToken = default) =>
        ToRecord(await SendRpcAsync<SupabaseDevice>(
            "rpc/moicalendar_rename_device",
            new { p_device_id = deviceId, p_name = name },
            cancellationToken));

    public async Task<CloudDeviceRecord> RevokeAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        ToRecord(await SendRpcAsync<SupabaseDevice>(
            "rpc/moicalendar_revoke_device",
            new { p_device_id = deviceId },
            cancellationToken));

    public async Task AcknowledgeSuccessfulSyncAsync(
        Guid deviceId,
        long serverRevision,
        CancellationToken cancellationToken = default)
    {
        _ = await SendRpcAsync<SupabaseDevice>(
            "rpc/moicalendar_acknowledge_device_sync",
            new { p_device_id = deviceId, p_server_revision = serverRevision },
            cancellationToken);
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
                "云设备请求超时。",
                CloudSyncFailureKind.Transient,
                "request_timeout");
        }
        catch (HttpRequestException exception)
        {
            throw new CloudSyncTransportException(
                "无法连接云设备服务。",
                exception,
                CloudSyncFailureKind.Transient,
                "network_unavailable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateFailureAsync(response, cancellationToken);
            }

            try
            {
                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                    ?? throw new CloudSyncTransportException(
                        "云设备服务返回了空响应。",
                        CloudSyncFailureKind.Permanent,
                        "empty_server_response");
            }
            catch (JsonException exception)
            {
                throw new CloudSyncTransportException(
                    "云设备服务返回了无法识别的数据。",
                    exception,
                    CloudSyncFailureKind.Permanent,
                    "invalid_server_payload");
            }
        }
    }

    private static async Task<CloudSyncTransportException> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        string? providerMessage = null;
        if (response.Content.Headers.ContentLength is null or <= 16_384)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var error = JsonSerializer.Deserialize<SupabaseError>(body, JsonOptions);
                providerMessage = error?.Message;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Provider error bodies are never surfaced or logged.
            }
        }

        var errorCode = providerMessage switch
        {
            "moicalendar_device_revoked" => "device_revoked",
            "moicalendar_device_not_found" => "device_not_found",
            "moicalendar_device_id_unavailable" => "device_id_unavailable",
            _ => $"http_{statusCode}"
        };
        var failureKind = statusCode == 401
            ? CloudSyncFailureKind.AuthenticationRequired
            : statusCode is 408 or 429 || statusCode >= 500
                ? CloudSyncFailureKind.Transient
                : CloudSyncFailureKind.Permanent;
        return new CloudSyncTransportException(
            $"云设备请求失败（HTTP {statusCode}）。",
            failureKind,
            errorCode,
            statusCode);
    }

    private static CloudDeviceRecord ToRecord(SupabaseDevice device)
    {
        if (device.Id == Guid.Empty || string.IsNullOrWhiteSpace(device.Name))
        {
            throw new CloudSyncTransportException(
                "云设备服务返回的数据不完整。",
                CloudSyncFailureKind.Permanent,
                "invalid_server_payload");
        }

        return new CloudDeviceRecord(
            device.Id,
            device.Name,
            device.Platform,
            device.CreatedAt,
            device.LastSeenAt,
            device.DeletedAt is not null);
    }

    private sealed record SupabaseDevice
    {
        public Guid Id { get; init; }

        public string? Name { get; init; }

        public string? Platform { get; init; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; init; }

        [JsonPropertyName("last_seen_at")]
        public DateTimeOffset? LastSeenAt { get; init; }

        [JsonPropertyName("deleted_at")]
        public DateTimeOffset? DeletedAt { get; init; }
    }

    private sealed record SupabaseError
    {
        public string? Message { get; init; }
    }
}
