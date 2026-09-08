using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Supabase;

internal interface ISupabaseAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

internal sealed class SupabaseAccountService : IAccountService, ISupabaseAccessTokenProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);
    private readonly HttpClient httpClient;
    private readonly string publicKey;
    private readonly ISupabaseSessionStore sessionStore;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim accountGate = new(1, 1);
    private CloudAccount? currentAccount;
    private SupabaseStoredSession? currentSession;
    private bool disposed;

    public SupabaseAccountService(
        HttpClient httpClient,
        string publicKey,
        ISupabaseSessionStore sessionStore,
        TimeProvider? timeProvider = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.publicKey = string.IsNullOrWhiteSpace(publicKey)
            ? throw new ArgumentException("Supabase 公开客户端密钥不能为空。", nameof(publicKey))
            : publicKey.Trim();
        this.sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAvailable => true;

    public bool IsPasswordRecovery { get; private set; }

    public async Task<CloudAccount?> RestoreSessionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            return await RestoreSessionCoreAsync(cancellationToken);
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task<CloudAccount?> GetCurrentAccountAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            return currentAccount ?? await RestoreSessionCoreAsync(cancellationToken);
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task<CloudAccount?> RefreshSessionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            currentSession ??= await sessionStore.ReadAsync(cancellationToken);
            if (currentSession is null)
            {
                currentAccount = null;
                return null;
            }

            try
            {
                var response = await RefreshSessionCoreAsync(cancellationToken);
                currentAccount = response.User is null
                    ? await GetUserAsync(currentSession!.AccessToken, cancellationToken)
                    : ToCloudAccount(response.User);
                return currentAccount;
            }
            catch (SupabaseAuthRequestException exception)
                when (exception.StatusCode is HttpStatusCode.BadRequest or
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await ClearSessionCoreAsync(cancellationToken);
                return null;
            }
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task<AccountRegistrationResult> RegisterAsync(
        string emailAddress,
        string password,
        string emailRedirectUrl,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var email = ValidateEmail(emailAddress);
        ValidatePassword(password);
        var redirectUri = ValidateRedirectUrl(emailRedirectUrl);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            var response = await SendAsync<SupabaseAuthPayload>(
                HttpMethod.Post,
                WithRedirect("signup", redirectUri),
                new EmailPasswordRequest(email, password),
                cancellationToken: cancellationToken);
            var user = response.User ?? response.ToTopLevelUser();
            var account = ToCloudAccount(user ?? throw InvalidResponse());
            var hasSession = response.HasSession;

            if (hasSession)
            {
                await SaveSessionAsync(response, cancellationToken);
                currentAccount = account;
            }
            else
            {
                currentAccount = null;
                currentSession = null;
                await sessionStore.ClearAsync(cancellationToken);
            }

            IsPasswordRecovery = false;
            return new AccountRegistrationResult(
                account,
                hasSession,
                EmailVerificationRequired: !hasSession || !account.IsEmailVerified);
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task<CloudAccount> LoginAsync(
        string emailAddress,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var email = ValidateEmail(emailAddress);
        ValidatePassword(password);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            var response = await SendAsync<SupabaseAuthPayload>(
                HttpMethod.Post,
                "token?grant_type=password",
                new EmailPasswordRequest(email, password),
                cancellationToken: cancellationToken);
            if (!response.HasSession)
            {
                throw InvalidResponse();
            }

            await SaveSessionAsync(response, cancellationToken);
            currentAccount = response.User is null
                ? await GetUserAsync(currentSession!.AccessToken, cancellationToken)
                : ToCloudAccount(response.User);
            IsPasswordRecovery = false;
            return currentAccount;
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            var session = currentSession ?? await sessionStore.ReadAsync(cancellationToken);
            currentSession = null;
            currentAccount = null;
            IsPasswordRecovery = false;
            await sessionStore.ClearAsync(cancellationToken);

            if (session is null)
            {
                return;
            }

            try
            {
                await SendWithoutResponseAsync(
                    HttpMethod.Post,
                    "logout?scope=local",
                    body: null,
                    session.AccessToken,
                    cancellationToken);
            }
            catch (SupabaseAuthRequestException exception)
                when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The local session is already removed; an expired remote session needs no retry.
            }
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task RequestPasswordResetAsync(
        string emailAddress,
        string redirectUrl,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var email = ValidateEmail(emailAddress);
        var redirectUri = ValidateRedirectUrl(redirectUrl);
        await SendWithoutResponseAsync(
            HttpMethod.Post,
            WithRedirect("recover", redirectUri),
            new PasswordRecoveryRequest(email),
            bearerToken: null,
            cancellationToken);
    }

    public async Task<CloudAccount> CompletePasswordResetAsync(
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidatePassword(newPassword);

        await accountGate.WaitAsync(cancellationToken);
        try
        {
            _ = currentAccount ?? await RestoreSessionCoreAsync(cancellationToken);
            if (!IsPasswordRecovery || currentSession is null)
            {
                throw new AccountServiceException("当前没有可用的密码恢复会话，请重新发送重置邮件。");
            }

            var user = await SendAsync<SupabaseUser>(
                HttpMethod.Put,
                "user",
                new PasswordUpdateRequest(newPassword),
                currentSession.AccessToken,
                cancellationToken);
            currentAccount = ToCloudAccount(user);
            IsPasswordRecovery = false;
            return currentAccount;
        }
        finally
        {
            accountGate.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await accountGate.WaitAsync(cancellationToken);
        try
        {
            _ = currentAccount ?? await RestoreSessionCoreAsync(cancellationToken);
            if (currentSession is null)
            {
                throw new AccountServiceException("请先登录云账户。");
            }

            if (currentSession.ExpiresAtUtc <= timeProvider.GetUtcNow() + RefreshMargin)
            {
                await RefreshSessionCoreAsync(cancellationToken);
            }

            return currentSession.AccessToken;
        }
        finally
        {
            accountGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        accountGate.Dispose();
        httpClient.Dispose();
    }

    private async Task<CloudAccount?> RestoreSessionCoreAsync(CancellationToken cancellationToken)
    {
        var callback = await sessionStore.ReadCallbackAsync(cancellationToken);
        if (callback is not null)
        {
            if (!string.IsNullOrWhiteSpace(callback.ErrorCode))
            {
                await ClearSessionCoreAsync(cancellationToken);
                throw new AccountServiceException(MapCallbackError(callback.ErrorCode));
            }

            if (!string.IsNullOrWhiteSpace(callback.AccessToken) &&
                !string.IsNullOrWhiteSpace(callback.RefreshToken))
            {
                currentSession = new SupabaseStoredSession(
                    callback.AccessToken,
                    callback.RefreshToken,
                    GetExpiry(callback.ExpiresIn));
                await sessionStore.WriteAsync(currentSession, cancellationToken);
                IsPasswordRecovery = string.Equals(
                    callback.Type,
                    "recovery",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        currentSession ??= await sessionStore.ReadAsync(cancellationToken);
        if (currentSession is null)
        {
            currentAccount = null;
            IsPasswordRecovery = false;
            return null;
        }

        try
        {
            if (currentSession.ExpiresAtUtc <= timeProvider.GetUtcNow() + RefreshMargin)
            {
                await RefreshSessionCoreAsync(cancellationToken);
            }

            currentAccount = await GetUserAsync(currentSession.AccessToken, cancellationToken);
            return currentAccount;
        }
        catch (SupabaseAuthRequestException exception)
            when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            try
            {
                await RefreshSessionCoreAsync(cancellationToken);
                currentAccount = await GetUserAsync(currentSession.AccessToken, cancellationToken);
                return currentAccount;
            }
            catch (AccountServiceException)
            {
                await ClearSessionCoreAsync(cancellationToken);
                return null;
            }
        }
        catch (SupabaseAuthRequestException exception)
            when (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            await ClearSessionCoreAsync(cancellationToken);
            return null;
        }
        catch (AccountServiceException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new AccountServiceException("无法恢复云账户会话，请检查网络连接后重试。", exception);
        }
    }

    private async Task<SupabaseAuthPayload> RefreshSessionCoreAsync(CancellationToken cancellationToken)
    {
        if (currentSession is null)
        {
            throw new AccountServiceException("没有可刷新的云账户会话。");
        }

        var response = await SendAsync<SupabaseAuthPayload>(
            HttpMethod.Post,
            "token?grant_type=refresh_token",
            new RefreshTokenRequest(currentSession.RefreshToken),
            cancellationToken: cancellationToken);
        if (!response.HasSession)
        {
            throw InvalidResponse();
        }

        await SaveSessionAsync(response, cancellationToken);
        return response;
    }

    private async Task<CloudAccount> GetUserAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var user = await SendAsync<SupabaseUser>(
            HttpMethod.Get,
            "user",
            body: null,
            accessToken,
            cancellationToken);
        return ToCloudAccount(user);
    }

    private async Task SaveSessionAsync(
        SupabaseAuthPayload response,
        CancellationToken cancellationToken)
    {
        currentSession = new SupabaseStoredSession(
            response.AccessToken!,
            response.RefreshToken!,
            GetExpiry(response.ExpiresIn));
        await sessionStore.WriteAsync(currentSession, cancellationToken);
    }

    private DateTimeOffset GetExpiry(int? expiresIn) =>
        timeProvider.GetUtcNow().AddSeconds(expiresIn is > 0 ? expiresIn.Value : 3600);

    private async Task ClearSessionCoreAsync(CancellationToken cancellationToken)
    {
        currentSession = null;
        currentAccount = null;
        IsPasswordRecovery = false;
        await sessionStore.ClearAsync(cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendCoreAsync(
            method,
            relativeUrl,
            body,
            bearerToken,
            cancellationToken);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new AccountServiceException("云账户服务返回了无法识别的数据。", exception);
        }
    }

    private async Task SendWithoutResponseAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(
            method,
            relativeUrl,
            body,
            bearerToken,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.TryAddWithoutValidation("apikey", publicKey);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountServiceException("云账户请求超时，请检查网络连接后重试。");
        }
        catch (HttpRequestException requestException)
        {
            throw new AccountServiceException("无法连接云账户服务；本地日历仍可继续使用。", requestException);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var errorCode = await ReadErrorCodeAsync(response, cancellationToken);
        var exception = new SupabaseAuthRequestException(
            response.StatusCode,
            errorCode,
            MapError(response.StatusCode, errorCode));
        response.Dispose();
        throw exception;
    }

    private static async Task<string?> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            foreach (var name in new[] { "error_code", "code", "error" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Error payloads are deliberately not surfaced because they may contain sensitive data.
        }

        return null;
    }

    private static CloudAccount ToCloudAccount(SupabaseUser user)
    {
        if (string.IsNullOrWhiteSpace(user.Id))
        {
            throw InvalidResponse();
        }

        return new CloudAccount(
            user.Id,
            GetDisplayName(user.UserMetadata),
            user.Email,
            user.EmailConfirmedAt is not null || user.ConfirmedAt is not null);
    }

    private static string? GetDisplayName(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (var name in new[] { "display_name", "full_name", "name" })
        {
            if (value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.GetString()))
            {
                return property.GetString()!.Trim();
            }
        }

        return null;
    }

    private static string ValidateEmail(string value)
    {
        var email = value?.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(email) ||
                email.Length > 320 ||
                new MailAddress(email).Address != email)
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new AccountServiceException("请输入有效的电子邮箱地址。");
        }

        return email;
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            throw new AccountServiceException("密码至少需要 6 个字符。");
        }
        if (password.Length > 1024)
        {
            throw new AccountServiceException("密码长度超过允许范围。");
        }
    }

    private static Uri ValidateRedirectUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new AccountServiceException("账户回调地址必须是绝对 HTTP 或 HTTPS URL。");
        }

        return uri;
    }

    private static string WithRedirect(string endpoint, Uri redirectUri) =>
        $"{endpoint}?redirect_to={Uri.EscapeDataString(redirectUri.AbsoluteUri)}";

    private static string MapError(HttpStatusCode statusCode, string? errorCode) =>
        errorCode?.ToLowerInvariant() switch
        {
            "invalid_credentials" => "邮箱或密码不正确。",
            "email_not_confirmed" => "邮箱尚未验证，请先查看验证邮件。",
            "user_already_exists" or "user_already_registered" => "该邮箱已注册，请直接登录。",
            "weak_password" => "密码不符合云账户的安全要求。",
            "over_email_send_rate_limit" or "over_request_rate_limit" => "请求过于频繁，请稍后再试。",
            "same_password" => "新密码不能与当前密码相同。",
            "session_not_found" or "refresh_token_not_found" => "登录会话已失效，请重新登录。",
            _ when statusCode == HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后再试。",
            _ when statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "云账户认证失败，请重新登录。",
            _ => $"云账户请求失败（HTTP {(int)statusCode}）。"
        };

    private static string MapCallbackError(string errorCode) =>
        errorCode.ToLowerInvariant() switch
        {
            "access_denied" => "验证或密码恢复链接已失效，请重新发起请求。",
            _ => "无法完成邮箱验证或密码恢复，请重新发起请求。"
        };

    private static AccountServiceException InvalidResponse() =>
        new("云账户服务返回的数据不完整。");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record EmailPasswordRequest(string Email, string Password);

    private sealed record PasswordRecoveryRequest(string Email);

    private sealed record PasswordUpdateRequest(string Password);

    private sealed record RefreshTokenRequest(
        [property: JsonPropertyName("refresh_token")] string RefreshToken);

    internal sealed record SupabaseAuthPayload
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }

        public SupabaseUser? User { get; init; }

        public string? Id { get; init; }

        public string? Email { get; init; }

        [JsonPropertyName("email_confirmed_at")]
        public DateTimeOffset? EmailConfirmedAt { get; init; }

        [JsonPropertyName("confirmed_at")]
        public DateTimeOffset? ConfirmedAt { get; init; }

        [JsonPropertyName("user_metadata")]
        public JsonElement? UserMetadata { get; init; }

        [JsonIgnore]
        public bool HasSession =>
            !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(RefreshToken);

        public SupabaseUser? ToTopLevelUser() => string.IsNullOrWhiteSpace(Id)
            ? null
            : new SupabaseUser
            {
                Id = Id,
                Email = Email,
                EmailConfirmedAt = EmailConfirmedAt,
                ConfirmedAt = ConfirmedAt,
                UserMetadata = UserMetadata
            };

        public override string ToString() =>
            $"SupabaseAuthPayload {{ AccessToken = ***, RefreshToken = ***, ExpiresIn = {ExpiresIn}, UserId = {User?.Id ?? Id} }}";
    }

    internal sealed record SupabaseUser
    {
        public string? Id { get; init; }

        public string? Email { get; init; }

        [JsonPropertyName("email_confirmed_at")]
        public DateTimeOffset? EmailConfirmedAt { get; init; }

        [JsonPropertyName("confirmed_at")]
        public DateTimeOffset? ConfirmedAt { get; init; }

        [JsonPropertyName("user_metadata")]
        public JsonElement? UserMetadata { get; init; }
    }
}

internal sealed class SupabaseAuthRequestException(
    HttpStatusCode statusCode,
    string? errorCode,
    string message) : AccountServiceException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;
}
