using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

/// <summary>
/// Keeps the provider-independent Realtime lifecycle aligned with authentication without
/// requiring UI components to know which cloud provider is active.
/// </summary>
internal sealed class RealtimeAwareAccountService(
    IAccountService inner,
    IRealtimeNotifier realtimeNotifier,
    AutoSyncSignal? autoSyncSignal = null,
    IReminderStateStore? reminderState = null,
    ICloudReminderTransport? reminders = null,
    IDeviceService? devices = null) : IAccountService
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
        if (result.IsSignedIn) NotifyAccountReady();
        return result;
    }

    public async Task<CloudAccount> LoginAsync(
        string emailAddress,
        string password,
        CancellationToken cancellationToken = default)
    {
        var account = await inner.LoginAsync(emailAddress, password, cancellationToken);
        await AlignSubscriptionAsync(account, cancellationToken);
        NotifyAccountReady();
        return account;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        // Clear the service-worker gate even offline, before dropping the session.
        if (reminderState is not null) await reminderState.SetPushOwnerAsync(null, cancellationToken);
        if (reminders?.IsAvailable == true && devices is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { await reminders.UnregisterAsync((await devices.GetDeviceIdentityAsync(timeout.Token)).DeviceId, timeout.Token); }
            catch (Exception ex) when (ex is InvalidOperationException or AccountServiceException or HttpRequestException or OperationCanceledException)
            { /* An offline logout still blocks old pushes locally. */ }
        }
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
        NotifyAccountReady();
        return account;
    }

    private void NotifyAccountReady()
    {
        try { autoSyncSignal?.NotifyAccountReady(); }
        catch { /* Synchronization failure must not invalidate successful authentication. */ }
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
