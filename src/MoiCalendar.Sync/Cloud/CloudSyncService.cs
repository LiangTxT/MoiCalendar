using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

public sealed class CloudSyncService(
    IAccountService accountService,
    ICloudSyncTransport transport,
    ICloudDeviceSyncService cloudDeviceSyncService,
    ISyncOutboxRepository outboxRepository,
    ISyncStateRepository syncStateRepository,
    ICloudSyncBindingRepository bindingRepository,
    ICloudChangeApplyRepository changeApplyRepository,
    ILocalDataOperationLock operationLock,
    IRestoreSyncGuard restoreSyncGuard,
    TimeProvider timeProvider,
    ICloudSyncRetryPolicy retryPolicy,
    ICloudSyncDelay syncDelay) : ICloudSyncService
{
    private const int BatchSize = 100;
    private const int MaximumPendingInspectionCount = 1_000;

    public bool IsAvailable => accountService.IsAvailable && transport.IsAvailable;

    public CloudSyncStatus CurrentStatus { get; private set; } = CloudSyncStatus.Idle;

    public async Task<CloudSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        CurrentStatus = CloudSyncStatus.Syncing;
        try
        {
            var result = await SynchronizeCoreAsync(cancellationToken);
            CurrentStatus = result.Status;
            return result;
        }
        catch (OperationCanceledException)
        {
            CurrentStatus = CloudSyncStatus.Idle;
            throw;
        }
        catch
        {
            CurrentStatus = CloudSyncStatus.Error;
            throw;
        }
    }

    private async Task<CloudSyncResult> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return Result(
                CloudSyncOutcome.Failed,
                CloudSyncStatus.Error,
                message: "云同步尚未配置。");
        }

        await using var lease = await operationLock.AcquireAsync(cancellationToken);
        if (await restoreSyncGuard.IsSyncBlockedAsync(cancellationToken))
        {
            return Result(
                CloudSyncOutcome.Failed,
                CloudSyncStatus.Error,
                message: "备份恢复后的同步保护仍处于启用状态。");
        }

        CloudAccount? account;
        try
        {
            account = await accountService.GetCurrentAccountAsync(cancellationToken);
        }
        catch (AccountServiceException)
        {
            return Result(
                CloudSyncOutcome.Failed,
                CloudSyncStatus.Offline,
                message: "无法恢复云账户会话，本地修改未受影响。请稍后重试。");
        }
        if (account is null)
        {
            return Result(CloudSyncOutcome.NotAuthenticated, CloudSyncStatus.AuthenticationRequired);
        }

        try
        {
            await bindingRepository.BindAsync(account.Id, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (SyncOperationException exception)
        {
            return Result(
                CloudSyncOutcome.AccountMismatch,
                CloudSyncStatus.Error,
                message: exception.Message);
        }

        Guid currentDeviceId;
        try
        {
            currentDeviceId = (await cloudDeviceSyncService.RegisterCurrentDeviceAsync(cancellationToken)).DeviceId;
        }
        catch (CloudSyncTransportException exception)
        {
            return DeviceFailureResult(exception);
        }
        catch (CloudDeviceServiceException exception)
        {
            return Result(
                CloudSyncOutcome.Failed,
                CloudSyncStatus.Error,
                message: exception.Message);
        }

        var scope = $"cloud:{account.Id}";
        var state = await syncStateRepository.GetAsync(scope, cancellationToken);
        var cursor = state?.LastSuccessfulServerRevision ?? 0;
        var pushedCount = 0;
        var pulledCount = 0;
        var conflicts = new List<CloudSyncConflict>();
        var permanentFailureCount = 0;

        var pendingAtStart = await outboxRepository.GetPendingAsync(
            MaximumPendingInspectionCount,
            cancellationToken);
        var blockedEntityIds = pendingAtStart
            .Where(entry => entry.ConflictCode is not null && !IsMissingEntityDelete(entry))
            .Select(entry => entry.EntityId)
            .ToHashSet();
        conflicts.AddRange(pendingAtStart
            .Where(entry => entry.ConflictCode is not null && !IsMissingEntityDelete(entry))
            .Select(ToConflict));

        while (true)
        {
            var pending = (await outboxRepository.GetReadyAsync(
                    timeProvider.GetUtcNow(),
                    BatchSize,
                    cancellationToken))
                .Where(entry => !blockedEntityIds.Contains(entry.EntityId))
                .ToArray();
            if (pending.Length == 0)
            {
                break;
            }

            foreach (var pendingEntry in pending)
            {
                if (blockedEntityIds.Contains(pendingEntry.EntityId))
                {
                    continue;
                }

                var entry = await outboxRepository.GetByIdAsync(
                    pendingEntry.MutationId,
                    cancellationToken);
                if (entry is null)
                {
                    continue;
                }

                var push = await PushWithRecoveryAsync(entry, cancellationToken);
                if (push.AuthenticationRequired)
                {
                    return Result(
                        CloudSyncOutcome.NotAuthenticated,
                        CloudSyncStatus.AuthenticationRequired,
                        pushedCount,
                        pulledCount,
                        cursor,
                        conflicts,
                        message: "云账户会话已失效；本地修改已保留，请重新登录后继续同步。");
                }
                if (push.TransientFailure is not null)
                {
                    var status = push.TransientFailure.ErrorCode == "network_unavailable"
                        ? CloudSyncStatus.Offline
                        : CloudSyncStatus.RetryScheduled;
                    return Result(
                        CloudSyncOutcome.Failed,
                        status,
                        pushedCount,
                        pulledCount,
                        cursor,
                        conflicts,
                        push.NextRetryAtUtc,
                        "云端暂时不可用，本地修改已保留并已安排有上限的重试。");
                }
                if (push.PermanentFailure is not null)
                {
                    permanentFailureCount++;
                    continue;
                }

                var response = push.Response!;
                if (!response.Applied)
                {
                    var conflict = new CloudSyncConflict(
                        entry.MutationId,
                        entry.EntityId,
                        response.ConflictCode ?? "unknown_conflict",
                        response.CurrentServerRevision,
                        entry.BaseRevision,
                        entry.Payload,
                        response.CurrentEntity);
                    await outboxRepository.RecordConflictAsync(
                        entry.MutationId,
                        timeProvider.GetUtcNow(),
                        conflict.Code,
                        conflict.CurrentServerRevision,
                        conflict.RemoteEvent,
                        cancellationToken);
                    conflicts.Add(conflict);
                    blockedEntityIds.Add(entry.EntityId);
                    continue;
                }

                if (response.ServerRevision is not > 0 ||
                    response.MutationId != entry.MutationId ||
                    response.EntityId != entry.EntityId)
                {
                    await outboxRepository.RecordAttemptAsync(
                        entry.MutationId,
                        timeProvider.GetUtcNow(),
                        SyncOutboxErrorCategory.Permanent,
                        "invalid_acknowledgement",
                        "云端返回了不一致的 mutation 确认结果。",
                        null,
                        cancellationToken);
                    permanentFailureCount++;
                    continue;
                }

                try
                {
                    await outboxRepository.AcknowledgeAsync(
                        entry.MutationId,
                        response.ServerRevision.Value,
                        cancellationToken);
                }
                catch (SyncOperationException)
                {
                    return Result(
                        CloudSyncOutcome.Failed,
                        CloudSyncStatus.Error,
                        pushedCount,
                        pulledCount,
                        cursor,
                        conflicts,
                        message: "云端已接收修改，但本地确认尚未完成；原 mutation 已保留，可安全重试。");
                }
                pushedCount++;
            }
        }

        while (true)
        {
            var pull = await PullWithRecoveryAsync(currentDeviceId, cursor, cancellationToken);
            if (pull.AuthenticationRequired)
            {
                return Result(
                    CloudSyncOutcome.NotAuthenticated,
                    CloudSyncStatus.AuthenticationRequired,
                    pushedCount,
                    pulledCount,
                    cursor,
                    conflicts,
                    message: "云账户会话已失效；已完成的本地批次保持安全，请重新登录后继续同步。");
            }
            if (pull.Failure is not null)
            {
                if (pull.Failure.ErrorCode is "device_revoked" or "device_not_found" or "device_id_unavailable")
                {
                    return DeviceFailureResult(
                        pull.Failure,
                        pushedCount,
                        pulledCount,
                        cursor);
                }
                var status = pull.Failure.ErrorCode == "network_unavailable"
                    ? CloudSyncStatus.Offline
                    : pull.Failure.FailureKind == CloudSyncFailureKind.Transient
                        ? CloudSyncStatus.RetryScheduled
                        : CloudSyncStatus.Error;
                return Result(
                    CloudSyncOutcome.Failed,
                    status,
                    pushedCount,
                    pulledCount,
                    cursor,
                    conflicts,
                    pull.NextRetryAtUtc,
                    "云端变更读取失败；已应用的本地批次和游标保持一致，可安全重试。");
            }

            var batch = pull.Batch!;
            try
            {
                ValidateBatch(batch, cursor);
                await changeApplyRepository.ApplyAndAdvanceCursorAsync(
                    scope,
                    batch.Changes,
                    batch.Cursor,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is CloudSyncTransportException or SyncOperationException)
            {
                return Result(
                    CloudSyncOutcome.Failed,
                    CloudSyncStatus.Error,
                    pushedCount,
                    pulledCount,
                    cursor,
                    conflicts,
                    message: "云端批次未能完整写入 IndexedDB，游标没有前进，可安全重新拉取。");
            }

            pulledCount += batch.Changes.Count;
            var previousCursor = cursor;
            cursor = batch.Cursor;
            if (!batch.HasMore)
            {
                break;
            }
            if (cursor <= previousCursor)
            {
                return Result(
                    CloudSyncOutcome.Failed,
                    CloudSyncStatus.Error,
                    pushedCount,
                    pulledCount,
                    cursor,
                    conflicts,
                    message: "云端分页游标没有前进，已停止同步且未丢失本地修改。");
            }
        }

        var remaining = await outboxRepository.GetPendingAsync(
            MaximumPendingInspectionCount,
            cancellationToken);
        permanentFailureCount = Math.Max(
            permanentFailureCount,
            remaining.Count(entry => entry.LastErrorCategory == SyncOutboxErrorCategory.Permanent));
        var nextRetryAtUtc = remaining
            .Where(entry => entry.LastErrorCategory == SyncOutboxErrorCategory.Transient)
            .Select(entry => entry.NextAttemptAtUtc)
            .Where(value => value is not null)
            .Min();

        if (conflicts.Count > 0)
        {
            return Result(
                CloudSyncOutcome.Conflict,
                CloudSyncStatus.Conflict,
                pushedCount,
                pulledCount,
                cursor,
                conflicts,
                nextRetryAtUtc,
                $"检测到 {conflicts.Count} 个同步冲突；冲突实体保持本地版本，其他实体已继续处理。");
        }
        if (permanentFailureCount > 0)
        {
            return Result(
                CloudSyncOutcome.Failed,
                CloudSyncStatus.Error,
                pushedCount,
                pulledCount,
                cursor,
                message: $"有 {permanentFailureCount} 个 mutation 需要人工检查；其他安全项目已继续同步。");
        }
        if (nextRetryAtUtc is not null)
        {
            return Result(
                CloudSyncOutcome.Failed,
                CloudSyncStatus.RetryScheduled,
                pushedCount,
                pulledCount,
                cursor,
                nextRetryAtUtc: nextRetryAtUtc,
                message: "部分本地修改仍在退避期内，将在稍后重试。");
        }

        try
        {
            await cloudDeviceSyncService.AcknowledgeSuccessfulSyncAsync(
                currentDeviceId,
                cursor,
                cancellationToken);
        }
        catch (CloudSyncTransportException exception)
        {
            return DeviceFailureResult(exception, pushedCount, pulledCount, cursor);
        }

        return Result(
            CloudSyncOutcome.Succeeded,
            CloudSyncStatus.Idle,
            pushedCount,
            pulledCount,
            cursor);
    }

    private async Task<PushExecution> PushWithRecoveryAsync(
        SyncOutboxEntry entry,
        CancellationToken cancellationToken)
    {
        var operationAttempts = 0;
        var refreshedSession = false;
        while (operationAttempts < retryPolicy.MaximumAttemptsPerOperation)
        {
            operationAttempts++;
            try
            {
                var response = await transport.PushAsync(
                    new CloudMutationRequest(
                        entry.MutationId,
                        entry.DeviceId,
                        entry.EntityId,
                        entry.Operation,
                        entry.BaseRevision,
                        entry.Payload),
                    cancellationToken);
                return new PushExecution(response);
            }
            catch (CloudSyncTransportException exception)
            {
                if (exception.FailureKind == CloudSyncFailureKind.AuthenticationRequired)
                {
                    await RecordFailureAsync(entry.MutationId, exception, null, cancellationToken);
                    if (refreshedSession)
                    {
                        return new PushExecution(AuthenticationRequired: true);
                    }

                    refreshedSession = true;
                    try
                    {
                        if (await accountService.RefreshSessionAsync(cancellationToken) is null)
                        {
                            return new PushExecution(AuthenticationRequired: true);
                        }
                    }
                    catch (AccountServiceException)
                    {
                        return new PushExecution(AuthenticationRequired: true);
                    }

                    operationAttempts = retryPolicy.MaximumAttemptsPerOperation - 1;
                    continue;
                }

                if (exception.FailureKind == CloudSyncFailureKind.Permanent)
                {
                    await RecordFailureAsync(entry.MutationId, exception, null, cancellationToken);
                    return new PushExecution(PermanentFailure: exception);
                }

                var nextRetryAtUtc = await RecordTransientFailureAsync(
                    entry,
                    exception,
                    cancellationToken);
                if (operationAttempts >= retryPolicy.MaximumAttemptsPerOperation)
                {
                    return new PushExecution(
                        TransientFailure: exception,
                        NextRetryAtUtc: nextRetryAtUtc);
                }

                await syncDelay.DelayAsync(
                    nextRetryAtUtc - timeProvider.GetUtcNow(),
                    cancellationToken);
            }
        }

        throw new InvalidOperationException("云同步重试循环意外结束。");
    }

    private async Task<PullExecution> PullWithRecoveryAsync(
        Guid deviceId,
        long cursor,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        var refreshedSession = false;
        while (attempts < retryPolicy.MaximumAttemptsPerOperation)
        {
            attempts++;
            try
            {
                return new PullExecution(await transport.PullAsync(
                    deviceId,
                    cursor,
                    BatchSize,
                    cancellationToken));
            }
            catch (CloudSyncTransportException exception)
            {
                if (exception.FailureKind == CloudSyncFailureKind.AuthenticationRequired)
                {
                    if (refreshedSession)
                    {
                        return new PullExecution(AuthenticationRequired: true);
                    }

                    refreshedSession = true;
                    try
                    {
                        if (await accountService.RefreshSessionAsync(cancellationToken) is null)
                        {
                            return new PullExecution(AuthenticationRequired: true);
                        }
                    }
                    catch (AccountServiceException)
                    {
                        return new PullExecution(AuthenticationRequired: true);
                    }

                    attempts = retryPolicy.MaximumAttemptsPerOperation - 1;
                    continue;
                }

                if (exception.FailureKind == CloudSyncFailureKind.Permanent)
                {
                    return new PullExecution(Failure: exception);
                }

                var delay = retryPolicy.GetDelay(attempts);
                var nextRetryAtUtc = timeProvider.GetUtcNow() + delay;
                if (attempts >= retryPolicy.MaximumAttemptsPerOperation)
                {
                    return new PullExecution(Failure: exception, NextRetryAtUtc: nextRetryAtUtc);
                }
                await syncDelay.DelayAsync(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("云同步拉取重试循环意外结束。");
    }

    private async Task<DateTimeOffset> RecordTransientFailureAsync(
        SyncOutboxEntry entry,
        CloudSyncTransportException exception,
        CancellationToken cancellationToken)
    {
        var current = await outboxRepository.GetByIdAsync(entry.MutationId, cancellationToken);
        var delay = retryPolicy.GetDelay(checked((current?.AttemptCount ?? entry.AttemptCount) + 1));
        var attemptedAtUtc = timeProvider.GetUtcNow();
        var nextRetryAtUtc = attemptedAtUtc + delay;
        _ = await outboxRepository.RecordAttemptAsync(
            entry.MutationId,
            attemptedAtUtc,
            SyncOutboxErrorCategory.Transient,
            exception.ErrorCode,
            exception.Message,
            nextRetryAtUtc,
            cancellationToken);
        return nextRetryAtUtc;
    }

    private Task<SyncOutboxEntry> RecordFailureAsync(
        Guid mutationId,
        CloudSyncTransportException exception,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken) =>
        outboxRepository.RecordAttemptAsync(
            mutationId,
            timeProvider.GetUtcNow(),
            exception.FailureKind == CloudSyncFailureKind.AuthenticationRequired
                ? SyncOutboxErrorCategory.Authentication
                : SyncOutboxErrorCategory.Permanent,
            exception.ErrorCode,
            exception.Message,
            nextAttemptAtUtc,
            cancellationToken);

    private static CloudSyncConflict ToConflict(SyncOutboxEntry entry) => new(
        entry.MutationId,
        entry.EntityId,
        entry.ConflictCode!,
        entry.ConflictServerRevision,
        entry.BaseRevision,
        entry.Payload,
        entry.ConflictRemoteEvent);

    private static bool IsMissingEntityDelete(SyncOutboxEntry entry) =>
        entry.Operation == SyncOperationType.Delete &&
        string.Equals(entry.ConflictCode, "entity_not_found", StringComparison.Ordinal);

    private static CloudSyncResult DeviceFailureResult(
        CloudSyncTransportException exception,
        int pushedCount = 0,
        int pulledCount = 0,
        long cursor = 0)
    {
        if (exception.FailureKind == CloudSyncFailureKind.AuthenticationRequired)
        {
            return Result(
                CloudSyncOutcome.NotAuthenticated,
                CloudSyncStatus.AuthenticationRequired,
                pushedCount,
                pulledCount,
                cursor,
                message: "云账户会话已失效；本地修改保持安全，请重新登录。");
        }

        var status = exception.ErrorCode == "network_unavailable"
            ? CloudSyncStatus.Offline
            : exception.FailureKind == CloudSyncFailureKind.Transient
                ? CloudSyncStatus.RetryScheduled
                : CloudSyncStatus.Error;
        var message = exception.ErrorCode is "device_revoked" or "device_not_found" or "device_id_unavailable"
            ? "当前设备已被移除或不可用，云同步已停止；本地日历和待上传修改保持不变。"
            : "云设备状态检查失败；本地日历和待上传修改保持不变，可稍后重试。";
        return Result(
            CloudSyncOutcome.Failed,
            status,
            pushedCount,
            pulledCount,
            cursor,
            message: message);
    }

    private static CloudSyncResult Result(
        CloudSyncOutcome outcome,
        CloudSyncStatus status,
        int pushedCount = 0,
        int pulledCount = 0,
        long cursor = 0,
        IReadOnlyList<CloudSyncConflict>? conflicts = null,
        DateTimeOffset? nextRetryAtUtc = null,
        string? message = null) =>
        new(
            outcome,
            status,
            pushedCount,
            pulledCount,
            cursor,
            conflicts,
            nextRetryAtUtc,
            message);

    private static void ValidateBatch(CloudChangeBatch batch, long previousCursor)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Cursor < previousCursor)
        {
            throw new CloudSyncTransportException(
                "云端同步游标发生倒退。",
                CloudSyncFailureKind.Permanent,
                "cursor_regression");
        }

        long lastRevision = previousCursor;
        foreach (var change in batch.Changes)
        {
            if (change.ServerRevision <= lastRevision || change.ServerRevision > batch.Cursor)
            {
                throw new CloudSyncTransportException(
                    "云端变更修订号无序或超出游标范围。",
                    CloudSyncFailureKind.Permanent,
                    "invalid_revision_order");
            }
            lastRevision = change.ServerRevision;
        }
    }

    private sealed record PushExecution(
        CloudMutationResponse? Response = null,
        CloudSyncTransportException? TransientFailure = null,
        CloudSyncTransportException? PermanentFailure = null,
        bool AuthenticationRequired = false,
        DateTimeOffset? NextRetryAtUtc = null);

    private sealed record PullExecution(
        CloudChangeBatch? Batch = null,
        CloudSyncTransportException? Failure = null,
        bool AuthenticationRequired = false,
        DateTimeOffset? NextRetryAtUtc = null);
}
