using Microsoft.Extensions.Configuration;

namespace MoiCalendar.App.Configuration;

public sealed record MoiCalendarConfiguration(
    Uri PublicBaseUrl,
    MicrosoftAuthenticationConfiguration MicrosoftAuthentication,
    SynchronizationConfiguration Synchronization)
{
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
                OptionalValue(configuration["MoiCalendar:MicrosoftAuthentication:RedirectPath"])),
            new SynchronizationConfiguration(
                OptionalValue(configuration["MoiCalendar:Synchronization:Provider"])));
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

        return uri;
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
}

public sealed record MicrosoftAuthenticationConfiguration(
    string? Authority,
    string? ClientId,
    string? RedirectPath);

public sealed record SynchronizationConfiguration(string? Provider);
