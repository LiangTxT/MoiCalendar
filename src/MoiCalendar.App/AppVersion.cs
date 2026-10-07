using System.Reflection;

namespace MoiCalendar.App;

public static class AppVersion
{
    public static string Current { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
}
