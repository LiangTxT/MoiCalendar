using MoiCalendar.Core;
using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.Tests;

public sealed class DiagnosticsAwareAccountServiceTests
{
    [Fact]
    public async Task RefreshFailure_RecordsOnlySafeCategoryAndCode()
    {
        var inner = new StubAccountService
        {
            RefreshException = new AccountServiceException(
                "refresh_token=private-value; user@example.com")
        };
        var diagnostics = new RecordingDiagnosticsSink();
        var service = new DiagnosticsAwareAccountService(inner, diagnostics);

        var error = await Assert.ThrowsAsync<AccountServiceException>(
            () => service.RefreshSessionAsync());

        Assert.Contains("private-value", error.Message, StringComparison.Ordinal);
        var entry = Assert.Single(diagnostics.Events);
        Assert.Equal(OperationalDiagnosticKind.AuthenticationRefreshFailure, entry.Kind);
        Assert.Equal(OperationalFailureCategory.Authentication, entry.FailureCategory);
        Assert.Equal("authentication_refresh_failed", entry.ErrorCode);
        Assert.DoesNotContain("private-value", entry.ErrorCode, StringComparison.Ordinal);
        Assert.DoesNotContain("user@example.com", entry.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsFailure_DoesNotReplaceAuthenticationResult()
    {
        var inner = new StubAccountService { RefreshResult = null };
        var service = new DiagnosticsAwareAccountService(inner, new ThrowingDiagnosticsSink());

        var account = await service.RefreshSessionAsync();

        Assert.Null(account);
    }

    private sealed class StubAccountService : IAccountService
    {
        public bool IsAvailable => true;
        public bool IsPasswordRecovery => false;
        public CloudAccount? RefreshResult { get; init; }
        public Exception? RefreshException { get; init; }

        public Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default) =>
            RefreshException is null
                ? Task.FromResult(RefreshResult)
                : Task.FromException<CloudAccount?>(RefreshException);

        public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudAccount?>(null);

        public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudAccount?>(null);

        public Task<AccountRegistrationResult> RegisterAsync(
            string emailAddress,
            string password,
            string emailRedirectUrl,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CloudAccount> LoginAsync(
            string emailAddress,
            string password,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RequestPasswordResetAsync(
            string emailAddress,
            string redirectUrl,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CloudAccount> CompletePasswordResetAsync(
            string newPassword,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingDiagnosticsSink : IOperationalDiagnosticsSink
    {
        public bool IsEnabled => true;
        public List<OperationalDiagnosticDraft> Events { get; } = [];

        public ValueTask RecordAsync(
            OperationalDiagnosticDraft diagnosticEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDiagnosticsSink : IOperationalDiagnosticsSink
    {
        public bool IsEnabled => true;

        public ValueTask RecordAsync(
            OperationalDiagnosticDraft diagnosticEvent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("诊断存储不可用"));
    }
}
