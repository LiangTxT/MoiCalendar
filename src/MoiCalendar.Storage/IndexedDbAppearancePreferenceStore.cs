using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbAppearancePreferenceStore(IndexedDbConnection connection)
    : IAppearancePreferenceStore
{
    public async Task<AppearancePreference?> GetAsync(CancellationToken cancellationToken = default)
    {
        var value = await InvokeAsync<string?>(
            "读取外观偏好",
            "getAppearancePreference",
            cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('|', StringSplitOptions.TrimEntries);
        var themeColor = parts.Length > 0 &&
                         Enum.TryParse<ThemeColorId>(parts[0], ignoreCase: true, out var parsedThemeColor) &&
                         Enum.IsDefined(parsedThemeColor)
            ? parsedThemeColor
            : AppearancePreference.Default.ThemeColor;
        var mode = parts.Length > 1 &&
                   Enum.TryParse<AppearanceMode>(parts[1], ignoreCase: true, out var parsedMode) &&
                   Enum.IsDefined(parsedMode)
            ? parsedMode
            : AppearancePreference.Default.Mode;
        var typography = parts.Length > 2 &&
                         Enum.TryParse<TypographyTheme>(parts[2], ignoreCase: true, out var parsedTypography) &&
                         Enum.IsDefined(parsedTypography)
            ? parsedTypography
            : AppearancePreference.Default.Typography;

        return new AppearancePreference(themeColor, mode, typography);
    }

    public Task SaveAsync(
        AppearancePreference preference,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            "保存外观偏好",
            "saveAppearancePreference",
            cancellationToken,
            preference.ThemeColor.ToString(),
            preference.Mode.ToString(),
            preference.Typography.ToString());

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            return await connection.InvokeAsync<T>(identifier, cancellationToken, arguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new EventRepositoryException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}
