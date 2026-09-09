using System.Text.Json;
using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;
using MoiCalendar.Sync.Diagnostics;

namespace MoiCalendar.Tests;

public sealed class OperationalDiagnosticsTests
{
    [Theory]
    [InlineData("Bearer-secret-token")]
    [InlineData("access_token")]
    [InlineData("refresh-token")]
    [InlineData("password")]
    [InlineData("authorization")]
    [InlineData("https://example.test/path?token=secret")]
    public void ErrorCodeSanitizer_RejectsSecretsAndUrls(string unsafeCode)
    {
        Assert.Equal("redacted_error", OperationalDiagnosticSanitizer.NormalizeErrorCode(unsafeCode));
    }

    [Fact]
    public async Task LocalSink_StoresOnlyWhitelistedStructuredFields()
    {
        var repository = new RecordingRepository();
        var sink = new LocalOperationalDiagnosticsSink(
            new DiagnosticsOptions { Enabled = true },
            repository,
            new FakePlatformProvider("Windows NT 10.0 detailed fingerprint"),
            TimeProvider.System,
            "2.0.28.0");

        await sink.RecordAsync(new OperationalDiagnosticDraft
        {
            Kind = OperationalDiagnosticKind.PushFailure,
            Outcome = OperationalDiagnosticOutcome.RetryScheduled,
            FailureCategory = OperationalFailureCategory.Network,
            ErrorCode = "Authorization=Bearer-secret",
            RetryCount = 2
        });

        var stored = Assert.Single(repository.Events);
        Assert.Equal("Browser", stored.ClientPlatform);
        Assert.Equal("2.0.28.0", stored.ApplicationVersion);
        Assert.Equal("redacted_error", stored.ErrorCode);
        Assert.Equal(2, stored.RetryCount);
        var propertyNames = typeof(OperationalDiagnosticEvent).GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(propertyNames, name => name.Contains("Title", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Description", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Location", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(propertyNames, name => name.Contains("Url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DisabledDiagnostics_DoesNotPersistEventsAndExportsHealthOnly()
    {
        var repository = new RecordingRepository();
        var options = new DiagnosticsOptions { Enabled = false };
        var sink = new LocalOperationalDiagnosticsSink(
            options,
            repository,
            new FakePlatformProvider("Windows"),
            TimeProvider.System,
            "2.0.28.0");
        await sink.RecordAsync(new OperationalDiagnosticDraft
        {
            Kind = OperationalDiagnosticKind.SyncStarted,
            Outcome = OperationalDiagnosticOutcome.Started
        });

        var service = CreateReportService(options, repository);
        var export = await service.CreateExportAsync();

        Assert.False(sink.IsEnabled);
        Assert.Empty(repository.Events);
        Assert.Equal(0, export.EventCount);
        Assert.Contains("\"operationalEventCollectionEnabled\": false", export.Json, StringComparison.Ordinal);
        Assert.Contains("\"operationalEvents\": []", export.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticExport_ContainsHealthAndEventsButNoCalendarOrSecrets()
    {
        const string privateCalendarTitle = "绝密牙医预约";
        const string secretToken = "private-access-token";
        var repository = new RecordingRepository();
        repository.Events.Add(OperationalDiagnosticSanitizer.Sanitize(new OperationalDiagnosticEvent
        {
            Id = Guid.NewGuid(),
            TimestampUtc = DateTimeOffset.UtcNow,
            ApplicationVersion = "2.0.28.0",
            ClientPlatform = "Windows",
            Kind = OperationalDiagnosticKind.PullFailure,
            Outcome = OperationalDiagnosticOutcome.Offline,
            FailureCategory = OperationalFailureCategory.Network,
            ErrorCode = "network_unavailable",
            RetryCount = 1
        }));
        var service = CreateReportService(new DiagnosticsOptions { Enabled = true }, repository);

        var export = await service.CreateExportAsync();

        Assert.Matches("^moicalendar-diagnostics-\\d{4}-\\d{2}-\\d{2}-\\d{6}\\.json$", export.FileName);
        using var document = JsonDocument.Parse(export.Json);
        Assert.Equal("moicalendar-diagnostics", document.RootElement.GetProperty("format").GetString());
        Assert.Equal("Available", document.RootElement.GetProperty("health").GetProperty("localIndexedDbState").GetString());
        Assert.Equal(4, document.RootElement.GetProperty("health").GetProperty("pendingOutboxCount").GetInt32());
        Assert.DoesNotContain(privateCalendarTitle, export.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(secretToken, export.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("calendarTitle", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("location", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", export.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CloudSyncFailureKind.AuthenticationRequired, "http_401", 401, OperationalFailureCategory.Authentication)]
    [InlineData(CloudSyncFailureKind.Transient, "network_unavailable", null, OperationalFailureCategory.Network)]
    [InlineData(CloudSyncFailureKind.Transient, "http_429", 429, OperationalFailureCategory.RateLimited)]
    [InlineData(CloudSyncFailureKind.Transient, "http_503", 503, OperationalFailureCategory.Server)]
    [InlineData(CloudSyncFailureKind.Permanent, "invalid_server_payload", 400, OperationalFailureCategory.InvalidData)]
    public void ErrorClassifier_UsesSafeOperationalCategories(
        CloudSyncFailureKind kind,
        string code,
        int? status,
        OperationalFailureCategory expected)
    {
        var exception = new CloudSyncTransportException("untrusted provider message", kind, code, status);

        Assert.Equal(expected, DiagnosticErrorClassifier.Classify(exception));
        Assert.Equal(code, DiagnosticErrorClassifier.SafeCode(code));
    }

    private static DiagnosticReportService CreateReportService(
        DiagnosticsOptions options,
        RecordingRepository repository) => new(
            options,
            repository,
            new FakeLocalStorageHealthService(),
            new FakePlatformProvider("Windows"),
            new FakeCloudBackend(),
            new FakeAccountService(),
            new FakeCloudSyncStatusService(),
            new FakeRealtimeNotifier(),
            TimeProvider.System,
            "2.0.28.0");

    private sealed class RecordingRepository : IOperationalDiagnosticRepository
    {
        public List<OperationalDiagnosticEvent> Events { get; } = [];

        public Task AddAsync(OperationalDiagnosticEvent diagnosticEvent, int retentionLimit, CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OperationalDiagnosticEvent>> GetRecentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalDiagnosticEvent>>(Events.ToArray());

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Events.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatformProvider(string platform) : IClientPlatformProvider
    {
        public ValueTask<string> GetPlatformAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(platform);
    }

    private sealed class FakeLocalStorageHealthService : ILocalStorageHealthService
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeCloudBackend : ICloudBackend
    {
        public string ProviderId => "Test";
        public bool IsEnabled => true;
        public CloudBackendCapabilities Capabilities => new(true, true, true);
    }

    private sealed class FakeCloudSyncStatusService : ICloudSyncStatusService
    {
        public Task<CloudSyncSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CloudSyncSnapshot(
                CloudSyncDisplayState.RetryScheduled,
                4,
                0,
                new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 10, 8, 5, 0, TimeSpan.Zero),
                17,
                RealtimeConnectionState.Reconnecting,
                true));

        public Task<CloudSyncSnapshot> SynchronizeNowAsync(CancellationToken cancellationToken = default) =>
            GetStatusAsync(cancellationToken);
        public Task<IReadOnlyList<CloudSyncConflictDetails>> GetConflictsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudSyncConflictDetails>>([]);
        public Task<CloudSyncSnapshot> ResolveConflictAsync(Guid mutationId, CloudConflictResolution resolution, CancellationToken cancellationToken = default) =>
            GetStatusAsync(cancellationToken);
    }

    private sealed class FakeAccountService : IAccountService
    {
        public bool IsAvailable => true;
        public bool IsPasswordRecovery => false;
        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudAccount?>(new("account-id-not-exported", null, null, true));
        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) => GetCurrentAccountAsync(cancellationToken);
        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) => GetCurrentAccountAsync(cancellationToken);
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AccountRegistrationResult> RegisterAsync(string emailAddress, string password, string emailRedirectUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudAccount> LoginAsync(string emailAddress, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RequestPasswordResetAsync(string emailAddress, string redirectUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudAccount> CompletePasswordResetAsync(string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeRealtimeNotifier : IRealtimeNotifier
    {
        public bool IsAvailable => true;
        public RealtimeConnectionState ConnectionState => RealtimeConnectionState.Reconnecting;
        public event EventHandler<RealtimeWakeUpEventArgs>? WakeUp { add { } remove { } }
        public event EventHandler<RealtimeConnectionStateChangedEventArgs>? ConnectionStateChanged { add { } remove { } }
        public Task StartAsync(string accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
