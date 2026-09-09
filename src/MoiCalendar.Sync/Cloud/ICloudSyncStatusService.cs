using System.Text.Json;
using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

public enum CloudSyncDisplayState
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
    Error
}

public enum CloudConflictResolution
{
    KeepLocal,
    KeepCloud
}

public sealed record CloudSyncSnapshot(
    CloudSyncDisplayState State,
    int PendingChangeCount,
    int ConflictCount,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    long ServerCursor,
    RealtimeConnectionState RealtimeState,
    bool IsRealtimeAvailable);

public sealed record CloudSyncConflictDetails(
    Guid MutationId,
    Guid EntityId,
    SyncOperationType Operation,
    string Reason,
    CalendarEvent? LocalVersion,
    CalendarEvent? CloudVersion,
    DateTimeOffset? DetectedAtUtc,
    bool CanKeepLocal,
    bool CanKeepCloud);

public interface ICloudSyncStatusService
{
    Task<CloudSyncSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CloudSyncSnapshot> SynchronizeNowAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudSyncConflictDetails>> GetConflictsAsync(
        CancellationToken cancellationToken = default);

    Task<CloudSyncSnapshot> ResolveConflictAsync(
        Guid mutationId,
        CloudConflictResolution resolution,
        CancellationToken cancellationToken = default);
}

public sealed class CloudConflictResolutionException : Exception
{
    public CloudConflictResolutionException(string message)
        : base(message)
    {
    }

    public CloudConflictResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CloudSyncStatusService(
    ICloudSyncService cloudSyncService,
    IAccountService accountService,
    IRealtimeNotifier realtimeNotifier,
    ISyncOutboxRepository outboxRepository,
    ICloudConflictRepository conflictRepository,
    ISyncStateRepository syncStateRepository,
    ICloudSyncBindingRepository bindingRepository,
    IEventRepository eventRepository,
    TimeProvider timeProvider) : ICloudSyncStatusService, IDisposable
{
    private const int MaximumStatusEntryCount = 1_000;
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim manualSyncGate = new(1, 1);
    private bool manualSyncFailed;

    public async Task<CloudSyncSnapshot> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await outboxRepository.GetPendingAsync(
            MaximumStatusEntryCount,
            cancellationToken);
        var conflicts = pending.Count(IsUserVisibleConflict);
        var nextRetry = pending
            .Where(entry => entry.NextAttemptAtUtc is not null)
            .Select(entry => entry.NextAttemptAtUtc)
            .Min();
        var hasAuthenticationFailure = pending.Any(
            entry => entry.LastErrorCategory == SyncOutboxErrorCategory.Authentication);
        var hasPermanentFailure = pending.Any(
            entry => entry.LastErrorCategory == SyncOutboxErrorCategory.Permanent);

        CloudAccount? account = null;
        var accountReadFailed = false;
        if (accountService.IsAvailable)
        {
            try
            {
                account = await accountService.GetCurrentAccountAsync(cancellationToken);
            }
            catch (AccountServiceException)
            {
                accountReadFailed = true;
            }
        }

        var binding = await bindingRepository.GetAsync(cancellationToken);
        var accountId = account?.Id ?? binding?.AccountId;
        var syncState = accountId is null
            ? null
            : await syncStateRepository.GetAsync($"cloud:{accountId}", cancellationToken);

        var displayState = DetermineDisplayState(
            cloudSyncService.IsAvailable,
            account,
            accountReadFailed,
            pending.Count,
            conflicts,
            nextRetry,
            hasAuthenticationFailure,
            hasPermanentFailure);

        return new CloudSyncSnapshot(
            displayState,
            pending.Count,
            conflicts,
            syncState?.LastSuccessfulSyncAtUtc,
            nextRetry,
            syncState?.LastSuccessfulServerRevision ?? 0,
            realtimeNotifier.ConnectionState,
            realtimeNotifier.IsAvailable);
    }

    public async Task<CloudSyncSnapshot> SynchronizeNowAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await manualSyncGate.WaitAsync(0, cancellationToken))
        {
            return await GetStatusAsync(cancellationToken);
        }

        try
        {
            try
            {
                _ = await cloudSyncService.SynchronizeAsync(cancellationToken);
                manualSyncFailed = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Provider and server exception details deliberately stop at this application boundary.
                manualSyncFailed = true;
            }

            return await GetStatusAsync(cancellationToken);
        }
        finally
        {
            manualSyncGate.Release();
        }
    }

    public async Task<IReadOnlyList<CloudSyncConflictDetails>> GetConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await conflictRepository.GetConflictsAsync(
            MaximumStatusEntryCount,
            cancellationToken);
        var details = new List<CloudSyncConflictDetails>(entries.Count);
        foreach (var entry in entries)
        {
            var localVersion = await eventRepository.GetByIdIncludingDeletedAsync(
                entry.EntityId,
                cancellationToken) ?? TryDeserializeEvent(entry.Payload);
            var canKeepLocal = localVersion is not null &&
                entry.ConflictServerRevision is > 0 &&
                entry.ConflictRemoteEvent is { DeletedAtUtc: null } remoteEvent &&
                remoteEvent.Id == entry.EntityId &&
                IsKeepLocalConflict(entry.ConflictCode);
            var canKeepCloud = entry.ConflictServerRevision is > 0 &&
                entry.ConflictRemoteEvent?.Id == entry.EntityId;
            details.Add(new CloudSyncConflictDetails(
                entry.MutationId,
                entry.EntityId,
                entry.Operation,
                FormatConflictReason(entry.ConflictCode),
                localVersion,
                entry.ConflictRemoteEvent,
                entry.ConflictDetectedAtUtc,
                canKeepLocal,
                canKeepCloud));
        }

        return details;
    }

    public async Task<CloudSyncSnapshot> ResolveConflictAsync(
        Guid mutationId,
        CloudConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var conflict = (await conflictRepository.GetConflictsAsync(
                    MaximumStatusEntryCount,
                    cancellationToken))
                .SingleOrDefault(entry => entry.MutationId == mutationId)
                ?? throw new CloudConflictResolutionException("该冲突已不存在，请刷新状态。");

            switch (resolution)
            {
                case CloudConflictResolution.KeepLocal
                    when conflict.ConflictServerRevision is > 0 &&
                         conflict.ConflictRemoteEvent is { DeletedAtUtc: null } localRemoteEvent &&
                         localRemoteEvent.Id == conflict.EntityId &&
                         IsKeepLocalConflict(conflict.ConflictCode):
                    _ = await conflictRepository.KeepLocalAsync(
                        mutationId,
                        Guid.NewGuid(),
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                    break;
                case CloudConflictResolution.KeepCloud
                    when conflict.ConflictServerRevision is > 0 &&
                         conflict.ConflictRemoteEvent?.Id == conflict.EntityId:
                    _ = await conflictRepository.KeepCloudAsync(mutationId, cancellationToken);
                    break;
                default:
                    throw new CloudConflictResolutionException(
                        "该冲突缺少安全解决所需的云端版本或修订号，请稍后重试同步。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloudConflictResolutionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CloudConflictResolutionException(
                "冲突处理未完成，原冲突仍保留。",
                exception);
        }

        return await GetStatusAsync(cancellationToken);
    }

    public void Dispose() => manualSyncGate.Dispose();

    private CloudSyncDisplayState DetermineDisplayState(
        bool isCloudAvailable,
        CloudAccount? account,
        bool accountReadFailed,
        int pendingCount,
        int conflictCount,
        DateTimeOffset? nextRetry,
        bool hasAuthenticationFailure,
        bool hasPermanentFailure)
    {
        if (!isCloudAvailable)
        {
            return CloudSyncDisplayState.LocalOnly;
        }
        if (cloudSyncService.CurrentStatus == CloudSyncStatus.Syncing)
        {
            return CloudSyncDisplayState.Syncing;
        }
        if (accountReadFailed)
        {
            return CloudSyncDisplayState.Offline;
        }
        if (account is null)
        {
            return cloudSyncService.CurrentStatus == CloudSyncStatus.AuthenticationRequired ||
                   hasAuthenticationFailure
                ? CloudSyncDisplayState.AuthenticationRequired
                : CloudSyncDisplayState.SignedOut;
        }
        if (cloudSyncService.CurrentStatus == CloudSyncStatus.AuthenticationRequired ||
            hasAuthenticationFailure)
        {
            return CloudSyncDisplayState.AuthenticationRequired;
        }
        if (conflictCount > 0)
        {
            return CloudSyncDisplayState.Conflict;
        }
        if (cloudSyncService.CurrentStatus == CloudSyncStatus.Offline)
        {
            return CloudSyncDisplayState.Offline;
        }
        if (cloudSyncService.CurrentStatus == CloudSyncStatus.RetryScheduled ||
            nextRetry > timeProvider.GetUtcNow())
        {
            return CloudSyncDisplayState.RetryScheduled;
        }
        if (manualSyncFailed || hasPermanentFailure ||
            cloudSyncService.CurrentStatus == CloudSyncStatus.Error)
        {
            return CloudSyncDisplayState.Error;
        }
        if (pendingCount > 0)
        {
            return CloudSyncDisplayState.PendingChanges;
        }

        return CloudSyncDisplayState.Synced;
    }

    private static bool IsKeepLocalConflict(string? code) =>
        string.Equals(code, "stale_revision", StringComparison.Ordinal) ||
        string.Equals(code, "entity_exists", StringComparison.Ordinal);

    private static bool IsUserVisibleConflict(SyncOutboxEntry entry) =>
        entry.ConflictCode is not null &&
        !(entry.Operation == SyncOperationType.Delete &&
          string.Equals(entry.ConflictCode, "entity_not_found", StringComparison.Ordinal));

    private static string FormatConflictReason(string? code) => code switch
    {
        "stale_revision" => "本地修改基于较旧的云端版本。",
        "entity_exists" => "云端已存在同一日程，但本地变更没有对应的基线版本。",
        "entity_deleted" => "该日程已在云端删除。",
        "entity_not_found" => "云端找不到该日程。",
        "mutation_id_reused" => "变更标识与之前的请求不一致。",
        "invalid_create_base_revision" => "新建请求携带了不适用的云端修订号。",
        _ => "本地版本与云端版本无法安全地自动合并。"
    };

    private static CalendarEvent? TryDeserializeEvent(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CalendarEvent>(payload, PayloadSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
