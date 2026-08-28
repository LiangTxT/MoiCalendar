using System.Text.RegularExpressions;

namespace MoiCalendar.Core;

public static partial class SyncLogSanitizer
{
    private const int MaximumMessageLength = 500;
    private const string Redacted = "[已隐藏]";

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "同步操作失败。";
        }

        var sanitized = AuthorizationSchemeRegex().Replace(message, "$1 " + Redacted);
        sanitized = SensitiveValueRegex().Replace(sanitized, "$1=" + Redacted);
        sanitized = UriUserInfoRegex().Replace(sanitized, "$1" + Redacted + "@");
        return sanitized.Length <= MaximumMessageLength
            ? sanitized
            : sanitized[..MaximumMessageLength];
    }

    public static SyncLogEntry Sanitize(SyncLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry with
        {
            Provider = Sanitize(entry.Provider),
            Message = Sanitize(entry.Message),
            ErrorCode = entry.ErrorCode is null ? null : Sanitize(entry.ErrorCode)
        };
    }

    [GeneratedRegex(@"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9+/_=.-]+")]
    private static partial Regex AuthorizationSchemeRegex();

    [GeneratedRegex("(?i)\\b(access[_-]?token|refresh[_-]?token|password|passwd|authorization|webdav[_-]?credentials?)\\b[\"']?\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;&]+)")]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex(@"(?i)\b(https?://)[^\s/@:]+:[^\s/@]+@")]
    private static partial Regex UriUserInfoRegex();
}
