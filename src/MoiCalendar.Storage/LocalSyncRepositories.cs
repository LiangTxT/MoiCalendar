using System.Text.Json;
using Microsoft.JSInterop;
using MoiCalendar.Core;

namespace MoiCalendar.Storage;

public sealed class IndexedDbSyncOutboxRepository(IndexedDbConnection connection)
    : ISyncOutboxRepository, ICloudConflictRepository
{
    public Task<SyncOutboxEntry?> GetByIdAsync(
        Guid mutationId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOutboxEntry?>(
            "读取同步发件箱记录",
            "getSyncOutboxEntryById",
            cancellationToken,
            mutationId);

    public async Task<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        return await InvokeAsync<SyncOutboxEntry[]>(
            "读取同步发件箱",
            "getPendingSyncOutboxEntries",
            cancellationToken,
            maximumCount);
    }

    public async Task<IReadOnlyList<SyncOutboxEntry>> GetReadyAsync(
        DateTimeOffset readyAtUtc,
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        return await InvokeAsync<SyncOutboxEntry[]>(
            "读取可重试同步发件箱",
            "getReadySyncOutboxEntries",
            cancellationToken,
            readyAtUtc.ToUniversalTime(),
            maximumCount);
    }

    public Task<SyncOutboxEntry> RecordAttemptAsync(
        Guid mutationId,
        DateTimeOffset attemptedAtUtc,
        SyncOutboxErrorCategory errorCategory,
        string errorCode,
        string safeErrorMessage,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOutboxEntry>(
            "记录同步发件箱尝试",
            "recordSyncOutboxAttempt",
            cancellationToken,
            mutationId,
            attemptedAtUtc.ToUniversalTime(),
            errorCategory,
            errorCode,
            SyncLogSanitizer.Sanitize(safeErrorMessage),
            nextAttemptAtUtc?.ToUniversalTime());

    public Task<SyncOutboxEntry> RecordConflictAsync(
        Guid mutationId,
        DateTimeOffset detectedAtUtc,
        string conflictCode,
        long? currentServerRevision,
        CalendarEvent? remoteEvent,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOutboxEntry>(
            "记录同步冲突",
            "recordSyncOutboxConflict",
            cancellationToken,
            mutationId,
            detectedAtUtc.ToUniversalTime(),
            conflictCode,
            currentServerRevision,
            remoteEvent);

    public async Task RemoveAsync(
        Guid mutationId,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<object?>(
            "删除同步发件箱记录",
            "removeSyncOutboxEntry",
            cancellationToken,
            mutationId);

    public async Task AcknowledgeAsync(
        Guid mutationId,
        long serverRevision,
        CancellationToken cancellationToken = default)
    {
        if (serverRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(serverRevision));
        }

        _ = await InvokeAsync<object?>(
            "确认同步发件箱记录",
            "acknowledgeSyncOutboxEntry",
            cancellationToken,
            mutationId,
            serverRevision);
    }

    public async Task<IReadOnlyList<SyncOutboxEntry>> GetConflictsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        return await InvokeAsync<SyncOutboxEntry[]>(
            "读取同步冲突",
            "getSyncOutboxConflicts",
            cancellationToken,
            maximumCount);
    }

    public Task<SyncOutboxEntry> KeepLocalAsync(
        Guid conflictMutationId,
        Guid replacementMutationId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncOutboxEntry>(
            "保留本地冲突版本",
            "resolveSyncConflictKeepLocal",
            cancellationToken,
            conflictMutationId,
            replacementMutationId,
            createdAtUtc.ToUniversalTime());

    public Task<CalendarEvent> KeepCloudAsync(
        Guid conflictMutationId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CalendarEvent>(
            "保留云端冲突版本",
            "resolveSyncConflictKeepCloud",
            cancellationToken,
            conflictMutationId);

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            return await connection.InvokeAsync<T>(identifier, cancellationToken, arguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}

public sealed class IndexedDbSyncStateRepository(IndexedDbConnection connection)
    : ISyncStateRepository
{
    public Task<SyncState?> GetAsync(
        string scope = SyncState.DefaultScope,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncState?>("读取云同步状态", "getCloudSyncState", cancellationToken, scope);

    public async Task SaveAsync(SyncState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        _ = await InvokeAsync<object?>("保存云同步状态", "saveCloudSyncState", cancellationToken, state);
    }

    private async Task<T> InvokeAsync<T>(
        string operation,
        string identifier,
        CancellationToken cancellationToken,
        params object?[] arguments)
    {
        try
        {
            return await connection.InvokeAsync<T>(identifier, cancellationToken, arguments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException($"{operation}失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}

public sealed class IndexedDbCloudSyncBindingRepository(IndexedDbConnection connection)
    : ICloudSyncBindingRepository
{
    public async Task<CloudSyncBinding?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await connection.InvokeAsync<CloudSyncBinding?>(
                "getCloudSyncBinding",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException("读取云账户绑定失败：浏览器本地数据库操作未完成。", exception);
        }
    }

    public async Task<CloudSyncBinding> BindAsync(
        string accountId,
        DateTimeOffset boundAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("云账户 ID 不能为空。", nameof(accountId));
        }

        try
        {
            return await connection.InvokeAsync<CloudSyncBinding>(
                "bindCloudSyncAccount",
                cancellationToken,
                accountId,
                boundAtUtc.ToUniversalTime());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException("绑定云账户失败：浏览器本地数据库操作未完成。", exception);
        }
    }
}

public sealed class IndexedDbCloudChangeApplyRepository(IndexedDbConnection connection)
    : ICloudChangeApplyRepository
{
    public async Task ApplyAndAdvanceCursorAsync(
        string scope,
        IReadOnlyList<CloudRemoteCalendarChange> changes,
        long cursor,
        DateTimeOffset synchronizedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (cursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        try
        {
            await connection.InvokeAsync<object?>(
                "applyCloudChangesAndAdvanceCursor",
                cancellationToken,
                scope,
                changes,
                cursor,
                synchronizedAtUtc.ToUniversalTime());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or JSException)
        {
            throw new SyncOperationException("应用云端变更失败：本地事务未完成。", exception);
        }
    }
}

public sealed class InMemorySyncOutboxRepository(IEventRepository? eventRepository = null)
    : ISyncOutboxRepository, ICloudConflictRepository
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, SyncOutboxEntry> entries = [];
    private readonly Dictionary<(SyncEntityType EntityType, Guid EntityId), long> entityRevisions = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task AddAsync(
        SyncOutboxEntry entry,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!entries.TryAdd(entry.MutationId, entry))
            {
                throw new InvalidOperationException("相同 ID 的同步发件箱记录已经存在。");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task AddLocalOperationAsync(
        SyncOperation operation,
        CancellationToken cancellationToken = default)
    {
        var entry = SyncOutboxEntry.FromLocalCalendarOperation(operation);
        if (entityRevisions.TryGetValue((entry.EntityType, entry.EntityId), out var revision))
        {
            entry = entry with { BaseRevision = revision };
        }
        return AddAsync(entry, cancellationToken);
    }

    public async Task<SyncOutboxEntry?> GetByIdAsync(
        Guid mutationId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return entries.GetValueOrDefault(mutationId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncOutboxEntry>> GetPendingAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            return entries.Values
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.MutationId)
                .Take(maximumCount)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncOutboxEntry>> GetReadyAsync(
        DateTimeOffset readyAtUtc,
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var blockedEntities = entries.Values
                .Where(entry =>
                    entry.ConflictCode is not null &&
                    !(entry.Operation == SyncOperationType.Delete &&
                      string.Equals(entry.ConflictCode, "entity_not_found", StringComparison.Ordinal)))
                .Select(entry => (entry.EntityType, entry.EntityId))
                .ToHashSet();
            return entries.Values
                .Where(entry =>
                    !blockedEntities.Contains((entry.EntityType, entry.EntityId)) &&
                    entry.LastErrorCategory != SyncOutboxErrorCategory.Permanent &&
                    (entry.NextAttemptAtUtc is null || entry.NextAttemptAtUtc <= readyAtUtc))
                .OrderBy(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.MutationId)
                .Take(maximumCount)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SyncOutboxEntry> RecordAttemptAsync(
        Guid mutationId,
        DateTimeOffset attemptedAtUtc,
        SyncOutboxErrorCategory errorCategory,
        string errorCode,
        string safeErrorMessage,
        DateTimeOffset? nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = entries.GetValueOrDefault(mutationId)
                ?? throw new KeyNotFoundException("找不到同步发件箱记录。");
            var updated = existing with
            {
                AttemptCount = checked(existing.AttemptCount + 1),
                LastAttemptAtUtc = attemptedAtUtc.ToUniversalTime(),
                LastError = SyncLogSanitizer.Sanitize(safeErrorMessage),
                LastErrorCategory = errorCategory,
                LastErrorCode = errorCode,
                NextAttemptAtUtc = nextAttemptAtUtc?.ToUniversalTime()
            };
            entries[mutationId] = updated;
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SyncOutboxEntry> RecordConflictAsync(
        Guid mutationId,
        DateTimeOffset detectedAtUtc,
        string conflictCode,
        long? currentServerRevision,
        CalendarEvent? remoteEvent,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var existing = entries.GetValueOrDefault(mutationId)
                ?? throw new KeyNotFoundException("找不到同步发件箱记录。");
            var updated = existing with
            {
                AttemptCount = checked(existing.AttemptCount + 1),
                LastAttemptAtUtc = detectedAtUtc.ToUniversalTime(),
                LastError = $"conflict:{conflictCode}",
                LastErrorCategory = SyncOutboxErrorCategory.Conflict,
                LastErrorCode = conflictCode,
                NextAttemptAtUtc = null,
                ConflictCode = conflictCode,
                ConflictServerRevision = currentServerRevision,
                ConflictRemoteEvent = remoteEvent,
                ConflictDetectedAtUtc = detectedAtUtc.ToUniversalTime()
            };
            entries[mutationId] = updated;
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RemoveAsync(Guid mutationId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            entries.Remove(mutationId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AcknowledgeAsync(
        Guid mutationId,
        long serverRevision,
        CancellationToken cancellationToken = default)
    {
        if (serverRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(serverRevision));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!entries.Remove(mutationId, out var acknowledged))
            {
                return;
            }

            entityRevisions[(acknowledged.EntityType, acknowledged.EntityId)] = serverRevision;

            foreach (var (id, entry) in entries.ToArray())
            {
                if (entry.EntityType == acknowledged.EntityType &&
                    entry.EntityId == acknowledged.EntityId &&
                    entry.BaseRevision == acknowledged.BaseRevision)
                {
                    entries[id] = entry with { BaseRevision = serverRevision };
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncOutboxEntry>> GetConflictsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            return entries.Values
                .Where(entry => entry.ConflictCode is not null &&
                                !(entry.Operation == SyncOperationType.Delete &&
                                  string.Equals(
                                      entry.ConflictCode,
                                      "entity_not_found",
                                      StringComparison.Ordinal)))
                .OrderBy(entry => entry.ConflictDetectedAtUtc)
                .ThenBy(entry => entry.MutationId)
                .Take(maximumCount)
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SyncOutboxEntry> KeepLocalAsync(
        Guid conflictMutationId,
        Guid replacementMutationId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        var events = eventRepository ?? throw new InvalidOperationException(
            "冲突解决需要事件仓储。");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var conflict = entries.GetValueOrDefault(conflictMutationId)
                ?? throw new KeyNotFoundException("找不到同步冲突。");
            if (conflict.ConflictCode is not ("stale_revision" or "entity_exists") ||
                conflict.ConflictServerRevision is not > 0 ||
                conflict.ConflictRemoteEvent is not { DeletedAtUtc: null } remoteEvent ||
                remoteEvent.Id != conflict.EntityId)
            {
                throw new SyncOperationException("该冲突不能安全地保留本地版本。");
            }
            if (replacementMutationId == conflictMutationId ||
                entries.ContainsKey(replacementMutationId))
            {
                throw new SyncOperationException("冲突解决必须使用新的变更标识。");
            }
            var localEvent = await events.GetByIdIncludingDeletedAsync(
                conflict.EntityId,
                cancellationToken)
                ?? throw new SyncOperationException("找不到冲突的本地事件版本。");
            var operation = localEvent.DeletedAtUtc is null
                ? SyncOperationType.Update
                : SyncOperationType.Delete;
            foreach (var entry in entries.Values
                         .Where(entry => entry.EntityType == conflict.EntityType &&
                                         entry.EntityId == conflict.EntityId)
                         .ToArray())
            {
                entries.Remove(entry.MutationId);
            }

            var replacement = conflict with
            {
                MutationId = replacementMutationId,
                Operation = operation,
                BaseRevision = conflict.ConflictServerRevision,
                Payload = JsonSerializer.Serialize(localEvent, PayloadSerializerOptions),
                CreatedAtUtc = createdAtUtc.ToUniversalTime(),
                AttemptCount = 0,
                LastAttemptAtUtc = null,
                LastError = null,
                LastErrorCategory = null,
                LastErrorCode = null,
                NextAttemptAtUtc = null,
                ConflictCode = null,
                ConflictServerRevision = null,
                ConflictRemoteEvent = null,
                ConflictDetectedAtUtc = null
            };
            entries.Add(replacement.MutationId, replacement);
            return replacement;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CalendarEvent> KeepCloudAsync(
        Guid conflictMutationId,
        CancellationToken cancellationToken = default)
    {
        var events = eventRepository ?? throw new InvalidOperationException(
            "冲突解决需要事件仓储。");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var conflict = entries.GetValueOrDefault(conflictMutationId)
                ?? throw new KeyNotFoundException("找不到同步冲突。");
            var remoteEvent = conflict.ConflictRemoteEvent
                ?? throw new SyncOperationException("该冲突没有可安全应用的云端版本。");
            if (conflict.ConflictServerRevision is not > 0)
            {
                throw new SyncOperationException("该冲突缺少有效的云端修订号。");
            }
            if (remoteEvent.Id != conflict.EntityId)
            {
                throw new SyncOperationException("云端冲突版本与本地实体不匹配。");
            }

            await events.UpsertAsync(remoteEvent, cancellationToken);
            foreach (var entry in entries.Values
                         .Where(entry => entry.EntityType == conflict.EntityType &&
                                         entry.EntityId == conflict.EntityId)
                         .ToArray())
            {
                entries.Remove(entry.MutationId);
            }
            entityRevisions[(conflict.EntityType, conflict.EntityId)] =
                conflict.ConflictServerRevision.Value;
            return remoteEvent;
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class InMemorySyncStateRepository : ISyncStateRepository
{
    private readonly Dictionary<string, SyncState> states = new(StringComparer.Ordinal);

    public Task<SyncState?> GetAsync(
        string scope = SyncState.DefaultScope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(states.GetValueOrDefault(scope));

    public Task SaveAsync(SyncState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        states[state.Scope] = state;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryCloudSyncBindingRepository : ICloudSyncBindingRepository
{
    private CloudSyncBinding? binding;

    public Task<CloudSyncBinding?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(binding);

    public Task<CloudSyncBinding> BindAsync(
        string accountId,
        DateTimeOffset boundAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (binding is not null && !string.Equals(binding.AccountId, accountId, StringComparison.Ordinal))
        {
            throw new SyncOperationException("本地日历已绑定到另一个云账户。");
        }

        binding ??= new CloudSyncBinding(accountId, boundAtUtc.ToUniversalTime());
        return Task.FromResult(binding);
    }
}

public sealed class InMemoryCloudChangeApplyRepository(
    IEventRepository eventRepository,
    ISyncStateRepository syncStateRepository,
    ISyncOutboxRepository? outboxRepository = null) : ICloudChangeApplyRepository
{
    public async Task ApplyAndAdvanceCursorAsync(
        string scope,
        IReadOnlyList<CloudRemoteCalendarChange> changes,
        long cursor,
        DateTimeOffset synchronizedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var pendingEntityIds = outboxRepository is null
            ? []
            : (await outboxRepository.GetPendingAsync(1_000, cancellationToken))
                .Select(entry => entry.EntityId)
                .ToHashSet();
        foreach (var change in changes)
        {
            if (!pendingEntityIds.Contains(change.CalendarEvent.Id))
            {
                await eventRepository.UpsertAsync(change.CalendarEvent, cancellationToken);
            }
        }

        await syncStateRepository.SaveAsync(new SyncState
        {
            Scope = scope,
            LastSuccessfulServerRevision = cursor,
            LastSuccessfulSyncAtUtc = synchronizedAtUtc.ToUniversalTime()
        }, cancellationToken);
    }
}
