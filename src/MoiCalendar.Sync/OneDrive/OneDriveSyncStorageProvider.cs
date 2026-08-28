using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoiCalendar.Sync.OneDrive;

public sealed class OneDriveSyncStorageProvider(
    HttpClient httpClient,
    IOneDriveAccessTokenProvider accessTokenProvider) : ISyncStorageProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        // Temporary workaround for a personal OneDrive App Folder provisioning issue.
        // Remove this request together with TemporaryBootstrapScope after approot succeeds.
        using var driveResponse = await SendAsync(
            HttpMethod.Get,
            "me/drive",
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(driveResponse, "初始化个人 OneDrive", cancellationToken);

        using var response = await SendAsync(
            HttpMethod.Get,
            "me/drive/special/approot",
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(response, "访问 OneDrive 应用文件夹", cancellationToken);
        return true;
    }

    public async Task EnsureDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(directoryPath, allowEmpty: true);
        if (normalized.Length == 0)
        {
            return;
        }

        using var appRootResponse = await SendAsync(
            HttpMethod.Get,
            "me/drive/special/approot",
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(appRootResponse, "访问 OneDrive 应用文件夹", cancellationToken);
        var appRoot = await ReadDriveItemAsync(appRootResponse, cancellationToken);
        var parentItemId = RequireItemId(appRoot, "OneDrive 应用文件夹");
        var parentPath = string.Empty;
        foreach (var segment in normalized.Split('/'))
        {
            var currentPath = CombinePath(parentPath, segment);
            using var existingResponse = await SendAsync(
                HttpMethod.Get,
                BuildItemPath(currentPath),
                cancellationToken: cancellationToken);

            if (existingResponse.StatusCode == HttpStatusCode.NotFound)
            {
                using var content = JsonContent.Create(new Dictionary<string, object>
                {
                    ["name"] = segment,
                    ["folder"] = new { },
                    ["@microsoft.graph.conflictBehavior"] = "fail"
                });
                using var createResponse = await SendAsync(
                    HttpMethod.Post,
                    BuildChildrenPath(parentItemId),
                    content,
                    cancellationToken: cancellationToken);

                if (createResponse.StatusCode == HttpStatusCode.Conflict)
                {
                    using var concurrentResponse = await SendAsync(
                        HttpMethod.Get,
                        BuildItemPath(currentPath),
                        cancellationToken: cancellationToken);
                    await EnsureSuccessAsync(
                        concurrentResponse,
                        "读取并发创建的 OneDrive 同步目录",
                        cancellationToken);
                    var concurrentItem = await ReadDriveItemAsync(
                        concurrentResponse,
                        cancellationToken);
                    parentItemId = RequireItemId(concurrentItem, "OneDrive 同步目录");
                }
                else
                {
                    await EnsureSuccessAsync(createResponse, "创建 OneDrive 同步目录", cancellationToken);
                    var createdItem = await ReadDriveItemAsync(createResponse, cancellationToken);
                    parentItemId = RequireItemId(createdItem, "OneDrive 同步目录");
                }
            }
            else
            {
                await EnsureSuccessAsync(existingResponse, "检查 OneDrive 同步目录", cancellationToken);
                var existingItem = await ReadDriveItemAsync(existingResponse, cancellationToken);
                parentItemId = RequireItemId(existingItem, "OneDrive 同步目录");
            }

            parentPath = currentPath;
        }
    }

    public async Task<bool> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            BuildItemPath(path),
            cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "检查 OneDrive 文件", cancellationToken);
        return true;
    }

    public async Task<SyncFileMetadata> UploadTextAsync(
        string path,
        string content,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var requestContent = new StringContent(content, Encoding.UTF8, "application/json");
        using var response = await SendAsync(
            HttpMethod.Put,
            $"{BuildItemPath(path)}:/content",
            requestContent,
            expectedVersionToken,
            cancellationToken);
        await EnsureSuccessAsync(response, "上传 OneDrive 文件", cancellationToken);

        var item = await ReadDriveItemAsync(response, cancellationToken);
        return ToMetadata(path, item);
    }

    public async Task<SyncTextFile?> DownloadTextAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var metadataResponse = await SendAsync(
            HttpMethod.Get,
            BuildItemPath(path),
            cancellationToken: cancellationToken);

        if (metadataResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(metadataResponse, "读取 OneDrive 文件信息", cancellationToken);
        var item = await ReadDriveItemAsync(metadataResponse, cancellationToken);

        using var contentResponse = await SendAsync(
            HttpMethod.Get,
            $"{BuildItemPath(path)}:/content",
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(contentResponse, "下载 OneDrive 文件", cancellationToken);

        string content;
        try
        {
            content = await contentResponse.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SyncStorageException("无法读取 OneDrive 文件内容。", exception);
        }

        return new SyncTextFile(path, content, item.ETag, item.LastModifiedDateTime);
    }

    public async Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedDirectory = NormalizePath(directoryPath, allowEmpty: true);
        string? requestPath = normalizedDirectory.Length == 0
            ? "me/drive/special/approot/children"
            : $"me/drive/special/approot:/{EscapePath(normalizedDirectory)}:/children";
        var files = new List<SyncFileMetadata>();

        while (requestPath is not null)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                requestPath,
                cancellationToken: cancellationToken);
            await EnsureSuccessAsync(response, "列出 OneDrive 文件", cancellationToken);

            try
            {
                var result = await response.Content.ReadFromJsonAsync<DriveItemCollection>(
                    JsonOptions,
                    cancellationToken);
                files.AddRange((result?.Value ?? []).Select(item => ToMetadata(
                    CombinePath(normalizedDirectory, item.Name ?? string.Empty),
                    item)));
                requestPath = ValidateNextLink(result?.NextLink);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new SyncStorageException("OneDrive 返回的文件列表格式无效。", exception);
            }
        }

        return files;
    }

    public async Task<bool> DeleteAsync(
        string path,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            BuildItemPath(path),
            expectedVersionToken: expectedVersionToken,
            cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "删除 OneDrive 文件", cancellationToken);
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string requestPath,
        HttpContent? content = null,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default)
    {
        var token = await accessTokenProvider.GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, requestPath)
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrWhiteSpace(expectedVersionToken))
        {
            request.Headers.TryAddWithoutValidation("If-Match", expectedVersionToken);
        }

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SyncStorageException("无法连接 Microsoft Graph。", exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        if (details.Length > 300)
        {
            details = details[..300];
        }

        throw new SyncStorageException(
            $"{operation}失败（HTTP {(int)response.StatusCode}）" +
            (string.IsNullOrWhiteSpace(details) ? "。" : $"：{details}"));
    }

    private static async Task<DriveItem> ReadDriveItemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<DriveItem>(JsonOptions, cancellationToken)
                ?? throw new SyncStorageException("OneDrive 返回了空的文件信息。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SyncStorageException("OneDrive 返回的文件信息格式无效。", exception);
        }
    }

    private static SyncFileMetadata ToMetadata(string path, DriveItem item) =>
        new(path, item.ETag, item.Size, item.LastModifiedDateTime);

    private static string BuildItemPath(string path) =>
        $"me/drive/special/approot:/{EscapePath(NormalizePath(path, allowEmpty: false))}";

    private static string BuildChildrenPath(string parentItemId) =>
        $"me/drive/items/{Uri.EscapeDataString(parentItemId)}/children";

    private static string RequireItemId(DriveItem item, string itemDescription) =>
        string.IsNullOrWhiteSpace(item.Id)
            ? throw new SyncStorageException($"{itemDescription}缺少必要的项目 ID。")
            : item.Id;

    private static string NormalizePath(string path, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (!allowEmpty && normalized.Length == 0)
        {
            throw new ArgumentException("同步文件路径不能为空。", nameof(path));
        }

        if (normalized.Split('/').Any(segment => segment is "." or ".." || segment.Length == 0))
        {
            throw new ArgumentException("同步文件路径包含无效的路径段。", nameof(path));
        }

        return normalized;
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string CombinePath(string directoryPath, string name) =>
        directoryPath.Length == 0 ? name : $"{directoryPath}/{name}";

    private static string? ValidateNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
        {
            return null;
        }

        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncStorageException("OneDrive 返回了不安全的下一页地址，已停止继续请求。");
        }

        return uri.AbsoluteUri;
    }

    private sealed record DriveItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("eTag")] string? ETag,
        [property: JsonPropertyName("size")] long? Size,
        [property: JsonPropertyName("lastModifiedDateTime")] DateTimeOffset? LastModifiedDateTime);

    private sealed record DriveItemCollection(
        [property: JsonPropertyName("value")] IReadOnlyList<DriveItem>? Value,
        [property: JsonPropertyName("@odata.nextLink")] string? NextLink);
}
