using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MoiCalendar.Sync;
using MoiCalendar.Sync.WebDav;

namespace MoiCalendar.Tests;

public sealed class WebDavSyncStorageProviderTests
{
    private static readonly WebDavSettings Settings = new(
        "https://dav.example.test/root/",
        "calendar-user",
        "app-password",
        "Moi Calendar");

    [Fact]
    public void Constructor_RequiresHttpsAndSettingsDoNotRevealPassword()
    {
        var insecureSettings = new WebDavSettings(
            "http://dav.example.test/",
            "user",
            "secret-value",
            "calendar");

        var exception = Assert.Throws<ArgumentException>(
            () => new WebDavSyncStorageProvider(new HttpClient(), insecureSettings));

        Assert.Contains("HTTPS", exception.Message);
        Assert.DoesNotContain("secret-value", insecureSettings.ToString());
    }

    [Fact]
    public async Task TestConnectionAsync_UsesDepthZeroPropFindAndBasicAuthentication()
    {
        var handler = new RecordingHandler(MultiStatusResponse("<d:multistatus xmlns:d=\"DAV:\" />"));
        ISyncStorageProvider provider = CreateProvider(handler);

        var connected = await provider.TestConnectionAsync();

        Assert.True(connected);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method);
        Assert.Equal("https://dav.example.test/root/Moi%20Calendar/", request.Uri);
        Assert.Equal("0", request.Depth);
        Assert.Equal("Basic", request.AuthorizationScheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("calendar-user:app-password")),
            request.AuthorizationParameter);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalseForMissingFile()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = CreateProvider(handler);

        var exists = await provider.ExistsAsync("folder/hello.json");

        Assert.False(exists);
        Assert.Equal(
            "https://dav.example.test/root/Moi%20Calendar/folder/hello.json",
            handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task UploadTextAsync_UsesPutAndConditionalVersionToken()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created);
        response.Headers.ETag = new EntityTagHeaderValue("\"new-version\"");
        response.Content = new ByteArrayContent([]);
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 27, 8, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(response);
        var provider = CreateProvider(handler);

        var metadata = await provider.UploadTextAsync(
            "hello.json",
            "{\"hello\":\"world\"}",
            "\"old-version\"");

        Assert.Equal("hello.json", metadata.Path);
        Assert.Equal("\"new-version\"", metadata.VersionToken);
        Assert.Equal(17, metadata.Size);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("\"old-version\"", request.IfMatch);
        Assert.Equal("{\"hello\":\"world\"}", request.Content);
    }

    [Fact]
    public async Task DownloadTextAsync_ReturnsContentAndMetadata()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("downloaded", Encoding.UTF8, "application/json")
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"version-2\"");
        response.Content.Headers.LastModified = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        var provider = CreateProvider(new RecordingHandler(response));

        var file = await provider.DownloadTextAsync("hello.json");

        Assert.NotNull(file);
        Assert.Equal("downloaded", file.Content);
        Assert.Equal("\"version-2\"", file.VersionToken);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero), file.LastModifiedUtc);
    }

    [Fact]
    public async Task ListFilesAsync_ParsesMultiStatusAndExcludesCollections()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/root/Moi%20Calendar/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/root/Moi%20Calendar/b.json</d:href>
                <d:propstat><d:prop><d:resourcetype /><d:getetag>"b-tag"</d:getetag><d:getcontentlength>12</d:getcontentlength><d:getlastmodified>Thu, 27 Aug 2026 08:00:00 GMT</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/root/Moi%20Calendar/a%20file.json</d:href>
                <d:propstat><d:prop><d:resourcetype /><d:getetag>"a-tag"</d:getetag><d:getcontentlength>5</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/root/Moi%20Calendar/archive/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection /></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        var handler = new RecordingHandler(MultiStatusResponse(xml));
        var provider = CreateProvider(handler);

        var files = await provider.ListFilesAsync(string.Empty);

        Assert.Equal(["a file.json", "b.json"], files.Select(file => file.Path).ToArray());
        Assert.Equal(5, files[0].Size);
        Assert.Equal("\"b-tag\"", files[1].VersionToken);
        Assert.Equal("1", handler.Requests.Single().Depth);
    }

    [Fact]
    public async Task DeleteAsync_UsesDeleteAndVersionToken()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var provider = CreateProvider(handler);

        var deleted = await provider.DeleteAsync("hello.json", "\"version-3\"");

        Assert.True(deleted);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("\"version-3\"", request.IfMatch);
    }

    [Fact]
    public async Task BrowserNetworkFailure_ProducesReadableCorsGuidance()
    {
        var provider = CreateProvider(new RecordingHandler(
            new HttpRequestException("TypeError: Failed to fetch")));

        var exception = await Assert.ThrowsAsync<SyncStorageException>(
            () => provider.TestConnectionAsync());

        Assert.Contains("CORS", exception.Message);
        Assert.Contains("PROPFIND", exception.Message);
        Assert.Contains("浏览器", exception.Message);
    }

    private static WebDavSyncStorageProvider CreateProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Settings);

    private static HttpResponseMessage MultiStatusResponse(string xml) =>
        new((HttpStatusCode)207)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = [];
        private readonly Exception? exception;

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses)
            {
                this.responses.Enqueue(response);
            }
        }

        public RecordingHandler(Exception exception)
        {
            this.exception = exception;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("Depth", out var depth) ? depth.Single() : null,
                request.Headers.TryGetValues("If-Match", out var ifMatch) ? ifMatch.Single() : null,
                content));

            if (exception is not null)
            {
                throw exception;
            }

            return responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string? Depth,
        string? IfMatch,
        string? Content);
}
