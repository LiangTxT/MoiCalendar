using System.Text.RegularExpressions;

namespace MoiCalendar.Core;

public sealed record DiagnosticsOptions
{
    public bool Enabled { get; init; } = true;

    public int RetentionLimit { get; init; } = 200;

    public void Validate()
    {
        if (RetentionLimit is < 1 or > 1_000)
        {
            throw new InvalidOperationException("诊断事件保留数量必须在 1–1000 之间。");
        }
    }
}

public enum OperationalDiagnosticKind
{
    SyncStarted,
    SyncCompleted,
    PushFailure,
    PullFailure,
    RealtimeConnectionFailure,
    AuthenticationRefreshFailure,
    CloudEndpointAvailabilityFailure
}

public enum OperationalDiagnosticOutcome
{
    Started,
    Succeeded,
    Failed,
    Offline,
    Conflict,
    RetryScheduled,
    AuthenticationRequired,
    Unavailable,
    Cancelled
}

public enum OperationalFailureCategory
{
    None,
    Network,
    Authentication,
    Conflict,
    RateLimited,
    Server,
    LocalStorage,
    Configuration,
    InvalidData,
    Unknown
}

public record OperationalDiagnosticDraft
{
    public required OperationalDiagnosticKind Kind { get; init; }

    public required OperationalDiagnosticOutcome Outcome { get; init; }

    public OperationalFailureCategory FailureCategory { get; init; }

    public string? ErrorCode { get; init; }

    public long? DurationMilliseconds { get; init; }

    public int? PushedCount { get; init; }

    public int? PulledCount { get; init; }

    public int? ConflictCount { get; init; }

    public int? RetryCount { get; init; }
}

public sealed record OperationalDiagnosticEvent : OperationalDiagnosticDraft
{
    public required Guid Id { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public required string ApplicationVersion { get; init; }

    public required string ClientPlatform { get; init; }
}

public interface IOperationalDiagnosticsSink
{
    bool IsEnabled { get; }

    ValueTask RecordAsync(
        OperationalDiagnosticDraft diagnosticEvent,
        CancellationToken cancellationToken = default);
}

public interface IOperationalDiagnosticRepository
{
    Task AddAsync(
        OperationalDiagnosticEvent diagnosticEvent,
        int retentionLimit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperationalDiagnosticEvent>> GetRecentAsync(
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IClientPlatformProvider
{
    ValueTask<string> GetPlatformAsync(CancellationToken cancellationToken = default);
}

public interface ILocalStorageHealthService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

public sealed class DisabledOperationalDiagnosticsSink : IOperationalDiagnosticsSink
{
    public static DisabledOperationalDiagnosticsSink Instance { get; } = new();

    private DisabledOperationalDiagnosticsSink()
    {
    }

    public bool IsEnabled => false;

    public ValueTask RecordAsync(
        OperationalDiagnosticDraft diagnosticEvent,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

public static partial class OperationalDiagnosticSanitizer
{
    private const string RedactedErrorCode = "redacted_error";

    public static OperationalDiagnosticEvent Sanitize(OperationalDiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        return diagnosticEvent with
        {
            ApplicationVersion = NormalizeApplicationVersion(diagnosticEvent.ApplicationVersion),
            ClientPlatform = NormalizePlatform(diagnosticEvent.ClientPlatform),
            ErrorCode = NormalizeErrorCode(diagnosticEvent.ErrorCode),
            DurationMilliseconds = NormalizeLong(diagnosticEvent.DurationMilliseconds, 86_400_000),
            PushedCount = NormalizeCount(diagnosticEvent.PushedCount),
            PulledCount = NormalizeCount(diagnosticEvent.PulledCount),
            ConflictCount = NormalizeCount(diagnosticEvent.ConflictCount),
            RetryCount = NormalizeCount(diagnosticEvent.RetryCount)
        };
    }

    public static string? NormalizeErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length <= 64 && SafeErrorCodeRegex().IsMatch(normalized) &&
               !SensitiveNameRegex().IsMatch(normalized)
            ? normalized
            : RedactedErrorCode;
    }

    public static string NormalizePlatform(string? value) => value?.Trim() switch
    {
        "Windows" => "Windows",
        "Android" => "Android",
        "iPadOS" => "iPadOS",
        "iOS" => "iOS",
        "macOS" => "macOS",
        "Linux" => "Linux",
        _ => "Browser"
    };

    private static string NormalizeApplicationVersion(string? value) =>
        Version.TryParse(value, out var version) ? version.ToString() : "unknown";

    private static int? NormalizeCount(int? value) => value is >= 0 and <= 1_000_000 ? value : null;

    private static long? NormalizeLong(long? value, long maximum) =>
        value is >= 0 && value <= maximum ? value : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex SafeErrorCodeRegex();

    [GeneratedRegex("(?i)(token|password|passwd|authorization|credential|secret|bearer|basic|webdav)")]
    private static partial Regex SensitiveNameRegex();
}
