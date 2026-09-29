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
        var modeText = parts.Length >= 3 ? parts[1] : parts[0];
        var mode = Enum.TryParse<AppearanceMode>(modeText, ignoreCase: true, out var parsedMode) &&
                   Enum.IsDefined(parsedMode)
            ? parsedMode
            : AppearancePreference.Default.Mode;

        return new AppearancePreference(mode);
    }

    public Task SaveAsync(
        AppearancePreference preference,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<object?>(
            "保存外观偏好",
            "saveAppearancePreference",
            cancellationToken,
            preference.Mode.ToString());

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
