using System.Text.Json;
using System.Text.Json.Serialization;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Sync.Diagnostics;

public enum DiagnosticAuthenticationState
{
    Disabled,
    SignedOut,
    SignedIn,
    Unavailable
}

public enum DiagnosticLocalStorageState
{
    Available,
    Unavailable
}

public enum DiagnosticSyncState
{
    LocalOnly,
    SignedOut,
    Synced,
    Syncing,
    Offline,
    PendingChanges,
    RetryScheduled,
    AuthenticationRequired,
    Conflict,
    Error,
    Unavailable
}

public sealed record DiagnosticHealthSnapshot(
    string ApplicationVersion,
    string ClientPlatform,
    DiagnosticLocalStorageState LocalIndexedDbState,
    bool CloudBackendConfigured,
    DiagnosticAuthenticationState AuthenticationState,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    int? PendingOutboxCount,
    DiagnosticSyncState CurrentSyncState,
    RealtimeConnectionState RealtimeState,
    bool RealtimeAvailable);

public sealed record DiagnosticReport(
    bool OperationalEventCollectionEnabled,
    DateTimeOffset GeneratedAtUtc,
    DiagnosticHealthSnapshot Health,
    IReadOnlyList<OperationalDiagnosticEvent> OperationalEvents);

public sealed record DiagnosticExport(string FileName, string Json, int EventCount);

public interface IDiagnosticReportService
{
    bool IsEventCollectionEnabled { get; }

    Task<DiagnosticReport> GetReportAsync(CancellationToken cancellationToken = default);

    Task<DiagnosticExport> CreateExportAsync(CancellationToken cancellationToken = default);

    Task ClearEventsAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalOperationalDiagnosticsSink(
    DiagnosticsOptions options,
    IOperationalDiagnosticRepository repository,
    IClientPlatformProvider clientPlatformProvider,
    TimeProvider timeProvider,
    string applicationVersion) : IOperationalDiagnosticsSink
{
    public bool IsEnabled => options.Enabled;

    public async ValueTask RecordAsync(
        OperationalDiagnosticDraft diagnosticEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (!IsEnabled || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var platform = await clientPlatformProvider.GetPlatformAsync(cancellationToken);
            var safeEvent = OperationalDiagnosticSanitizer.Sanitize(new OperationalDiagnosticEvent
            {
                Id = Guid.NewGuid(),
                TimestampUtc = timeProvider.GetUtcNow(),
                ApplicationVersion = applicationVersion,
                ClientPlatform = platform,
                Kind = diagnosticEvent.Kind,
                Outcome = diagnosticEvent.Outcome,
                FailureCategory = diagnosticEvent.FailureCategory,
                ErrorCode = diagnosticEvent.ErrorCode,
                DurationMilliseconds = diagnosticEvent.DurationMilliseconds,
                PushedCount = diagnosticEvent.PushedCount,
                PulledCount = diagnosticEvent.PulledCount,
                ConflictCount = diagnosticEvent.ConflictCount,
                RetryCount = diagnosticEvent.RetryCount
            });
            await repository.AddAsync(safeEvent, options.RetentionLimit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Diagnostics are best effort and never make the calendar operation fail.
        }
        catch
        {
            // Local diagnostics are optional and never affect local-first behavior.
        }
    }
}

public sealed class DiagnosticReportService(
    DiagnosticsOptions options,
    IOperationalDiagnosticRepository repository,
    ILocalStorageHealthService localStorageHealthService,
    IClientPlatformProvider clientPlatformProvider,
    ICloudBackend cloudBackend,
    IAccountService accountService,
    ICloudSyncStatusService cloudSyncStatusService,
    IRealtimeNotifier realtimeNotifier,
    TimeProvider timeProvider,
    string applicationVersion) : IDiagnosticReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool IsEventCollectionEnabled => options.Enabled;

    public async Task<DiagnosticReport> GetReportAsync(
        CancellationToken cancellationToken = default)
    {
        var localState = await ReadLocalStorageStateAsync(cancellationToken);
        var platform = await ReadPlatformAsync(cancellationToken);
        var authenticationState = await ReadAuthenticationStateAsync(cancellationToken);
        var syncSnapshot = await ReadSyncSnapshotAsync(cancellationToken);
        var events = options.Enabled
            ? await ReadEventsAsync(cancellationToken)
            : [];

        return new DiagnosticReport(
            options.Enabled,
            timeProvider.GetUtcNow(),
            new DiagnosticHealthSnapshot(
                NormalizeVersion(applicationVersion),
                OperationalDiagnosticSanitizer.NormalizePlatform(platform),
                localState,
                cloudBackend.IsEnabled,
                authenticationState,
                syncSnapshot?.LastSuccessfulSyncAtUtc,
                syncSnapshot?.PendingChangeCount,
                MapSyncState(syncSnapshot?.State),
                realtimeNotifier.ConnectionState,
                realtimeNotifier.IsAvailable),
            events);
    }

    public async Task<DiagnosticExport> CreateExportAsync(
        CancellationToken cancellationToken = default)
    {
        var report = await GetReportAsync(cancellationToken);
        var document = new DiagnosticExportDocument(
            "moicalendar-diagnostics",
            1,
            report.GeneratedAtUtc,
            report.OperationalEventCollectionEnabled,
            report.Health,
            report.OperationalEvents);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        return new DiagnosticExport(
            $"moicalendar-diagnostics-{report.GeneratedAtUtc:yyyy-MM-dd-HHmmss}.json",
            json,
            report.OperationalEvents.Count);
    }

    public async Task ClearEventsAsync(CancellationToken cancellationToken = default)
    {
        if (options.Enabled)
        {
            await repository.ClearAsync(cancellationToken);
        }
    }

    private async Task<DiagnosticLocalStorageState> ReadLocalStorageStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await localStorageHealthService.IsAvailableAsync(cancellationToken)
                ? DiagnosticLocalStorageState.Available
                : DiagnosticLocalStorageState.Unavailable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return DiagnosticLocalStorageState.Unavailable;
        }
    }

    private async Task<string> ReadPlatformAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await clientPlatformProvider.GetPlatformAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "Browser";
        }
    }

    private async Task<DiagnosticAuthenticationState> ReadAuthenticationStateAsync(
        CancellationToken cancellationToken)
    {
        if (!accountService.IsAvailable)
        {
            return DiagnosticAuthenticationState.Disabled;
        }

        try
        {
            return await accountService.GetCurrentAccountAsync(cancellationToken) is null
                ? DiagnosticAuthenticationState.SignedOut
                : DiagnosticAuthenticationState.SignedIn;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountServiceException)
        {
            return DiagnosticAuthenticationState.Unavailable;
        }
    }

    private async Task<CloudSyncSnapshot?> ReadSyncSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await cloudSyncStatusService.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<OperationalDiagnosticEvent>> ReadEventsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return (await repository.GetRecentAsync(cancellationToken))
                .Select(OperationalDiagnosticSanitizer.Sanitize)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static DiagnosticSyncState MapSyncState(CloudSyncDisplayState? state) => state switch
    {
        CloudSyncDisplayState.LocalOnly => DiagnosticSyncState.LocalOnly,
        CloudSyncDisplayState.SignedOut => DiagnosticSyncState.SignedOut,
        CloudSyncDisplayState.Synced => DiagnosticSyncState.Synced,
        CloudSyncDisplayState.Syncing => DiagnosticSyncState.Syncing,
        CloudSyncDisplayState.Offline => DiagnosticSyncState.Offline,
        CloudSyncDisplayState.PendingChanges => DiagnosticSyncState.PendingChanges,
        CloudSyncDisplayState.RetryScheduled => DiagnosticSyncState.RetryScheduled,
        CloudSyncDisplayState.AuthenticationRequired => DiagnosticSyncState.AuthenticationRequired,
        CloudSyncDisplayState.Conflict => DiagnosticSyncState.Conflict,
        CloudSyncDisplayState.Error => DiagnosticSyncState.Error,
        _ => DiagnosticSyncState.Unavailable
    };

    private static string NormalizeVersion(string value) =>
        Version.TryParse(value, out var version) ? version.ToString() : "unknown";

    private sealed record DiagnosticExportDocument(
        string Format,
        int SchemaVersion,
        DateTimeOffset ExportedAtUtc,
        bool OperationalEventCollectionEnabled,
        DiagnosticHealthSnapshot Health,
        IReadOnlyList<OperationalDiagnosticEvent> OperationalEvents);
}

public static class DiagnosticErrorClassifier
{
    public static OperationalFailureCategory Classify(CloudSyncTransportException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.FailureKind == CloudSyncFailureKind.AuthenticationRequired)
        {
            return OperationalFailureCategory.Authentication;
        }
        if (exception.StatusCode == 429 || exception.ErrorCode.Contains("rate", StringComparison.OrdinalIgnoreCase))
        {
            return OperationalFailureCategory.RateLimited;
        }
        if (exception.ErrorCode is "network_unavailable" or "request_timeout")
        {
            return OperationalFailureCategory.Network;
        }
        if (exception.StatusCode >= 500)
        {
            return OperationalFailureCategory.Server;
        }
        if (exception.ErrorCode.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            exception.ErrorCode.Contains("cursor", StringComparison.OrdinalIgnoreCase))
        {
            return OperationalFailureCategory.InvalidData;
        }
        return exception.FailureKind == CloudSyncFailureKind.Permanent
            ? OperationalFailureCategory.InvalidData
            : OperationalFailureCategory.Unknown;
    }

    public static string? SafeCode(string? value) =>
        OperationalDiagnosticSanitizer.NormalizeErrorCode(value);
}
