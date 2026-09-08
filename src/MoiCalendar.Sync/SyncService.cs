using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using System.Text;
using MoiCalendar.Core;

namespace MoiCalendar.Sync;

public sealed partial class SyncService : ISyncService, ISyncDiagnosticsService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private const string OperationIdDataKey = "SyncOperationId";
    private const string StageDataKey = "SyncStage";
    private readonly SemaphoreSlim syncGate = new(1, 1);
    private readonly IOperationRepository operationRepository;
    private readonly IEventRepository eventRepository;
    private readonly ISyncStorageProvider storageProvider;
    private readonly ISyncProviderSelection? providerSelection;
    private readonly ISyncLogRepository logRepository;
    private readonly ISyncStatusRepository statusRepository;
    private readonly TimeProvider timeProvider;
    private readonly ILocalDataOperationLock operationLock;
    private readonly IRestoreSyncGuard? restoreSyncGuard;
    private readonly IRemoteSyncApplyRepository? remoteSyncApplyRepository;
    private volatile bool isSyncing;

    public SyncService(
        IOperationRepository operationRepository,
        IEventRepository eventRepository,
        ISyncStorageProvider storageProvider,
        ISyncProviderSelection? providerSelection = null,
        ISyncLogRepository? logRepository = null,
        ISyncStatusRepository? statusRepository = null,
        TimeProvider? timeProvider = null,
        ILocalDataOperationLock? operationLock = null,
        IRestoreSyncGuard? restoreSyncGuard = null,
        IRemoteSyncApplyRepository? remoteSyncApplyRepository = null)
    {
        this.operationRepository = operationRepository;
        this.eventRepository = eventRepository;
        this.storageProvider = storageProvider;
        this.providerSelection = providerSelection;
        this.logRepository = logRepository ?? new TransientSyncLogRepository();
        this.statusRepository = statusRepository ?? new TransientSyncStatusRepository();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.operationLock = operationLock ?? NoOpLocalDataOperationLock.Instance;
        this.restoreSyncGuard = restoreSyncGuard;
        this.remoteSyncApplyRepository = remoteSyncApplyRepository;
    }

    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
        await EnsureSyncAllowedAsync(cancellationToken);
        return await PushCoreAsync(includeFailed: false, cancellationToken);
    }

    private async Task<SyncResult> PushCoreAsync(
        bool includeFailed,
        CancellationToken cancellationToken)
    {
        await storageProvider.EnsureDirectoryAsync(
            RemoteSyncFormat.OperationsDirectory,
            cancellationToken);
        var pending = await operationRepository.GetByStatusAsync(
            SyncOperationStatus.Pending,
            cancellationToken);
        var operations = pending;
        if (includeFailed)
        {
            var failed = await operationRepository.GetByStatusAsync(
                SyncOperationStatus.Failed,
                cancellationToken);
            operations = pending.Concat(failed)
                .OrderBy(operation => operation.TimestampUtc)
                .ThenBy(operation => operation.OperationId)
                .ToArray();
        }
        var pushedCount = 0;
        Exception? firstFailure = null;

        foreach (var operation in operations)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = RemoteSyncFormat.GetOperationPath(operation.OperationId);
                var content = Serialize(operation);
                if (Encoding.UTF8.GetByteCount(content) > RemoteSyncFormat.MaximumOperationFileBytes)
                {
                    throw new SyncStorageException(
                        $"本地同步操作 {operation.OperationId:D} 超过 {RemoteSyncFormat.MaximumOperationFileBytes} 字节限制，未上传。");
                }
                var existing = await storageProvider.DownloadTextAsync(path, cancellationToken);

                if (existing is null)
                {
                    await storageProvider.UploadTextAsync(
                        path,
                        content,
                        cancellationToken: cancellationToken);
                }
                else if (!RemoteContentsMatch(existing.Content, content))
                {
                    throw new SyncStorageException($"远端操作文件 {operation.OperationId:D} 与本地内容不一致，已停止覆盖。");
                }

                await operationRepository.UpdateStatusAsync(
                    operation.OperationId,
                    SyncOperationStatus.Uploaded,
                    cancellationToken);
                pushedCount++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                try
                {
                    await operationRepository.UpdateStatusAsync(
                        operation.OperationId,
                        SyncOperationStatus.Failed,
                        cancellationToken);
                }
                catch (Exception statusException) when (statusException is not OperationCanceledException)
                {
                    exception.Data["StatusUpdateError"] = statusException.Message;
                }

                exception.Data[OperationIdDataKey] = operation.OperationId;
                exception.Data[StageDataKey] = SyncLogStage.Push;
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }

        return new SyncResult(pushedCount, 0, 0);
    }

    public async Task<SyncResult> PullAsync(CancellationToken cancellationToken = default)
    {
        await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
        await EnsureSyncAllowedAsync(cancellationToken);
        return await PullCoreAsync(cancellationToken);
    }

    private async Task<SyncResult> PullCoreAsync(CancellationToken cancellationToken)
    {
        await storageProvider.EnsureDirectoryAsync(
            RemoteSyncFormat.OperationsDirectory,
            cancellationToken);
        var files = await storageProvider.ListFilesAsync(
            RemoteSyncFormat.OperationsDirectory,
            cancellationToken);
        var downloadedCount = 0;
        var appliedCount = 0;
        var provider = await GetProviderNameAsync(cancellationToken);

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (!TryGetOperationId(file.Path, out var operationId))
            {
                continue;
            }

            try
            {
                if (file.Size > RemoteSyncFormat.MaximumOperationFileBytes)
                {
                    await TryAddLogAsync(
                        SyncLogSeverity.Warning,
                        SyncLogStage.Pull,
                        provider,
                        $"远端同步操作文件超过 {RemoteSyncFormat.MaximumOperationFileBytes} 字节限制，已跳过。",
                        operationId,
                        "REMOTE_FILE_TOO_LARGE",
                        cancellationToken);
                    continue;
                }

                var localOperation = await operationRepository.GetByIdAsync(operationId, cancellationToken);
                if (localOperation?.Status == SyncOperationStatus.Applied)
                {
                    continue;
                }

                SyncTextFile? remoteFile;
                try
                {
                    remoteFile = await storageProvider.DownloadTextAsync(file.Path, cancellationToken);
                }
                catch (SyncContentTooLargeException exception)
                {
                    await TryAddLogAsync(
                        SyncLogSeverity.Warning,
                        SyncLogStage.Pull,
                        provider,
                        CreateSafeErrorSummary(exception),
                        operationId,
                        "REMOTE_FILE_TOO_LARGE",
                        cancellationToken);
                    continue;
                }
                if (remoteFile is null)
                {
                    continue;
                }

                RemoteSyncOperationDocument document;
                CalendarEvent remoteEvent;
                try
                {
                    document = Deserialize(remoteFile.Content);
                    ValidateDocument(document, operationId);
                    if (localOperation is not null &&
                        !RemoteContentsMatch(remoteFile.Content, Serialize(localOperation)))
                    {
                        throw new SyncStorageException(
                            $"远端操作文件 {operationId:D} 与同 ID 的本地操作内容不一致，已跳过该文件。");
                    }

                    remoteEvent = DeserializeEvent(document);
                }
                catch (SyncStorageException exception)
                {
                    await TryAddLogAsync(
                        SyncLogSeverity.Warning,
                        SyncLogStage.Pull,
                        provider,
                        CreateSafeErrorSummary(exception),
                        operationId,
                        GetErrorCode(exception),
                        cancellationToken);
                    continue;
                }

                downloadedCount++;

                var localEvent = await eventRepository.GetByIdIncludingDeletedAsync(
                    document.EntityId,
                    cancellationToken);
                var appliedOperation = ToLocalOperation(document, SyncOperationStatus.Applied);
                var eventToApply = localEvent is null || ShouldApplyRemoteEvent(remoteEvent, localEvent)
                    ? remoteEvent
                    : null;
                if (remoteSyncApplyRepository is not null)
                {
                    await remoteSyncApplyRepository.ApplyAsync(
                        eventToApply,
                        appliedOperation,
                        localOperation is not null,
                        cancellationToken);
                }
                else
                {
                    if (eventToApply is not null)
                    {
                        await eventRepository.UpsertAsync(eventToApply, cancellationToken);
                    }

                    if (localOperation is null)
                    {
                        await operationRepository.AddAsync(appliedOperation, cancellationToken);
                    }
                    else
                    {
                        await operationRepository.UpdateStatusAsync(
                            operationId,
                            SyncOperationStatus.Applied,
                            cancellationToken);
                    }
                }

                if (eventToApply is not null)
                {
                    appliedCount++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                exception.Data[OperationIdDataKey] = operationId;
                exception.Data[StageDataKey] = SyncLogStage.Pull;
                throw;
            }
        }

        return new SyncResult(0, downloadedCount, appliedCount);
    }

    public Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        ExecuteSynchronizationAsync(SyncLogStage.Synchronize, retryFailed: false, cancellationToken);

    public Task<SyncResult> RetryFailedAsync(CancellationToken cancellationToken = default) =>
        ExecuteSynchronizationAsync(SyncLogStage.Retry, retryFailed: true, cancellationToken);

    public async Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = await statusRepository.GetAsync(cancellationToken);
        var pending = await operationRepository.GetByStatusAsync(
            SyncOperationStatus.Pending,
            cancellationToken);
        var failed = await operationRepository.GetByStatusAsync(
            SyncOperationStatus.Failed,
            cancellationToken);
        return new SyncStatus
        {
            ActiveProvider = await GetProviderNameAsync(cancellationToken),
            IsSyncing = isSyncing,
            LastSyncStartedAtUtc = state.LastSyncStartedAtUtc,
            LastSuccessfulSyncAtUtc = state.LastSuccessfulSyncAtUtc,
            LastFailedSyncAtUtc = state.LastFailedSyncAtUtc,
            PendingOperationCount = pending.Count,
            FailedOperationCount = failed.Count,
            LastErrorSummary = state.LastErrorSummary
        };
    }

    public Task<IReadOnlyList<SyncLogEntry>> GetLogEntriesAsync(
        CancellationToken cancellationToken = default) =>
        logRepository.GetRecentAsync(cancellationToken);

    public Task ClearLogAsync(CancellationToken cancellationToken = default) =>
        logRepository.ClearAsync(cancellationToken);

    private async Task<SyncResult> ExecuteSynchronizationAsync(
        SyncLogStage requestedStage,
        bool retryFailed,
        CancellationToken cancellationToken)
    {
        await syncGate.WaitAsync(cancellationToken);
        isSyncing = true;
        var provider = "Unknown";
        var startedAt = timeProvider.GetUtcNow();
        var previousState = new SyncStatusState();

        try
        {
            await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
            await EnsureSyncAllowedAsync(cancellationToken);
            provider = await GetProviderNameAsync(cancellationToken);
            previousState = await statusRepository.GetAsync(cancellationToken);
            await TrySaveStatusAsync(previousState with
            {
                LastSyncStartedAtUtc = startedAt
            }, cancellationToken);

            var pushed = await PushCoreAsync(retryFailed, cancellationToken);
            var pulled = await PullCoreAsync(cancellationToken);
            var completedAt = timeProvider.GetUtcNow();
            await TrySaveStatusAsync(previousState with
            {
                LastSyncStartedAtUtc = startedAt,
                LastSuccessfulSyncAtUtc = completedAt,
                LastErrorSummary = null
            }, cancellationToken);
            await TryAddLogAsync(
                SyncLogSeverity.Information,
                requestedStage,
                provider,
                $"同步完成：上传 {pushed.PushedCount} 项，下载 {pulled.DownloadedCount} 项，应用 {pulled.AppliedCount} 项。",
                null,
                null,
                cancellationToken);
            return new SyncResult(
                pushed.PushedCount,
                pulled.DownloadedCount,
                pulled.AppliedCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failedAt = timeProvider.GetUtcNow();
            var summary = CreateSafeErrorSummary(exception);
            await TrySaveStatusAsync(previousState with
            {
                LastSyncStartedAtUtc = startedAt,
                LastFailedSyncAtUtc = failedAt,
                LastErrorSummary = summary
            }, cancellationToken);
            await TryAddLogAsync(
                SyncLogSeverity.Error,
                GetFailureStage(exception, requestedStage),
                provider,
                summary,
                GetOperationId(exception),
                GetErrorCode(exception),
                cancellationToken);
            throw;
        }
        finally
        {
            isSyncing = false;
            syncGate.Release();
        }
    }

    private async Task EnsureSyncAllowedAsync(CancellationToken cancellationToken)
    {
        if (restoreSyncGuard is not null &&
            await restoreSyncGuard.IsSyncBlockedAsync(cancellationToken))
        {
            throw new RestoreSyncBlockedException();
        }
    }

    private async Task<string> GetProviderNameAsync(CancellationToken cancellationToken)
    {
        if (providerSelection is null)
        {
            return "Unknown";
        }

        var configuration = await providerSelection.GetAsync(cancellationToken);
        return configuration.ProviderType.ToString();
    }

    private async Task TryAddLogAsync(
        SyncLogSeverity severity,
        SyncLogStage stage,
        string provider,
        string message,
        Guid? operationId,
        string? errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await logRepository.AddAsync(new SyncLogEntry
            {
                Id = Guid.NewGuid(),
                TimestampUtc = timeProvider.GetUtcNow(),
                Severity = severity,
                Stage = stage,
                Provider = provider,
                OperationId = operationId,
                Message = message,
                ErrorCode = errorCode
            }, cancellationToken);
        }
        catch (SyncOperationException)
        {
            // Diagnostic persistence must not change the result of calendar synchronization.
        }
    }

    private async Task TrySaveStatusAsync(
        SyncStatusState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await statusRepository.SaveAsync(state, cancellationToken);
        }
        catch (SyncOperationException)
        {
            // Diagnostic persistence must not change the result of calendar synchronization.
        }
    }

    private static Guid? GetOperationId(Exception exception) =>
        exception.Data[OperationIdDataKey] is Guid operationId ? operationId : null;

    private static SyncLogStage GetFailureStage(Exception exception, SyncLogStage fallback) =>
        exception.Data[StageDataKey] is SyncLogStage stage ? stage : fallback;

    private static string? GetErrorCode(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } statusCode })
        {
            return $"HTTP_{(int)statusCode}";
        }

        var match = HttpStatusRegex().Match(exception.Message);
        return match.Success ? $"HTTP_{match.Groups[1].Value}" : null;
    }

    private static string CreateSafeErrorSummary(Exception exception)
    {
        var httpSummary = HttpSummaryRegex().Match(exception.Message);
        return SyncLogSanitizer.Sanitize(
            httpSummary.Success ? $"{httpSummary.Groups[1].Value}。" : exception.Message);
    }

    [GeneratedRegex(@"HTTP\s+(\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex HttpStatusRegex();

    [GeneratedRegex(@"^(.*?（HTTP\s+\d{3}）)", RegexOptions.IgnoreCase)]
    private static partial Regex HttpSummaryRegex();

    private sealed class TransientSyncLogRepository : ISyncLogRepository
    {
        private readonly List<SyncLogEntry> entries = [];

        public Task AddAsync(SyncLogEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Insert(0, entry);
            if (entries.Count > 200)
            {
                entries.RemoveAt(entries.Count - 1);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SyncLogEntry>> GetRecentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SyncLogEntry>>(entries.ToArray());
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class TransientSyncStatusRepository : ISyncStatusRepository
    {
        private SyncStatusState state = new();

        public Task<SyncStatusState> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        public Task SaveAsync(SyncStatusState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.state = state;
            return Task.CompletedTask;
        }
    }

    private static string Serialize(SyncOperation operation)
    {
        using var payload = JsonDocument.Parse(operation.Payload);
        var document = new RemoteSyncOperationDocument
        {
            FormatVersion = RemoteSyncFormat.CurrentVersion,
            OperationId = operation.OperationId,
            DeviceId = operation.DeviceId,
            EntityId = operation.EntityId,
            OperationType = operation.OperationType,
            TimestampUtc = operation.TimestampUtc.ToUniversalTime(),
            Payload = payload.RootElement.Clone()
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static RemoteSyncOperationDocument Deserialize(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<RemoteSyncOperationDocument>(content, JsonOptions)
                ?? throw new SyncStorageException("远端同步操作文件为空。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SyncStorageException("远端同步操作 JSON 格式无效。", exception);
        }
    }

    private static CalendarEvent DeserializeEvent(RemoteSyncOperationDocument document)
    {
        CalendarEvent calendarEvent;
        try
        {
            calendarEvent = document.Payload.Deserialize<CalendarEvent>(JsonOptions)
                ?? throw new SyncStorageException("远端同步操作缺少事件负载。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SyncStorageException("远端同步操作的事件负载无效。", exception);
        }

        if (calendarEvent.Id != document.EntityId)
        {
            throw new SyncStorageException("远端同步操作的事件 ID 与操作记录不一致。");
        }

        var isDelete = document.OperationType == SyncOperationType.Delete;
        if (isDelete != (calendarEvent.DeletedAtUtc is not null))
        {
            throw new SyncStorageException("远端同步操作类型与事件删除标记不一致。");
        }

        if (string.IsNullOrWhiteSpace(calendarEvent.Title) || calendarEvent.Title.Length > 200 ||
            calendarEvent.Description is null || calendarEvent.Description.Length > 4_000 ||
            calendarEvent.Location is null || calendarEvent.Location.Length > 300 ||
            string.IsNullOrWhiteSpace(calendarEvent.TimeZoneId) ||
            calendarEvent.EndUtc <= calendarEvent.StartUtc ||
            calendarEvent.UpdatedAtUtc < calendarEvent.CreatedAtUtc ||
            calendarEvent.DeletedAtUtc > calendarEvent.UpdatedAtUtc ||
            calendarEvent.ExternalUid is { Length: > 1_024 } ||
            calendarEvent.ExternalUid is not null && string.IsNullOrWhiteSpace(calendarEvent.ExternalUid))
        {
            throw new SyncStorageException("远端同步操作包含无效的事件字段或时间范围。");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(calendarEvent.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new SyncStorageException("远端同步操作包含当前设备无法识别的事件时区。", exception);
        }

        if (calendarEvent.RecurrenceRule is not null)
        {
            try
            {
                _ = RecurrenceRuleParser.Parse(calendarEvent.RecurrenceRule);
            }
            catch (RecurrenceRuleException exception)
            {
                throw new SyncStorageException("远端同步操作包含无效或不受支持的重复规则。", exception);
            }
        }

        return calendarEvent with
        {
            StartUtc = calendarEvent.StartUtc.ToUniversalTime(),
            EndUtc = calendarEvent.EndUtc.ToUniversalTime(),
            CreatedAtUtc = calendarEvent.CreatedAtUtc.ToUniversalTime(),
            UpdatedAtUtc = calendarEvent.UpdatedAtUtc.ToUniversalTime(),
            DeletedAtUtc = calendarEvent.DeletedAtUtc?.ToUniversalTime()
        };
    }

    private static void ValidateDocument(RemoteSyncOperationDocument document, Guid fileOperationId)
    {
        if (document.FormatVersion < RemoteSyncFormat.MinimumSupportedVersion ||
            document.FormatVersion > RemoteSyncFormat.CurrentVersion)
        {
            throw new SyncStorageException($"不支持远端同步格式版本 {document.FormatVersion}。");
        }

        if (document.OperationId == Guid.Empty || document.OperationId != fileOperationId)
        {
            throw new SyncStorageException("远端同步操作 ID 与文件名不一致。");
        }

        if (document.EntityId == Guid.Empty || string.IsNullOrWhiteSpace(document.DeviceId))
        {
            throw new SyncStorageException("远端同步操作缺少必要标识。");
        }

        if (!Enum.IsDefined(document.OperationType))
        {
            throw new SyncStorageException("远端同步操作类型无效。");
        }
    }

    private static SyncOperation ToLocalOperation(
        RemoteSyncOperationDocument document,
        SyncOperationStatus status) => new()
    {
        OperationId = document.OperationId,
        DeviceId = document.DeviceId,
        EntityId = document.EntityId,
        OperationType = document.OperationType,
        TimestampUtc = document.TimestampUtc.ToUniversalTime(),
        Payload = document.Payload.GetRawText(),
        Status = status
    };

    private static bool RemoteContentsMatch(string existing, string expected)
    {
        var existingDocument = Deserialize(existing);
        var expectedDocument = Deserialize(expected);
        return JsonSerializer.Serialize(existingDocument, JsonOptions) ==
               JsonSerializer.Serialize(expectedDocument, JsonOptions);
    }

    private static bool ShouldApplyRemoteEvent(CalendarEvent remoteEvent, CalendarEvent localEvent)
    {
        var timestampComparison = remoteEvent.UpdatedAtUtc.CompareTo(localEvent.UpdatedAtUtc);
        if (timestampComparison != 0)
        {
            return timestampComparison > 0;
        }

        if ((remoteEvent.DeletedAtUtc is not null) != (localEvent.DeletedAtUtc is not null))
        {
            return remoteEvent.DeletedAtUtc is not null;
        }

        var remoteContent = JsonSerializer.Serialize(remoteEvent, JsonOptions);
        var localContent = JsonSerializer.Serialize(localEvent, JsonOptions);
        return string.CompareOrdinal(remoteContent, localContent) > 0;
    }

    private static bool TryGetOperationId(string path, out Guid operationId)
    {
        operationId = Guid.Empty;
        var normalized = path.Replace('\\', '/').Trim('/');
        var prefix = $"{RemoteSyncFormat.OperationsDirectory}/";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = normalized[prefix.Length..];
        return !fileName.Contains('/') &&
               fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(fileName[..^5], "D", out operationId);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
