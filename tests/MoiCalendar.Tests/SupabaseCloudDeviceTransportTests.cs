using System.Net;
using System.Text;
using System.Text.Json;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class SupabaseCloudDeviceTransportTests
{
    [Fact]
    public async Task Register_UsesAuthenticatedProviderRpcWithoutSdkTypes()
    {
        var deviceId = Guid.NewGuid();
        var handler = new RecordingHandler(JsonResponse(DeviceJson(deviceId, "MoiCalendar PWA")));
        using var transport = CreateTransport(handler);

        var device = await transport.RegisterAsync(
            new CloudDeviceRegistration(deviceId, "MoiCalendar PWA", "PWA"));

        Assert.Equal(deviceId, device.DeviceId);
        Assert.Equal("Bearer test-access-token", handler.Authorization);
        Assert.Equal("public-key", handler.ApiKey);
        Assert.EndsWith("/rest/v1/rpc/moicalendar_register_device", handler.Uri, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(deviceId, body.RootElement.GetProperty("p_device_id").GetGuid());
    }

    [Fact]
    public async Task List_MapsActiveAndRevokedDeviceMetadata()
    {
        var activeId = Guid.NewGuid();
        var revokedId = Guid.NewGuid();
        var handler = new RecordingHandler(JsonResponse($$"""
            [
              {{DeviceJson(activeId, "当前设备")}},
              {{DeviceJson(revokedId, "旧设备", "2026-09-09T10:00:00Z")}}
            ]
            """));
        using var transport = CreateTransport(handler);

        var devices = await transport.GetDevicesAsync();

        Assert.Equal(2, devices.Count);
        Assert.False(devices.Single(device => device.DeviceId == activeId).IsRevoked);
        Assert.True(devices.Single(device => device.DeviceId == revokedId).IsRevoked);
        Assert.All(devices, device => Assert.Equal("PWA", device.Platform));
    }

    [Fact]
    public async Task RevokedDeviceError_IsStructuredAndNeverExposesProviderBody()
    {
        const string secret = "Bearer should-never-appear";
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    message = "moicalendar_device_revoked",
                    details = secret
                }),
                Encoding.UTF8,
                "application/json")
        });
        using var transport = CreateTransport(handler);

        var exception = await Assert.ThrowsAsync<CloudSyncTransportException>(() =>
            transport.AcknowledgeSuccessfulSyncAsync(Guid.NewGuid(), 17));

        Assert.Equal("device_revoked", exception.ErrorCode);
        Assert.Equal(CloudSyncFailureKind.Permanent, exception.FailureKind);
        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    private static SupabaseCloudDeviceTransport CreateTransport(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://example.supabase.co/rest/v1/") },
        "public-key",
        new FakeAccessTokenProvider());

    private static string DeviceJson(Guid id, string name, string? deletedAt = null) => $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "platform": "PWA",
          "created_at": "2026-09-09T08:00:00Z",
          "last_seen_at": "2026-09-09T09:00:00Z",
          "deleted_at": {{(deletedAt is null ? "null" : $"\"{deletedAt}\"")}}
        }
        """;

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
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
