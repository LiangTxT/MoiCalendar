using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal sealed class SupabaseReminderTransport(HttpClient client, string publicKey, ISupabaseAccessTokenProvider tokens)
    : ICloudReminderTransport, IDisposable
{
    public bool IsAvailable => true;
    public async Task<string> GetPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("functions/v1/push-configuration", null, cancellationToken);
        var key = result.TryGetProperty("publicKey", out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return !string.IsNullOrWhiteSpace(key) ? key : throw new InvalidOperationException("后台提醒尚未配置推送公钥。");
    }
    public async Task RegisterAsync(Guid deviceId, string subscriptionJson, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(subscriptionJson);
        _ = await SendAsync("rest/v1/rpc/moicalendar_register_push_subscription", new { p_device_id = deviceId, p_subscription = document.RootElement }, cancellationToken);
    }
    public async Task UnregisterAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        _ = await SendAsync("rest/v1/rpc/moicalendar_unregister_push_subscription", new { p_device_id = deviceId }, cancellationToken);

    private async Task<JsonElement> SendAsync(string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("apikey", publicKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAccessTokenAsync(cancellationToken));
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException((int)response.StatusCode switch
                {
                    401 or 403 => "请登录云账户并确认此设备未被撤销，再开启后台提醒。",
                    404 => "后台提醒服务尚未部署。",
                    429 => "开启提醒的请求过于频繁，请稍后重试。",
                    _ => "后台提醒登记失败，请确认后端迁移与推送密钥已配置，再重试。"
                });
            return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("无法连接后台提醒服务；本地提醒不受影响，请联网后重试。"); }
    }
    public void Dispose() => client.Dispose();
}
