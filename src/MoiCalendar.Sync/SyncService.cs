using System.Text.Json;
using System.Text.Json.Serialization;
using MoiCalendar.Core;

namespace MoiCalendar.Sync;

public sealed class SyncService(
    IOperationRepository operationRepository,
    IEventRepository eventRepository,
    ISyncStorageProvider storageProvider) : ISyncService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        await storageProvider.EnsureDirectoryAsync(
            RemoteSyncFormat.OperationsDirectory,
            cancellationToken);
        var pending = await operationRepository.GetByStatusAsync(
            SyncOperationStatus.Pending,
            cancellationToken);
        var pushedCount = 0;

        foreach (var operation in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = RemoteSyncFormat.GetOperationPath(operation.OperationId);
            var content = Serialize(operation);
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

        return new SyncResult(pushedCount, 0, 0);
    }

    public async Task<SyncResult> PullAsync(CancellationToken cancellationToken = default)
    {
        await storageProvider.EnsureDirectoryAsync(
            RemoteSyncFormat.OperationsDirectory,
            cancellationToken);
        var files = await storageProvider.ListFilesAsync(
            RemoteSyncFormat.OperationsDirectory,
            cancellationToken);
        var downloadedCount = 0;
        var appliedCount = 0;

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (!TryGetOperationId(file.Path, out var operationId))
            {
                continue;
            }

            var localOperation = await operationRepository.GetByIdAsync(operationId, cancellationToken);
            if (localOperation?.Status == SyncOperationStatus.Applied)
            {
                continue;
            }

            var remoteFile = await storageProvider.DownloadTextAsync(file.Path, cancellationToken);
            if (remoteFile is null)
            {
                continue;
            }

            var document = Deserialize(remoteFile.Content);
            ValidateDocument(document, operationId);
            var remoteEvent = DeserializeEvent(document);
            downloadedCount++;

            var localEvent = await eventRepository.GetByIdIncludingDeletedAsync(
                document.EntityId,
                cancellationToken);
            if (localEvent is null || remoteEvent.UpdatedAtUtc > localEvent.UpdatedAtUtc)
            {
                await eventRepository.UpsertAsync(remoteEvent, cancellationToken);
                appliedCount++;
            }

            var appliedOperation = ToLocalOperation(document, SyncOperationStatus.Applied);
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

        return new SyncResult(0, downloadedCount, appliedCount);
    }

    public async Task<SyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        var pushed = await PushAsync(cancellationToken);
        var pulled = await PullAsync(cancellationToken);
        return new SyncResult(
            pushed.PushedCount,
            pulled.DownloadedCount,
            pulled.AppliedCount);
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

        return calendarEvent;
    }

    private static void ValidateDocument(RemoteSyncOperationDocument document, Guid fileOperationId)
    {
        if (document.FormatVersion != RemoteSyncFormat.CurrentVersion)
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
