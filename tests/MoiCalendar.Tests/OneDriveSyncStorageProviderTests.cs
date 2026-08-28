using System.Net;
using System.Text;
using System.Text.Json;
using MoiCalendar.Sync;
using MoiCalendar.Sync.OneDrive;

namespace MoiCalendar.Tests;

public sealed class OneDriveSyncStorageProviderTests
{
    [Fact]
    public async Task ConnectionTest_AccessesAppFolderAndVerifiesHelloFile()
    {
        var handler = new RecordingHandler(
            JsonResponse("{\"id\":\"personal-drive\"}"),
            JsonResponse("{\"name\":\"MoiCalendar\",\"eTag\":\"folder-tag\"}"),
            JsonResponse("{\"name\":\"hello.json\",\"eTag\":\"upload-tag\",\"size\":36}"),
            JsonResponse("{\"name\":\"hello.json\",\"eTag\":\"download-tag\",\"size\":36}"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OneDriveConnectionTester.TestContent, Encoding.UTF8)
            });
        var provider = CreateProvider(handler);
        var tester = new OneDriveConnectionTester(provider);

        var result = await tester.TestAsync();

        Assert.True(result.IsSuccess);
        Assert.Contains("hello.json", result.Message);
        Assert.Equal(
            [
                "GET https://graph.microsoft.com/v1.0/me/drive",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot",
                "PUT https://graph.microsoft.com/v1.0/me/drive/special/approot:/hello.json:/content",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot:/hello.json",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot:/hello.json:/content"
            ],
            handler.Requests.Select(request => $"{request.Method} {request.Uri}").ToArray());
        Assert.All(handler.Requests, request => Assert.Equal("test-access-token", request.BearerToken));
        Assert.Equal(OneDriveConnectionTester.TestContent, handler.Requests[2].Content);
    }

    [Fact]
    public async Task ConnectionTest_ReturnsFailureWhenDownloadedContentDoesNotMatch()
    {
        var handler = new RecordingHandler(
            JsonResponse("{\"id\":\"personal-drive\"}"),
            JsonResponse("{\"name\":\"MoiCalendar\"}"),
            JsonResponse("{\"name\":\"hello.json\"}"),
            JsonResponse("{\"name\":\"hello.json\"}"),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("different content")
            });
        var tester = new OneDriveConnectionTester(CreateProvider(handler));

        var result = await tester.TestAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("内容不一致", result.Message);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalseForMissingFile()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = CreateProvider(handler);

        var exists = await provider.ExistsAsync("folder/file.json");

        Assert.False(exists);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/me/drive/special/approot:/folder/file.json",
            handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task UploadTextAsync_SendsExpectedVersionToken()
    {
        var handler = new RecordingHandler(
            JsonResponse("{\"name\":\"hello.json\",\"eTag\":\"new-tag\"}"));
        var provider = CreateProvider(handler);

        var metadata = await provider.UploadTextAsync(
            "hello.json",
            "{}",
            expectedVersionToken: "old-tag");

        Assert.Equal("new-tag", metadata.VersionToken);
        Assert.Equal("old-tag", handler.Requests.Single().IfMatch);
    }

    [Fact]
    public async Task UploadTextAsync_WrapsInvalidGraphJson()
    {
        var handler = new RecordingHandler(JsonResponse("not-json"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<SyncStorageException>(
            () => provider.UploadTextAsync("hello.json", "{}"));

        Assert.Contains("格式无效", exception.Message);
    }

    [Fact]
    public async Task EnsureDirectoryAsync_CreatesNestedFoldersInsideAppFolder()
    {
        var handler = new RecordingHandler(
            JsonResponse("{\"id\":\"app-root-id\",\"name\":\"MoiCalendar\"}"),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            JsonResponse("{\"id\":\"calendar-id\",\"name\":\"MoiCalendar\",\"folder\":{}}"),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            JsonResponse("{\"id\":\"operations-id\",\"name\":\"operations\",\"folder\":{}}"));
        var provider = CreateProvider(handler);

        await provider.EnsureDirectoryAsync("MoiCalendar/operations");

        Assert.Equal(
            [
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot:/MoiCalendar",
                "POST https://graph.microsoft.com/v1.0/me/drive/items/app-root-id/children",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot:/MoiCalendar/operations",
                "POST https://graph.microsoft.com/v1.0/me/drive/items/calendar-id/children"
            ],
            handler.Requests.Select(request => $"{request.Method} {request.Uri}").ToArray());
        Assert.Equal(
            ["MoiCalendar", "operations"],
            handler.Requests
                .Where(request => request.Method == "POST")
                .Select(request => JsonDocument.Parse(request.Content!).RootElement.GetProperty("name").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task EnsureDirectoryAsync_ResolvesConcurrentFolderCreationWithoutDuplicate()
    {
        var handler = new RecordingHandler(
            JsonResponse("{\"id\":\"app-root-id\"}"),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.Conflict),
            JsonResponse("{\"id\":\"calendar-id\",\"name\":\"MoiCalendar\",\"folder\":{}}"));
        var provider = CreateProvider(handler);

        await provider.EnsureDirectoryAsync("MoiCalendar");

        Assert.Equal(
            [
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot:/MoiCalendar",
                "POST https://graph.microsoft.com/v1.0/me/drive/items/app-root-id/children",
                "GET https://graph.microsoft.com/v1.0/me/drive/special/approot:/MoiCalendar"
            ],
            handler.Requests.Select(request => $"{request.Method} {request.Uri}").ToArray());
    }

    [Fact]
    public async Task ListFilesAsync_FollowsTrustedGraphPagination()
    {
        var handler = new RecordingHandler(
            JsonResponse(
                "{\"value\":[{\"name\":\"a.json\",\"size\":1}]," +
                "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/next-page\"}"),
            JsonResponse("{\"value\":[{\"name\":\"b.json\",\"size\":2}]}"));
        var provider = CreateProvider(handler);

        var files = await provider.ListFilesAsync("MoiCalendar/operations");

        Assert.Equal(
            ["MoiCalendar/operations/a.json", "MoiCalendar/operations/b.json"],
            files.Select(file => file.Path).ToArray());
        Assert.Equal(
            [
                "https://graph.microsoft.com/v1.0/me/drive/special/approot:/MoiCalendar/operations:/children",
                "https://graph.microsoft.com/v1.0/next-page"
            ],
            handler.Requests.Select(request => request.Uri).ToArray());
    }

    [Fact]
    public async Task ListFilesAsync_RejectsUntrustedPaginationUrl()
    {
        var handler = new RecordingHandler(JsonResponse(
            "{\"value\":[],\"@odata.nextLink\":\"https://evil.example.test/steal\"}"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<SyncStorageException>(
            () => provider.ListFilesAsync("MoiCalendar/operations"));

        Assert.Contains("不安全", exception.Message);
        Assert.Single(handler.Requests);
    }

    private static OneDriveSyncStorageProvider CreateProvider(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri(OneDriveGraphSettings.BaseUrl)
            },
            new FakeTokenProvider());

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeTokenProvider : IOneDriveAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-access-token");
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

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
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null,
                content));

            return responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? BearerToken,
        string? IfMatch,
        string? Content);
}
