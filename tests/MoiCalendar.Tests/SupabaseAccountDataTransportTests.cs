using System.Net;
using System.Text;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class SupabaseAccountDataTransportTests
{
    [Fact]
    public async Task Export_UsesAuthenticatedOwnerRpcAndMapsPortableDocument()
    {
        var handler = new RecordingHandler(JsonResponse(ExportJson()));
        using var transport = CreateTransport(handler);

        var export = await transport.ExportAsync();

        Assert.Equal("moicalendar-cloud-export", export.Format);
        Assert.Equal("Bearer test-access-token", handler.Authorization);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.EndsWith(
            "/rest/v1/rpc/moicalendar_export_account_data",
            handler.Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_DoesNotSendTargetUserIdOrCredentialsInBody()
    {
        var handler = new RecordingHandler(JsonResponse("{\"deleted\":true}"));
        using var transport = CreateTransport(handler);

        await transport.DeleteCurrentAccountAsync();

        Assert.EndsWith("/functions/v1/delete-account", handler.Uri, StringComparison.Ordinal);
        Assert.Equal("{}", handler.Body);
        Assert.DoesNotContain("user", handler.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_MapsRecentAuthenticationWithoutExposingProviderBody()
    {
        const string sensitiveDetails = "provider-token-details";
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                $$"""{"code":"recent_authentication_required","details":"{{sensitiveDetails}}"}""",
                Encoding.UTF8,
                "application/json")
        });
        using var transport = CreateTransport(handler);

        var exception = await Assert.ThrowsAsync<AccountDataException>(() =>
            transport.DeleteCurrentAccountAsync());

        Assert.Equal(AccountDataFailureKind.RecentAuthenticationRequired, exception.FailureKind);
        Assert.DoesNotContain(sensitiveDetails, exception.Message, StringComparison.Ordinal);
    }

    private static SupabaseAccountDataTransport CreateTransport(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://backend.example.test/") },
        "public-key",
        new FakeAccessTokenProvider());

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ExportJson()
    {
        var calendarId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        return $$"""
        {
          "format": "moicalendar-cloud-export",
          "schemaVersion": 1,
          "exportedAtUtc": "2026-09-10T08:00:00Z",
          "preferences": { "displayName": "测试", "timeZoneId": "Asia/Hong_Kong" },
          "calendars": [{
            "id": "{{calendarId}}", "name": "日历", "color": "#425A48", "isDefault": true,
            "createdAtUtc": "2026-09-09T08:00:00Z", "updatedAtUtc": "2026-09-10T08:00:00Z", "deletedAtUtc": null
          }],
          "calendarEvents": [{
            "calendarId": "{{calendarId}}",
            "calendarEvent": {
              "id": "{{eventId}}", "title": "日程", "description": "", "location": "",
              "startUtc": "2026-09-10T08:00:00Z", "endUtc": "2026-09-10T09:00:00Z",
              "timeZoneId": "Asia/Hong_Kong", "isAllDay": false,
              "recurrenceRule": null, "externalUid": null,
              "createdAtUtc": "2026-09-09T08:00:00Z", "updatedAtUtc": "2026-09-10T08:00:00Z", "deletedAtUtc": null
            }
          }]
        }
        """;
    }

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
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.AbsoluteUri;
            ApiKey = request.Headers.GetValues("apikey").Single();
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
