using System.Net;
using System.Text;
using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class SupabaseCloudSyncTransportTests
{
    [Fact]
    public async Task Push_MapsMutationToAuthenticatedRpc()
    {
        var mutationId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var handler = new RecordingHandler(JsonResponse($$"""
            {
              "status": "applied",
              "mutation_id": "{{mutationId}}",
              "entity_id": "{{entityId}}",
              "server_revision": 17
            }
            """));
        using var transport = CreateTransport(handler);
        var calendarEvent = CreateEvent(entityId);

        var response = await transport.PushAsync(new CloudMutationRequest(
            mutationId,
            Guid.NewGuid(),
            entityId,
            SyncOperationType.Update,
            16,
            JsonSerializer.Serialize(calendarEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

        Assert.True(response.Applied);
        Assert.Equal(17, response.ServerRevision);
        Assert.Equal("Bearer test-access-token", handler.Authorization);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.EndsWith("/rest/v1/rpc/moicalendar_apply_calendar_mutation", handler.Uri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Body!);
        var mutation = body.RootElement.GetProperty("p_mutation");
        Assert.Equal("update", mutation.GetProperty("operation").GetString());
        Assert.Equal(16, mutation.GetProperty("base_revision").GetInt64());
    }

    [Fact]
    public async Task Push_MapsStructuredConflict()
    {
        var mutationId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var handler = new RecordingHandler(JsonResponse($$"""
            {
              "status": "conflict",
              "mutation_id": "{{mutationId}}",
              "entity_id": "{{entityId}}",
              "conflict": {
                "code": "stale_revision",
                "current_revision": 22,
                "current_entity": {
                  "id": "{{entityId}}",
                  "title": "远端版本",
                  "description": "",
                  "location": "",
                  "startUtc": "2026-09-05T01:00:00Z",
                  "endUtc": "2026-09-05T02:00:00Z",
                  "timeZoneId": "UTC",
                  "isAllDay": false,
                  "createdAtUtc": "2026-09-05T00:00:00Z",
                  "updatedAtUtc": "2026-09-05T03:00:00Z",
                  "deletedAtUtc": null
                }
              }
            }
            """));
        using var transport = CreateTransport(handler);

        var response = await transport.PushAsync(new CloudMutationRequest(
            mutationId, Guid.NewGuid(), entityId, SyncOperationType.Delete, 20, "{}"));

        Assert.False(response.Applied);
        Assert.Equal("stale_revision", response.ConflictCode);
        Assert.Equal(22, response.CurrentServerRevision);
        Assert.Equal("远端版本", response.CurrentEntity?.Title);
    }

    [Fact]
    public async Task Pull_MapsTombstoneAndServerCursor()
    {
        var entityId = Guid.NewGuid();
        var handler = new RecordingHandler(JsonResponse($$"""
            {
              "cursor": 31,
              "has_more": false,
              "changes": [{
                "server_revision": 31,
                "calendar_event": {
                  "id": "{{entityId}}",
                  "title": "已删除",
                  "description": "",
                  "location": "",
                  "startUtc": "2026-09-05T01:00:00Z",
                  "endUtc": "2026-09-05T02:00:00Z",
                  "timeZoneId": "UTC",
                  "isAllDay": false,
                  "createdAtUtc": "2026-09-05T00:00:00Z",
                  "updatedAtUtc": "2026-09-05T03:00:00Z",
                  "deletedAtUtc": "2026-09-05T03:00:00Z"
                }
              }]
            }
            """));
        using var transport = CreateTransport(handler);

        var deviceId = Guid.NewGuid();
        var batch = await transport.PullAsync(deviceId, 12, 100);

        Assert.Equal(31, batch.Cursor);
        Assert.False(batch.HasMore);
        var change = Assert.Single(batch.Changes);
        Assert.Equal(31, change.ServerRevision);
        Assert.NotNull(change.CalendarEvent.DeletedAtUtc);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(deviceId, body.RootElement.GetProperty("p_device_id").GetGuid());
        Assert.EndsWith(
            "/rest/v1/rpc/moicalendar_pull_calendar_changes_for_device",
            handler.Uri,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CloudSyncFailureKind.AuthenticationRequired)]
    [InlineData(HttpStatusCode.TooManyRequests, CloudSyncFailureKind.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CloudSyncFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, CloudSyncFailureKind.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, CloudSyncFailureKind.Permanent)]
    public async Task RpcFailure_IsClassifiedWithoutExposingResponseBody(
        HttpStatusCode statusCode,
        CloudSyncFailureKind expectedKind)
    {
        const string sensitiveBody = "{\"message\":\"Bearer secret-access-token\"}";
        var handler = new RecordingHandler(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(sensitiveBody, Encoding.UTF8, "application/json")
        });
        using var transport = CreateTransport(handler);

        var exception = await Assert.ThrowsAsync<CloudSyncTransportException>(() =>
            transport.PullAsync(Guid.NewGuid(), 0, 100));

        Assert.Equal(expectedKind, exception.FailureKind);
        Assert.Equal((int)statusCode, exception.StatusCode);
        Assert.DoesNotContain("secret-access-token", exception.Message, StringComparison.Ordinal);
    }

    private static SupabaseCloudSyncTransport CreateTransport(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://example.supabase.co/rest/v1/") },
        "public-key",
        new FakeAccessTokenProvider());

    private static CalendarEvent CreateEvent(Guid id) => new()
    {
        Id = id,
        Title = "事件",
        Description = string.Empty,
        Location = string.Empty,
        StartUtc = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 9, 5, 2, 0, 0, TimeSpan.Zero),
        TimeZoneId = "UTC",
        IsAllDay = false,
        CreatedAtUtc = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero)
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeAccessTokenProvider : ISupabaseAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("test-access-token");
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.AbsoluteUri;
            ApiKey = request.Headers.GetValues("apikey").Single();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
