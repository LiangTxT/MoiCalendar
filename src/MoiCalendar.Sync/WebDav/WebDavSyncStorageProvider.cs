using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MoiCalendar.Sync.WebDav;

public sealed class WebDavSyncStorageProvider : ISyncStorageProvider
{
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod MakeCollectionMethod = new("MKCOL");
    private static readonly XNamespace DavNamespace = "DAV:";
    private const string PropFindBody =
        """
        <?xml version="1.0" encoding="utf-8" ?>
        <d:propfind xmlns:d="DAV:">
          <d:prop>
            <d:resourcetype />
            <d:getetag />
            <d:getcontentlength />
            <d:getlastmodified />
          </d:prop>
        </d:propfind>
        """;

    private readonly HttpClient httpClient;
    private readonly Uri remoteRootUri;
    private readonly AuthenticationHeaderValue authorization;

    public WebDavSyncStorageProvider(HttpClient httpClient, WebDavSettings settings)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(settings);

        remoteRootUri = BuildRemoteRootUri(settings);
        authorization = BuildBasicAuthorization(settings);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendPropFindAsync(remoteRootUri, "0", cancellationToken);
        await EnsureSuccessAsync(response, "测试 WebDAV 连接", cancellationToken);
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

        var currentPath = string.Empty;
        foreach (var segment in normalized.Split('/'))
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}/{segment}";
            using var response = await SendAsync(
                MakeCollectionMethod,
                BuildResourceUri(currentPath, asDirectory: true),
                cancellationToken: cancellationToken);

            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
            {
                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    continue;
                }

                throw new SyncStorageException("创建 WebDAV 同步目录失败：父目录不存在或服务器拒绝创建目录。");
            }

            await EnsureSuccessAsync(response, "创建 WebDAV 同步目录", cancellationToken);
        }
    }

    public async Task<bool> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendPropFindAsync(
            BuildResourceUri(path),
            "0",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "检查 WebDAV 文件", cancellationToken);
        return true;
    }

    public async Task<SyncFileMetadata> UploadTextAsync(
        string path,
        string content,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path, allowEmpty: false);
        ArgumentNullException.ThrowIfNull(content);

        using var requestContent = new StringContent(content, Encoding.UTF8, "application/json");
        using var response = await SendAsync(
            HttpMethod.Put,
            BuildResourceUri(normalizedPath),
            requestContent,
            expectedVersionToken,
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(response, "上传 WebDAV 文件", cancellationToken);

        return new SyncFileMetadata(
            normalizedPath,
            response.Headers.ETag?.ToString(),
            Encoding.UTF8.GetByteCount(content),
            response.Content.Headers.LastModified);
    }

    public async Task<SyncTextFile?> DownloadTextAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path, allowEmpty: false);
        using var response = await SendAsync(
            HttpMethod.Get,
            BuildResourceUri(normalizedPath),
            cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "下载 WebDAV 文件", cancellationToken);

        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new SyncTextFile(
                normalizedPath,
                content,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SyncStorageException("无法读取 WebDAV 文件内容。", exception);
        }
    }

    public async Task<IReadOnlyList<SyncFileMetadata>> ListFilesAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedDirectory = NormalizePath(directoryPath, allowEmpty: true);
        var directoryUri = normalizedDirectory.Length == 0
            ? remoteRootUri
            : BuildResourceUri(normalizedDirectory, asDirectory: true);

        using var response = await SendPropFindAsync(directoryUri, "1", cancellationToken);
        await EnsureSuccessAsync(response, "列出 WebDAV 文件", cancellationToken);

        var xml = await ReadXmlAsync(response, cancellationToken);
        return ParseFileList(xml, directoryUri, normalizedDirectory);
    }

    public async Task<bool> DeleteAsync(
        string path,
        string? expectedVersionToken = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            BuildResourceUri(path),
            expectedVersionToken: expectedVersionToken,
            cancellationToken: cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "删除 WebDAV 文件", cancellationToken);
        return true;
    }

    private Task<HttpResponseMessage> SendPropFindAsync(
        Uri uri,
        string depth,
        CancellationToken cancellationToken)
    {
        var content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml");
        return SendAsync(
            PropFindMethod,
            uri,
            content,
            additionalHeaderName: "Depth",
            additionalHeaderValue: depth,
            cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content = null,
        string? expectedVersionToken = null,
        string? additionalHeaderName = null,
        string? additionalHeaderValue = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = content
        };
        request.Headers.Authorization = authorization;

        if (!string.IsNullOrWhiteSpace(expectedVersionToken))
        {
            request.Headers.TryAddWithoutValidation("If-Match", expectedVersionToken);
        }

        if (additionalHeaderName is not null && additionalHeaderValue is not null)
        {
            request.Headers.TryAddWithoutValidation(additionalHeaderName, additionalHeaderValue);
        }

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw CreateNetworkException(exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncStorageException(
                "WebDAV 请求超时。请检查服务器地址、HTTPS 证书、网络连接和浏览器 CORS 配置。",
                exception);
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

        var guidance = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "请检查用户名和密码，建议使用应用专用密码。",
            HttpStatusCode.Forbidden => "服务器拒绝访问，请检查账户权限和远端目录权限。",
            HttpStatusCode.PreconditionFailed => "远端文件已被其他客户端修改，请重新读取后再试。",
            HttpStatusCode.MethodNotAllowed => "服务器不允许此 WebDAV 方法，请检查 WebDAV 和 CORS 配置。",
            _ => null
        };

        throw new SyncStorageException(
            $"{operation}失败（HTTP {(int)response.StatusCode}）" +
            (guidance is null ? string.Empty : $"：{guidance}") +
            (string.IsNullOrWhiteSpace(details) ? string.Empty : $" 服务器响应：{details}"));
    }

    private static SyncStorageException CreateNetworkException(HttpRequestException exception)
    {
        var message = exception.ToString();
        var looksLikeBrowserCorsFailure =
            message.Contains("Failed to fetch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("NetworkError", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Load failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("CORS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TypeError", StringComparison.OrdinalIgnoreCase);

        var explanation = looksLikeBrowserCorsFailure
            ? "浏览器阻止了 WebDAV 请求。这通常是服务器未允许 CORS 预检、Authorization 请求头或 PROPFIND/PUT/DELETE 方法。"
            : "无法连接 WebDAV 服务器。可能原因包括 CORS、网络中断、DNS 错误或 HTTPS 证书无效。";

        return new SyncStorageException(
            $"{explanation} 请检查浏览器开发者工具的控制台和网络面板；此问题通常需要在 WebDAV 服务器端解决。",
            exception);
    }

    private static async Task<XDocument> ReadXmlAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
            return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new SyncStorageException("WebDAV 返回的 XML 文件列表格式无效。", exception);
        }
    }

    private IReadOnlyList<SyncFileMetadata> ParseFileList(
        XDocument xml,
        Uri directoryUri,
        string normalizedDirectory)
    {
        var directoryPath = EnsureTrailingSlash(Uri.UnescapeDataString(directoryUri.AbsolutePath));
        var files = new List<SyncFileMetadata>();

        foreach (var responseElement in xml.Descendants(DavNamespace + "response"))
        {
            var hrefValue = responseElement.Element(DavNamespace + "href")?.Value;
            var successfulProperties = responseElement
                .Elements(DavNamespace + "propstat")
                .Where(IsSuccessfulPropStat)
                .Select(element => element.Element(DavNamespace + "prop"))
                .FirstOrDefault(element => element is not null);

            if (string.IsNullOrWhiteSpace(hrefValue) || successfulProperties is null)
            {
                continue;
            }

            var itemUri = Uri.TryCreate(hrefValue, UriKind.Absolute, out var absoluteUri)
                ? absoluteUri
                : new Uri(directoryUri, hrefValue);
            var itemPath = Uri.UnescapeDataString(itemUri.AbsolutePath);

            if (!itemPath.StartsWith(directoryPath, StringComparison.Ordinal) ||
                string.Equals(
                    itemPath.TrimEnd('/'),
                    directoryPath.TrimEnd('/'),
                    StringComparison.Ordinal) ||
                successfulProperties
                    .Element(DavNamespace + "resourcetype")?
                    .Element(DavNamespace + "collection") is not null)
            {
                continue;
            }

            var relativePath = itemPath[directoryPath.Length..].Trim('/');
            if (relativePath.Length == 0 || relativePath.Contains('/'))
            {
                continue;
            }

            var path = normalizedDirectory.Length == 0
                ? relativePath
                : $"{normalizedDirectory}/{relativePath}";
            files.Add(new SyncFileMetadata(
                path,
                OptionalValue(successfulProperties.Element(DavNamespace + "getetag")?.Value),
                ParseNullableLong(successfulProperties.Element(DavNamespace + "getcontentlength")?.Value),
                ParseNullableDate(successfulProperties.Element(DavNamespace + "getlastmodified")?.Value)));
        }

        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private Uri BuildResourceUri(string path, bool asDirectory = false)
    {
        var normalizedPath = NormalizePath(path, allowEmpty: false);
        var escapedPath = string.Join('/', normalizedPath.Split('/').Select(Uri.EscapeDataString));
        return new Uri(remoteRootUri, asDirectory ? $"{escapedPath}/" : escapedPath);
    }

    private static Uri BuildRemoteRootUri(WebDavSettings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "WebDAV BaseUrl 必须是没有查询参数或片段的绝对 HTTPS URL。",
                nameof(settings));
        }

        var normalizedRemotePath = NormalizePath(settings.RemotePath ?? string.Empty, allowEmpty: true);
        var builder = new UriBuilder(baseUri)
        {
            Path = EnsureTrailingSlash(baseUri.AbsolutePath) +
                (normalizedRemotePath.Length == 0
                    ? string.Empty
                    : string.Join('/', normalizedRemotePath.Split('/').Select(Uri.EscapeDataString)) + "/")
        };
        return builder.Uri;
    }

    private static AuthenticationHeaderValue BuildBasicAuthorization(WebDavSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            throw new ArgumentException("WebDAV 用户名不能为空。", nameof(settings));
        }

        if (string.IsNullOrEmpty(settings.Password))
        {
            throw new ArgumentException("WebDAV 密码不能为空。建议使用应用专用密码。", nameof(settings));
        }

        var credentialBytes = Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentialBytes));
    }

    private static string NormalizePath(string path, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');

        if (normalized.Length == 0)
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new ArgumentException("同步文件路径不能为空。", nameof(path));
        }

        if (normalized.Split('/').Any(segment => segment is "." or ".." || segment.Length == 0))
        {
            throw new ArgumentException("同步文件路径包含无效的路径段。", nameof(path));
        }

        return normalized;
    }

    private static bool IsSuccessfulPropStat(XElement propStat)
    {
        var status = propStat.Element(DavNamespace + "status")?.Value;
        return status?.Contains(" 200 ", StringComparison.Ordinal) == true;
    }

    private static long? ParseNullableLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out var result)
                ? result
                : null;

    private static string? OptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";
}
