using System.Text.RegularExpressions;

namespace MoiCalendar.Core;

public static class ApplicationVersion
{
    public static string? Normalize(string? value) => value is { Length: <= 100 } &&
        Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+(?:\.[0-9]+){0,2}(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z", RegexOptions.CultureInvariant)
            ? value : null;
}
