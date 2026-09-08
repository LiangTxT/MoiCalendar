using System.Text;
using System.Text.Json;

namespace MoiCalendar.Sync.Supabase;

internal static class SupabasePublicKeyGuard
{
    public static void Validate(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && IsPrivilegedKey(key.Trim()))
        {
            throw new InvalidOperationException(
                "MoiCalendar:CloudBackend:PublicKey 不能使用 service-role、secret 或管理员密钥。Blazor 客户端只能配置公开客户端密钥。");
        }
    }

    private static bool IsPrivilegedKey(string key)
    {
        if (key.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("service_role", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("supabase_admin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = key.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        try
        {
            var payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(
                Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty("role", out var role) &&
                role.ValueKind == JsonValueKind.String &&
                role.GetString() is { } roleValue &&
                (roleValue.Equals("service_role", StringComparison.OrdinalIgnoreCase) ||
                 roleValue.Equals("supabase_admin", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }
}
