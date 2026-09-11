namespace MoiCalendar.Core;

public enum ThemeColorId
{
    Cupertino,
    Ocean,
    Sage,
    Lavender,
    Sunset,
    Rose,
    Graphite
}

public enum AppearanceMode
{
    System,
    Light,
    Dark
}

public enum TypographyTheme
{
    System,
    Gallery,
    Editorial,
    Studio
}

public sealed record AppearancePreference(
    ThemeColorId ThemeColor,
    AppearanceMode Mode,
    TypographyTheme Typography = TypographyTheme.System)
{
    public static AppearancePreference Default { get; } =
        new(ThemeColorId.Sage, AppearanceMode.System, TypographyTheme.System);
}

public sealed record TypographyDefinition(
    TypographyTheme Id,
    string CssId,
    string DisplayName,
    string Description);

public static class TypographyCatalog
{
    public static IReadOnlyList<TypographyDefinition> All { get; } =
    [
        new(TypographyTheme.System, "system", "System", "原生、快速、熟悉"),
        new(TypographyTheme.Gallery, "gallery", "Gallery", "优雅、安静、画廊感"),
        new(TypographyTheme.Editorial, "editorial", "Editorial", "温润、当代、编辑感"),
        new(TypographyTheme.Studio, "studio", "Studio", "现代、精确、工作室感")
    ];

    public static TypographyDefinition Get(TypographyTheme id) =>
        All.First(typography => typography.Id == id);
}

public sealed record ThemeSemanticPalette(
    string Primary,
    string PrimaryHover,
    string PrimaryPressed,
    string PrimarySoft,
    string PrimaryText,
    string Secondary,
    string Tertiary,
    string Focus,
    string Selection,
    string SelectionText,
    string Today);

public sealed record ThemeColorDefinition(
    ThemeColorId Id,
    string CssId,
    string DisplayName,
    string Description,
    ThemeSemanticPalette Light,
    ThemeSemanticPalette Dark,
    IReadOnlyList<string> CalendarColors);

public static class ThemeColorCatalog
{
    public static IReadOnlyList<ThemeColorDefinition> All { get; } =
    [
        Create(
            ThemeColorId.Cupertino, "cupertino", "Cupertino", "清爽、熟悉、平静",
            ["#0A84FF", "#0077E6", "#0066C2", "#E7F2FF", "#001A2F", "#64D2FF", "#5E5CE6", "#0A84FF", "#0A84FF", "#001A2F", "#0A84FF"],
            ["#409CFF", "#66AEFF", "#2388E8", "#1D3047", "#06192A", "#64D2FF", "#7D7AFF", "#64D2FF", "#409CFF", "#06192A", "#409CFF"],
            ["#0A84FF", "#30D158", "#FF9F0A", "#FF453A", "#BF5AF2", "#FF375F", "#5E5CE6", "#32ADE6"]),
        Create(
            ThemeColorId.Ocean, "ocean", "Ocean", "冷静、现代、专注",
            ["#2563EB", "#1D4ED8", "#1E40AF", "#E8EFFF", "#FFFFFF", "#0891B2", "#14B8A6", "#2563EB", "#1D4ED8", "#FFFFFF", "#2563EB"],
            ["#60A5FA", "#7BB5FB", "#3B82F6", "#20314D", "#07182E", "#22D3EE", "#2DD4BF", "#67E8F9", "#60A5FA", "#07182E", "#60A5FA"],
            ["#2563EB", "#06B6D4", "#14B8A6", "#22C55E", "#84CC16", "#F59E0B", "#8B5CF6", "#EC4899"]),
        Create(
            ThemeColorId.Sage, "sage", "Sage", "自然、安静、耐看",
            ["#397467", "#2F6257", "#285249", "#E8F1EE", "#FFFFFF", "#6B9A8B", "#8D6A9F", "#397467", "#397467", "#FFFFFF", "#397467"],
            ["#70B7A5", "#86C3B4", "#559A89", "#223A35", "#071A15", "#8AC3B4", "#B393C1", "#91D0BF", "#70B7A5", "#071A15", "#70B7A5"],
            ["#397467", "#6B9A8B", "#91B8A5", "#D6A84B", "#C87941", "#B95D5D", "#6B7FA3", "#8D6A9F"]),
        Create(
            ThemeColorId.Lavender, "lavender", "Lavender", "柔和、优雅、现代",
            ["#7C3AED", "#6D28D9", "#5B21B6", "#F1EAFE", "#FFFFFF", "#A855F7", "#3B82F6", "#8B5CF6", "#7C3AED", "#FFFFFF", "#7C3AED"],
            ["#A78BFA", "#B9A2FB", "#8B5CF6", "#352A4D", "#171027", "#C084FC", "#67A3FA", "#C4B5FD", "#A78BFA", "#171027", "#A78BFA"],
            ["#7C3AED", "#A855F7", "#C084FC", "#EC4899", "#F472B6", "#3B82F6", "#06B6D4", "#F59E0B"]),
        Create(
            ThemeColorId.Sunset, "sunset", "Sunset", "温暖、有表现力但不过度饱和",
            ["#E76F51", "#D45F43", "#B94D34", "#FCECE7", "#2D170F", "#F4A261", "#7C5CFC", "#C95D42", "#E76F51", "#2D170F", "#E76F51"],
            ["#F08A70", "#F39B84", "#DA6C52", "#442B25", "#26110B", "#F4B47C", "#9B87FF", "#FFB09B", "#F08A70", "#26110B", "#F08A70"],
            ["#E76F51", "#F4A261", "#E9C46A", "#2A9D8F", "#457B9D", "#7C5CFC", "#D65DB1", "#4F772D"]),
        Create(
            ThemeColorId.Rose, "rose", "Rose", "温暖、柔和、富有个人感",
            ["#E11D48", "#BE123C", "#9F1239", "#FDE8EE", "#FFFFFF", "#FB7185", "#8B5CF6", "#E11D48", "#BE123C", "#FFFFFF", "#E11D48"],
            ["#FB7185", "#FC8A9A", "#E94D69", "#49252D", "#2A0E15", "#FDA4AF", "#A78BFA", "#FDA4AF", "#FB7185", "#2A0E15", "#FB7185"],
            ["#E11D48", "#FB7185", "#F472B6", "#C084FC", "#8B5CF6", "#0EA5E9", "#14B8A6", "#F59E0B"]),
        Create(
            ThemeColorId.Graphite, "graphite", "Graphite", "专业、中性、专注效率",
            ["#4F6B8A", "#425B77", "#354A61", "#E9EEF3", "#FFFFFF", "#64748B", "#0EA5E9", "#4F6B8A", "#425B77", "#FFFFFF", "#4F6B8A"],
            ["#849AB4", "#98A9BD", "#6B829E", "#2B343E", "#10171F", "#94A3B8", "#38BDF8", "#A6BCD6", "#849AB4", "#10171F", "#849AB4"],
            ["#4F6B8A", "#64748B", "#0EA5E9", "#14B8A6", "#22C55E", "#D97706", "#A855F7", "#DC5A5A"])
    ];

    public static ThemeColorDefinition Get(ThemeColorId id) =>
        All.First(theme => theme.Id == id);

    private static ThemeColorDefinition Create(
        ThemeColorId id,
        string cssId,
        string displayName,
        string description,
        IReadOnlyList<string> lightValues,
        IReadOnlyList<string> darkValues,
        IReadOnlyList<string> calendarColors) =>
        new(
            id,
            cssId,
            displayName,
            description,
            CreatePalette(lightValues),
            CreatePalette(darkValues),
            calendarColors);

    private static ThemeSemanticPalette CreatePalette(IReadOnlyList<string> values) =>
        new(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5],
            values[6],
            values[7],
            values[8],
            values[9],
            values[10]);
}

public interface IAppearancePreferenceStore
{
    Task<AppearancePreference?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        AppearancePreference preference,
        CancellationToken cancellationToken = default);
}
