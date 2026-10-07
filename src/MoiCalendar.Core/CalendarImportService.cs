using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace MoiCalendar.Core;

public enum CalendarImportDuplicateAction
{
    Skip,
    Update
}

public sealed record CalendarImportPreviewItem(
    int ItemNumber,
    ICalendarImportCandidate Candidate,
    Guid? ExistingEventId)
{
    public bool IsPotentialDuplicate => ExistingEventId.HasValue;
    public bool IsDeletedDuplicate { get; init; }
}

public sealed record CalendarImportPreview(
    Guid ImportId,
    string? SourceName,
    string? CalendarName,
    int TotalEventCount,
    IReadOnlyList<CalendarImportPreviewItem> Items,
    IReadOnlyList<ICalendarImportMessage> Messages)
{
    public int ValidEventCount => Items.Count;

    public int PotentialDuplicateCount => Items.Count(item => item.IsPotentialDuplicate && !item.IsDeletedDuplicate);

    public int WarningCount => Messages.Count(message => message.Severity == ICalendarImportMessageSeverity.Warning);

    public int ErrorCount => Messages.Count(message => message.Severity == ICalendarImportMessageSeverity.Error);
}

public sealed record CalendarImportResult(int CreatedCount, int UpdatedCount, int SkippedCount);

public sealed record CalendarImportChange(
    CalendarEvent CalendarEvent,
    SyncOperation Operation,
    Guid? ExpectedExistingEventId,
    DateTimeOffset? ExpectedExistingUpdatedAtUtc);

public interface ICalendarImportService
{
    Task<CalendarImportPreview> PrepareAsync(
        string content,
        string? sourceName = null,
        CancellationToken cancellationToken = default);

    Task<CalendarImportResult> ConfirmAsync(
        Guid importId,
        IReadOnlyDictionary<int, CalendarImportDuplicateAction> duplicateActions,
        CancellationToken cancellationToken = default);

    void Cancel(Guid importId);
}

public sealed class CalendarImportService(
    ICalendarImportParser parser,
    IEventRepository eventRepository,
    ILocalEventChangeRepository localEventChanges,
    IDeviceService deviceService,
    TimeProvider timeProvider,
    ILocalDataOperationLock? operationLock = null,
    IRecurrenceExpansionService? recurrenceExpansionService = null) : ICalendarImportService
{
    private readonly IRecurrenceExpansionService expansion = recurrenceExpansionService ?? new RecurrenceExpansionService();
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly object stateGate = new();
    private readonly SemaphoreSlim confirmationGate = new(1, 1);
    private readonly ILocalDataOperationLock operationLock =
        operationLock ?? NoOpLocalDataOperationLock.Instance;
    private PreparedImport? preparedImport;

    public async Task<CalendarImportPreview> PrepareAsync(
        string content,
        string? sourceName = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = parser.Parse(content, sourceName);
        var existingEvents = await eventRepository.GetAllIncludingDeletedAsync(cancellationToken);
        var duplicateLookup = existingEvents
            .Where(calendarEvent => !string.IsNullOrWhiteSpace(calendarEvent.ExternalUid))
            .GroupBy(calendarEvent => calendarEvent.ExternalUid!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAtUtc).ThenBy(item => item.Id.ToString("D"), StringComparer.Ordinal).First(), StringComparer.Ordinal);
        var byId = existingEvents.ToDictionary(item => item.Id);
        CalendarEvent? FindDuplicate(ICalendarImportCandidate candidate) =>
            duplicateLookup.GetValueOrDefault(candidate.ExternalUid) ??
            (TryReadLocalUid(candidate.ExternalUid, out var id) ? byId.GetValueOrDefault(id) : null);
        var items = parsed.CandidateEvents
            .Select((candidate, index) => new CalendarImportPreviewItem(
                index + 1,
                candidate,
                FindDuplicate(candidate)?.Id)
                { IsDeletedDuplicate = FindDuplicate(candidate)?.DeletedAtUtc is not null })
            .ToArray();
        var preview = new CalendarImportPreview(
            Guid.NewGuid(),
            parsed.SourceName,
            parsed.CalendarName,
            parsed.TotalEventCount,
            items,
            parsed.Messages);

        lock (stateGate)
        {
            preparedImport = new PreparedImport(preview, existingEvents.ToDictionary(item => item.Id));
        }

        return preview;
    }

    public async Task<CalendarImportResult> ConfirmAsync(
        Guid importId,
        IReadOnlyDictionary<int, CalendarImportDuplicateAction> duplicateActions,
        CancellationToken cancellationToken = default)
    {
        await confirmationGate.WaitAsync(cancellationToken);
        try
        {
            await using var operationLease = await operationLock.AcquireAsync(cancellationToken);
            PreparedImport prepared;
            lock (stateGate)
            {
                prepared = preparedImport is { } value && value.Preview.ImportId == importId
                    ? value
                    : throw new CalendarImportException("导入预览已失效，请重新选择 iCalendar 文件。");
            }

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var deviceId = await deviceService.GetDeviceIdAsync(cancellationToken);
            var changes = new List<CalendarImportChange>();
            var createdCount = 0;
            var updatedCount = 0;
            var skippedCount = 0;
            foreach (var item in prepared.Preview.Items)
            {
                CalendarEvent calendarEvent;
                SyncOperationType operationType;
                CalendarEvent? existing = null;
                if (item.ExistingEventId is { } existingId)
                {
                    var action = duplicateActions.GetValueOrDefault(item.ItemNumber, CalendarImportDuplicateAction.Skip);
                    if (!Enum.IsDefined(action))
                    {
                        throw new CalendarImportException("重复事件处理方式无效，请重新选择后导入。");
                    }

                    if (action == CalendarImportDuplicateAction.Skip || item.IsDeletedDuplicate)
                    {
                        skippedCount++;
                        continue;
                    }

                    existing = prepared.ExistingEvents[existingId];
                    calendarEvent = ToCalendarEvent(item.Candidate, existing.Id, existing.CreatedAtUtc, now, existing) with
                    {
                        DeletedAtUtc = null
                    };
                    operationType = SyncOperationType.Update;
                    updatedCount++;
                }
                else
                {
                    calendarEvent = ToCalendarEvent(
                        item.Candidate,
                        CreateDeterministicImportedEventId(item.Candidate.ExternalUid),
                        now,
                        now);
                    operationType = SyncOperationType.Create;
                    createdCount++;
                }

                var operation = new SyncOperation
                {
                    OperationId = Guid.NewGuid(),
                    DeviceId = deviceId,
                    EntityId = calendarEvent.Id,
                    OperationType = operationType,
                    TimestampUtc = now,
                    Payload = JsonSerializer.Serialize(calendarEvent, PayloadSerializerOptions),
                    Status = SyncOperationStatus.Pending
                };
                changes.Add(new CalendarImportChange(
                    calendarEvent,
                    operation,
                    existing?.Id,
                    existing?.UpdatedAtUtc));
            }

            try
            {
                await localEventChanges.ApplyImportAsync(changes, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SyncOperationException or EventRepositoryException or InvalidOperationException)
            {
                throw new CalendarImportException("导入未完成；本地事务已中止，原有数据应保持不变。", exception);
            }

            lock (stateGate)
            {
                if (preparedImport?.Preview.ImportId == importId)
                {
                    preparedImport = null;
                }
            }

            return new CalendarImportResult(createdCount, updatedCount, skippedCount);
        }
        finally
        {
            confirmationGate.Release();
        }
    }

    public void Cancel(Guid importId)
    {
        lock (stateGate)
        {
            if (preparedImport?.Preview.ImportId == importId)
            {
                preparedImport = null;
            }
        }
    }

    private CalendarEvent ToCalendarEvent(
        ICalendarImportCandidate candidate,
        Guid id,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        CalendarEvent? existing = null)
    {
        var updated = (existing ?? new CalendarEvent
        {
            Id = id, Title = string.Empty, Description = string.Empty, Location = string.Empty,
            StartUtc = candidate.StartUtc, EndUtc = candidate.EndUtc, TimeZoneId = candidate.TimeZoneId,
            IsAllDay = candidate.IsAllDay, CreatedAtUtc = createdAtUtc, UpdatedAtUtc = updatedAtUtc
        }) with
        {
            Id = id,
            Title = candidate.Title,
            Description = candidate.Description,
            Location = candidate.Location,
            StartUtc = candidate.StartUtc,
            EndUtc = candidate.EndUtc,
            TimeZoneId = candidate.TimeZoneId,
            IsAllDay = candidate.IsAllDay,
            RecurrenceRule = candidate.RecurrenceRule,
            ColorIndex = candidate.ColorIndex ?? existing?.ColorIndex ?? 1,
            ExcludedOccurrenceStartsUtc = candidate.ExcludedOccurrenceStartsUtc ?? existing?.ExcludedOccurrenceStartsUtc ?? [],
            ExternalUid = candidate.ExternalUid,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc
        };
        if (existing is not null)
        {
            try
            {
                updated = updated with { ExcludedOccurrenceStartsUtc = [.. RecurrenceExclusionPolicy.Remap(existing, updated, expansion)
                    .Union(candidate.ExcludedOccurrenceStartsUtc ?? []).Order()] };
            }
            catch (Exception exception) when (exception is RecurrenceRuleException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new CalendarImportException("无法安全保留此事件的重复例外，请先修复原事件的规则或时区。", exception);
            }
        }
        if (updated.ExcludedOccurrenceStartsUtc.Length > 100_000)
            throw new CalendarImportException("此系列超过 100000 条单次排除记录上限，未修改本地数据。");
        return updated;
    }

    private static bool TryReadLocalUid(string uid, out Guid id)
    {
        const string suffix = "@moicalendar.local";
        id = default;
        return uid.EndsWith(suffix, StringComparison.Ordinal) && Guid.TryParseExact(uid[..^suffix.Length], "D", out id);
    }

    private static Guid CreateDeterministicImportedEventId(string externalUid)
    {
        if (TryReadLocalUid(externalUid, out var localId)) return localId;
        var name = Encoding.UTF8.GetBytes("MoiCalendar/iCalendar/" + externalUid);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(name, hash);
        return new Guid(hash[..16]);
    }

    private sealed record PreparedImport(
        CalendarImportPreview Preview,
        IReadOnlyDictionary<Guid, CalendarEvent> ExistingEvents);
}

public sealed class CalendarImportException : Exception
{
    public CalendarImportException(string message)
        : base(message)
    {
    }

    public CalendarImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
