using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal sealed class SupabaseAccountDataTransport(
    HttpClient httpClient,
    string publicKey,
    ISupabaseAccessTokenProvider accessTokenProvider) : ICloudAccountDataTransport, IDisposable
{
    private const long MaximumExportResponseSize = 20 * 1024 * 1024;
    private const long MaximumErrorResponseSize = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsAvailable => true;

    public async Task<CloudAccountExportDocument> ExportAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            "rest/v1/rpc/moicalendar_export_account_data",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentLength > MaximumExportResponseSize)
        {
            throw new AccountDataException(
                "云账户数据导出超过安全大小限制。",
                AccountDataFailureKind.Permanent);
        }

        try
        {
            await response.Content.LoadIntoBufferAsync(
                MaximumExportResponseSize,
                cancellationToken);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<CloudAccountExportDocument>(
                    contentStream,
                    JsonOptions,
                    cancellationToken)
                ?? throw new AccountDataException(
                    "云端返回了空的数据导出。",
                    AccountDataFailureKind.Permanent);
        }
        catch (JsonException exception)
        {
            throw new AccountDataException(
                "云端返回了无法识别的数据导出。",
                exception,
                AccountDataFailureKind.Permanent);
        }
        catch (HttpRequestException exception)
        {
            throw new AccountDataException(
                "云账户数据导出超过安全大小限制或传输不完整。",
                exception,
                AccountDataFailureKind.Permanent);
        }
    }

    public async Task DeleteCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync("functions/v1/delete-account", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void Dispose() => httpClient.Dispose();

    private async Task<HttpResponseMessage> SendAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        }
        catch (AccountServiceException exception)
        {
            throw new AccountDataException(
                "云账户会话不可用，请重新登录。",
                exception,
                AccountDataFailureKind.AuthenticationRequired);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        request.Headers.TryAddWithoutValidation("apikey", publicKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new { }, options: JsonOptions);

        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountDataException(
                "云账户请求超时，请稍后重试。",
                AccountDataFailureKind.Transient);
        }
        catch (HttpRequestException exception)
        {
            throw new AccountDataException(
                "无法连接云账户服务，请检查网络后重试。",
                exception,
                AccountDataFailureKind.Transient);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var statusCode = (int)response.StatusCode;
        string? providerCode = null;
        if (response.Content.Headers.ContentLength is null or <= MaximumErrorResponseSize)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                providerCode = JsonSerializer.Deserialize<ProviderError>(body, JsonOptions)?.Code;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Provider bodies are intentionally not surfaced or logged.
            }
        }

        if (statusCode == 409 &&
            string.Equals(providerCode, "recent_authentication_required", StringComparison.Ordinal))
        {
            throw new AccountDataException(
                "为保护账户，请退出后重新登录，再立即重试删除。",
                AccountDataFailureKind.RecentAuthenticationRequired);
        }

        var failureKind = statusCode is 401 or 403
            ? AccountDataFailureKind.AuthenticationRequired
            : statusCode is 408 or 429 || statusCode >= 500
                ? AccountDataFailureKind.Transient
                : AccountDataFailureKind.Permanent;
        var message = failureKind switch
        {
            AccountDataFailureKind.AuthenticationRequired => "云账户会话已失效，请重新登录。",
            AccountDataFailureKind.Transient => "云账户服务暂时不可用，请稍后重试。",
            _ => $"云账户请求失败（HTTP {statusCode}）。"
        };
        throw new AccountDataException(message, failureKind);
    }

    private sealed record ProviderError(string? Code);
}
