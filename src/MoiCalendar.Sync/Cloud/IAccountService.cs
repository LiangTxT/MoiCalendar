namespace MoiCalendar.Sync.Cloud;

public interface IAccountService
{
    bool IsAvailable { get; }

    bool IsPasswordRecovery { get; }

    Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default);

    Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default);

    Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default);

    Task<AccountRegistrationResult> RegisterAsync(
        string emailAddress,
        string password,
        string emailRedirectUrl,
        CancellationToken cancellationToken = default);

    Task<CloudAccount> LoginAsync(
        string emailAddress,
        string password,
        CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    Task RequestPasswordResetAsync(
        string emailAddress,
        string redirectUrl,
        CancellationToken cancellationToken = default);

    Task<CloudAccount> CompletePasswordResetAsync(
        string newPassword,
        CancellationToken cancellationToken = default);
}

public sealed record CloudAccount(
    string Id,
    string? DisplayName,
    string? EmailAddress,
    bool IsEmailVerified);

public sealed record AccountRegistrationResult(
    CloudAccount Account,
    bool IsSignedIn,
    bool EmailVerificationRequired);

public class AccountServiceException : Exception
{
    public AccountServiceException(string message)
        : base(message)
    {
    }

    public AccountServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
