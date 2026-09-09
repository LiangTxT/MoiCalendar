using Microsoft.Extensions.Configuration;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.App.Configuration;

public sealed record MoiCalendarConfiguration(
    Uri PublicBaseUrl,
    MicrosoftAuthenticationConfiguration MicrosoftAuthentication,
    SynchronizationConfiguration Synchronization,
    CloudBackendOptions CloudBackend,
    DiagnosticsOptions Diagnostics)
{
    public const string CloudAccountRedirectPath = "settings";
    public const string DefaultMicrosoftLoginCallbackPath = "authentication/login-callback";

    public Uri CloudAccountRedirectUrl =>
        new(PublicBaseUrl, CloudAccountRedirectPath);

    public Uri MicrosoftLoginCallbackUrl =>
        new(
            PublicBaseUrl,
            MicrosoftAuthentication.RedirectPath ?? DefaultMicrosoftLoginCallbackPath);

    public static MoiCalendarConfiguration Load(
        IConfiguration configuration,
        Uri fallbackBaseUrl)
    {
        var configuredBaseUrl = OptionalValue(configuration["MoiCalendar:PublicBaseUrl"]);
        var publicBaseUrl = configuredBaseUrl is null
            ? fallbackBaseUrl
            : ParsePublicBaseUrl(configuredBaseUrl);

        return new MoiCalendarConfiguration(
            EnsureTrailingSlash(publicBaseUrl),
            new MicrosoftAuthenticationConfiguration(
                OptionalValue(configuration["MoiCalendar:MicrosoftAuthentication:Authority"]),
                OptionalValue(configuration["MoiCalendar:MicrosoftAuthentication:ClientId"]),
                ParseRedirectPath(configuration["MoiCalendar:MicrosoftAuthentication:RedirectPath"])),
            new SynchronizationConfiguration(
                OptionalValue(configuration["MoiCalendar:Synchronization:Provider"])),
            new CloudBackendOptions
            {
                Enabled = ReadBoolean(configuration, "MoiCalendar:CloudBackend:Enabled"),
                BaseUrl = OptionalValue(configuration["MoiCalendar:CloudBackend:BaseUrl"]),
                PublicKey = OptionalValue(configuration["MoiCalendar:CloudBackend:PublicKey"])
            },
            CreateDiagnosticsOptions(configuration));
    }

    private static Uri ParsePublicBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "MoiCalendar:PublicBaseUrl 必须是绝对 HTTP 或 HTTPS URL。");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "MoiCalendar:PublicBaseUrl 不能包含查询参数或片段。");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "MoiCalendar:PublicBaseUrl 不能包含用户名或密码。");
        }

        return uri;
    }

    private static string? ParseRedirectPath(string? configuredValue)
    {
        var value = OptionalValue(configuredValue);
        if (value is null)
        {
            return null;
        }

        if (value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains('\\') ||
            value.Contains("?", StringComparison.Ordinal) ||
            value.Contains("#", StringComparison.Ordinal) ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidOperationException(
                "MoiCalendar:MicrosoftAuthentication:RedirectPath 必须是安全的站点相对路径。");
        }

        return value;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri($"{value}/", UriKind.Absolute);
    }

    private static string? OptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DiagnosticsOptions CreateDiagnosticsOptions(IConfiguration configuration)
    {
        var retentionValue = OptionalValue(configuration["MoiCalendar:Diagnostics:RetentionLimit"]);
        var options = new DiagnosticsOptions
        {
            Enabled = ReadBoolean(configuration, "MoiCalendar:Diagnostics:Enabled", true),
            RetentionLimit = retentionValue is null
                ? 200
                : int.TryParse(retentionValue, out var retentionLimit)
                    ? retentionLimit
                    : throw new InvalidOperationException(
                        "MoiCalendar:Diagnostics:RetentionLimit 必须是整数。")
        };
        options.Validate();
        return options;
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool defaultValue = false)
    {
        var value = OptionalValue(configuration[key]);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"{key} 必须是 true 或 false。");
    }
}

public sealed record MicrosoftAuthenticationConfiguration(
    string? Authority,
    string? ClientId,
    string? RedirectPath);

public sealed record SynchronizationConfiguration(string? Provider);
