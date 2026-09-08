namespace MoiCalendar.Sync.Cloud;

/// <summary>
/// Keeps the provider-independent Realtime lifecycle aligned with authentication without
/// requiring UI components to know which cloud provider is active.
/// </summary>
internal sealed class RealtimeAwareAccountService(
    IAccountService inner,
    IRealtimeNotifier realtimeNotifier) : IAccountService
{
    public bool IsAvailable => inner.IsAvailable;

    public bool IsPasswordRecovery => inner.IsPasswordRecovery;

    public async Task<CloudAccount?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var account = await inner.RestoreSessionAsync(cancellationToken);
        await AlignSubscriptionAsync(account, cancellationToken);
        return account;
    }

    public async Task<CloudAccount?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        var account = await inner.GetCurrentAccountAsync(cancellationToken);
        await AlignSubscriptionAsync(account, cancellationToken);
        return account;
    }

    public async Task<CloudAccount?> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var account = await inner.RefreshSessionAsync(cancellationToken);
        await AlignSubscriptionAsync(account, cancellationToken);
        return account;
    }

    public async Task<AccountRegistrationResult> RegisterAsync(
        string emailAddress,
        string password,
        string emailRedirectUrl,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.RegisterAsync(
            emailAddress,
            password,
            emailRedirectUrl,
            cancellationToken);
        await AlignSubscriptionAsync(result.IsSignedIn ? result.Account : null, cancellationToken);
        return result;
    }

    public async Task<CloudAccount> LoginAsync(
        string emailAddress,
        string password,
        CancellationToken cancellationToken = default)
    {
        var account = await inner.LoginAsync(emailAddress, password, cancellationToken);
        await AlignSubscriptionAsync(account, cancellationToken);
        return account;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.LogoutAsync(cancellationToken);
        }
        finally
        {
            await TryStopAsync(CancellationToken.None);
        }
    }

    public Task RequestPasswordResetAsync(
        string emailAddress,
        string redirectUrl,
        CancellationToken cancellationToken = default) =>
        inner.RequestPasswordResetAsync(emailAddress, redirectUrl, cancellationToken);

    public async Task<CloudAccount> CompletePasswordResetAsync(
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var account = await inner.CompletePasswordResetAsync(newPassword, cancellationToken);
        await AlignSubscriptionAsync(account, cancellationToken);
        return account;
    }

    private async Task AlignSubscriptionAsync(
        CloudAccount? account,
        CancellationToken cancellationToken)
    {
        if (!realtimeNotifier.IsAvailable || account is null)
        {
            await TryStopAsync(cancellationToken);
            return;
        }

        try
        {
            await realtimeNotifier.StartAsync(account.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Authentication and local-first use must remain available when Realtime is blocked.
        }
    }

    private async Task TryStopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await realtimeNotifier.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Stopping a best-effort wake-up channel must not invalidate the account action.
        }
    }
}
