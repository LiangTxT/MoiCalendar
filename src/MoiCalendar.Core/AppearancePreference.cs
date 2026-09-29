namespace MoiCalendar.Core;

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public sealed record AppearancePreference(AppearanceMode Mode)
{
    public static AppearancePreference Default { get; } = new(AppearanceMode.System);
}

public interface IAppearancePreferenceStore
{
    Task<AppearancePreference?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppearancePreference preference, CancellationToken cancellationToken = default);
}
