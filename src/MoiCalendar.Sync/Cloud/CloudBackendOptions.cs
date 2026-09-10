namespace MoiCalendar.Sync.Cloud;

public sealed record CloudBackendOptions
{
    public bool Enabled { get; init; }

    public string? BaseUrl { get; init; }

    public string? PublicKey { get; init; }

    public void Validate()
    {
        var baseUrl = OptionalValue(BaseUrl);
        var publicKey = OptionalValue(PublicKey);

        if (Enabled && baseUrl is null)
        {
            throw new InvalidOperationException(
                "启用云后端时必须配置 MoiCalendar:CloudBackend:BaseUrl。");
        }

        if (Enabled && publicKey is null)
        {
            throw new InvalidOperationException(
                "启用云后端时必须配置 MoiCalendar:CloudBackend:PublicKey。仅可使用公开客户端密钥。");
        }

        if (baseUrl is not null)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException(
                    "MoiCalendar:CloudBackend:BaseUrl 必须是没有内嵌凭据、查询参数或片段的绝对 HTTP 或 HTTPS URL。");
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                throw new InvalidOperationException(
                    "非本机云后端必须使用 HTTPS；HTTP 仅允许本地回环开发地址。");
            }
        }
    }

    public override string ToString() =>
        $"CloudBackendOptions {{ Enabled = {Enabled}, BaseUrl = {BaseUrl}, PublicKey = *** }}";

    private static string? OptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
