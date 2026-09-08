using System.Net;
using System.Text;
using System.Text.Json;
using MoiCalendar.Sync.Supabase;

namespace MoiCalendar.Tests;

public sealed class SupabaseAccountServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Login_PersistsSessionWithoutPersistingPassword()
    {
        var store = new FakeSessionStore();
        var handler = new QueueHttpMessageHandler(JsonResponse(
            """
            {
              "access_token": "access-1",
              "refresh_token": "refresh-1",
              "expires_in": 3600,
              "user": {
                "id": "11111111-1111-4111-8111-111111111111",
                "email": "user@example.com",
                "email_confirmed_at": "2026-09-04T07:00:00Z",
                "user_metadata": { "display_name": "测试用户" }
              }
            }
            """));
        using var service = CreateService(handler, store);

        var account = await service.LoginAsync("user@example.com", "password-123");
        var accessToken = await ((ISupabaseAccessTokenProvider)service).GetAccessTokenAsync();

        Assert.Equal("测试用户", account.DisplayName);
        Assert.True(account.IsEmailVerified);
        Assert.Equal("access-1", store.Session?.AccessToken);
        Assert.Equal("access-1", accessToken);
        Assert.Equal("refresh-1", store.Session?.RefreshToken);
        Assert.DoesNotContain("password-123", store.Session?.ToString() ?? string.Empty, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://example.supabase.co/auth/v1/token?grant_type=password", request.Uri);
        Assert.Equal("public-test-key", request.ApiKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("user@example.com", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("password-123", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task RestoreSession_RefreshesExpiredTokenAndRetrievesCurrentUser()
    {
        var store = new FakeSessionStore
        {
            Session = new SupabaseStoredSession("expired-access", "refresh-1", Now.AddMinutes(-1))
        };
        var handler = new QueueHttpMessageHandler(
            JsonResponse(
                """
                {
                  "access_token": "access-2",
                  "refresh_token": "refresh-2",
                  "expires_in": 7200
                }
                """),
            JsonResponse(
                """
                {
                  "id": "11111111-1111-4111-8111-111111111111",
                  "email": "user@example.com",
                  "email_confirmed_at": null
                }
                """));
        using var service = CreateService(handler, store);

        var account = await service.RestoreSessionAsync();

        Assert.NotNull(account);
        Assert.False(account.IsEmailVerified);
        Assert.Equal("access-2", store.Session?.AccessToken);
        Assert.Equal("refresh-2", store.Session?.RefreshToken);
        Assert.Equal(
            [
                "https://example.supabase.co/auth/v1/token?grant_type=refresh_token",
                "https://example.supabase.co/auth/v1/user"
            ],
            handler.Requests.Select(request => request.Uri));
    }

    [Fact]
    public async Task RestoreSession_ClearsSessionWhenRefreshTokenIsRejected()
    {
        var store = new FakeSessionStore
        {
            Session = new SupabaseStoredSession("expired-access", "rejected-refresh", Now.AddMinutes(-1))
        };
        var handler = new QueueHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"code\":\"refresh_token_not_found\"}",
                Encoding.UTF8,
                "application/json")
        });
        using var service = CreateService(handler, store);

        var account = await service.RestoreSessionAsync();

        Assert.Null(account);
        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task RefreshSession_ForcesTokenRotationAndReturnsAccount()
    {
        var store = new FakeSessionStore
        {
            Session = new SupabaseStoredSession("access-1", "refresh-1", Now.AddHours(1))
        };
        var handler = new QueueHttpMessageHandler(JsonResponse(
            """
            {
              "access_token": "access-2",
              "refresh_token": "refresh-2",
              "expires_in": 3600,
              "user": {
                "id": "11111111-1111-4111-8111-111111111111",
                "email": "user@example.com",
                "email_confirmed_at": "2026-09-04T07:00:00Z"
              }
            }
            """));
        using var service = CreateService(handler, store);

        var account = await service.RefreshSessionAsync();

        Assert.NotNull(account);
        Assert.Equal("access-2", store.Session?.AccessToken);
        Assert.Equal("refresh-2", store.Session?.RefreshToken);
        Assert.Single(handler.Requests);
        Assert.EndsWith("token?grant_type=refresh_token", handler.Requests[0].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_WhenEmailVerificationIsRequired_DoesNotCreateSession()
    {
        var store = new FakeSessionStore();
        var handler = new QueueHttpMessageHandler(JsonResponse(
            """
            {
              "id": "11111111-1111-4111-8111-111111111111",
              "email": "user@example.com",
              "email_confirmed_at": null
            }
            """));
        using var service = CreateService(handler, store);

        var result = await service.RegisterAsync(
            "user@example.com",
            "password-123",
            "https://calendar.example.com/settings");

        Assert.False(result.IsSignedIn);
        Assert.True(result.EmailVerificationRequired);
        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCount);
        Assert.Contains(
            "redirect_to=https%3A%2F%2Fcalendar.example.com%2Fsettings",
            handler.Requests.Single().Uri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryCallback_RestoresSessionAndAllowsPasswordUpdate()
    {
        var store = new FakeSessionStore
        {
            Callback = new SupabaseAuthCallback(
                "recovery-access",
                "recovery-refresh",
                "bearer",
                3600,
                "recovery",
                null,
                null)
        };
        var handler = new QueueHttpMessageHandler(
            JsonResponse(UserJson(emailConfirmed: true)),
            JsonResponse(UserJson(emailConfirmed: true)));
        using var service = CreateService(handler, store);

        _ = await service.RestoreSessionAsync();
        Assert.True(service.IsPasswordRecovery);

        var account = await service.CompletePasswordResetAsync("new-password-123");

        Assert.True(account.IsEmailVerified);
        Assert.False(service.IsPasswordRecovery);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("new-password-123", body.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Logout_AlwaysClearsBrowserSession()
    {
        var store = new FakeSessionStore
        {
            Session = new SupabaseStoredSession("access-1", "refresh-1", Now.AddHours(1))
        };
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error_code\":\"session_not_found\"}", Encoding.UTF8, "application/json")
            });
        using var service = CreateService(handler, store);

        await service.LogoutAsync();

        Assert.Null(store.Session);
        Assert.Equal(1, store.ClearCount);
    }

    [Fact]
    public async Task RequestPasswordReset_DoesNotCreateOrChangeSession()
    {
        var original = new SupabaseStoredSession("access-1", "refresh-1", Now.AddHours(1));
        var store = new FakeSessionStore { Session = original };
        var handler = new QueueHttpMessageHandler(JsonResponse("{}"));
        using var service = CreateService(handler, store);

        await service.RequestPasswordResetAsync(
            "user@example.com",
            "https://calendar.example.com/settings");

        Assert.Same(original, store.Session);
        Assert.Equal(0, store.ClearCount);
        Assert.Contains("/auth/v1/recover?redirect_to=", handler.Requests.Single().Uri, StringComparison.Ordinal);
    }

    private static SupabaseAccountService CreateService(
        QueueHttpMessageHandler handler,
        ISupabaseSessionStore store) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.supabase.co/auth/v1/") },
            "public-test-key",
            store,
            new FixedTimeProvider(Now));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string UserJson(bool emailConfirmed) => $$"""
        {
          "id": "11111111-1111-4111-8111-111111111111",
          "email": "user@example.com",
          "email_confirmed_at": {{(emailConfirmed ? "\"2026-09-04T07:00:00Z\"" : "null")}}
        }
        """;

    private sealed class FakeSessionStore : ISupabaseSessionStore
    {
        public SupabaseStoredSession? Session { get; set; }

        public SupabaseAuthCallback? Callback { get; set; }

        public int ClearCount { get; private set; }

        public Task<SupabaseStoredSession?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Session);

        public Task WriteAsync(
            SupabaseStoredSession session,
            CancellationToken cancellationToken = default)
        {
            Session = session;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Session = null;
            ClearCount++;
            return Task.CompletedTask;
        }

        public Task<SupabaseAuthCallback?> ReadCallbackAsync(
            CancellationToken cancellationToken = default)
        {
            var result = Callback;
            Callback = null;
            return Task.FromResult(result);
        }
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string? ApiKey,
        string? Body);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
