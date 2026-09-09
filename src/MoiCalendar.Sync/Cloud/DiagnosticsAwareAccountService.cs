using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

internal sealed class DiagnosticsAwareAccountService(
    IAccountService inner,
    IOperationalDiagnosticsSink diagnostics) : IAccountService
{
    public bool IsAvailable => inner.IsAvailable;

    public bool IsPasswordRecovery => inner.IsPasswordRecovery;

    public Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default) =>
        inner.RestoreSessionAsync(cancellationToken);

    public Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
        inner.GetCurrentAccountAsync(cancellationToken);

    public async Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await inner.RefreshSessionAsync(cancellationToken);
            if (account is null)
            {
                await TryRecordRefreshFailureAsync("session_not_restored", cancellationToken);
            }
            return account;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountServiceException)
        {
            await TryRecordRefreshFailureAsync("authentication_refresh_failed", cancellationToken);
            throw;
        }
        catch
        {
            await TryRecordRefreshFailureAsync("authentication_refresh_failed", cancellationToken);
            throw;
        }
    }

    public Task<AccountRegistrationResult> RegisterAsync(
        string emailAddress,
        string password,
        string emailRedirectUrl,
        CancellationToken cancellationToken = default) =>
        inner.RegisterAsync(emailAddress, password, emailRedirectUrl, cancellationToken);

    public Task<CloudAccount> LoginAsync(
        string emailAddress,
        string password,
        CancellationToken cancellationToken = default) =>
        inner.LoginAsync(emailAddress, password, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        inner.LogoutAsync(cancellationToken);

    public Task RequestPasswordResetAsync(
        string emailAddress,
        string redirectUrl,
        CancellationToken cancellationToken = default) =>
        inner.RequestPasswordResetAsync(emailAddress, redirectUrl, cancellationToken);

    public Task<CloudAccount> CompletePasswordResetAsync(
        string newPassword,
        CancellationToken cancellationToken = default) =>
        inner.CompletePasswordResetAsync(newPassword, cancellationToken);

    private async ValueTask TryRecordRefreshFailureAsync(
        string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await diagnostics.RecordAsync(new OperationalDiagnosticDraft
            {
                Kind = OperationalDiagnosticKind.AuthenticationRefreshFailure,
                Outcome = OperationalDiagnosticOutcome.AuthenticationRequired,
                FailureCategory = OperationalFailureCategory.Authentication,
                ErrorCode = errorCode
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is already represented by the authentication operation.
        }
        catch
        {
            // Diagnostics are optional and must never replace the authentication result.
        }
    }
}
