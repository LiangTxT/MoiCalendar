using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbReminderStateStore(IndexedDbConnection connection) : IReminderStateStore
{
    public Task<bool> GetEnabledAsync(CancellationToken cancellationToken = default) => connection.InvokeAsync<bool>("getRemindersEnabled", cancellationToken);
    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => connection.InvokeAsync<object?>("setRemindersEnabled", cancellationToken, enabled);
    public Task<bool> ClaimAsync(string key, DateTimeOffset now, CancellationToken cancellationToken = default) => connection.InvokeAsync<bool>("claimReminder", cancellationToken, key, now);
    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default) => connection.InvokeAsync<object?>("releaseReminder", cancellationToken, key);
    public Task<string?> GetPushOwnerAsync(CancellationToken cancellationToken = default) => connection.InvokeAsync<string?>("getReminderPushOwner", cancellationToken);
    public Task SetPushOwnerAsync(string? owner, CancellationToken cancellationToken = default) => connection.InvokeAsync<object?>("setReminderPushOwner", cancellationToken, owner);
}
