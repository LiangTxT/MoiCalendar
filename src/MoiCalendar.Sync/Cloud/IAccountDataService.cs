using System.Text.Json;
using MoiCalendar.Core;

namespace MoiCalendar.Sync.Cloud;

public sealed record CloudAccountPreferences(
    string? DisplayName,
    string? TimeZoneId);

public sealed record CloudCalendarExport(
    Guid Id,
    string Name,
    string? Color,
    bool IsDefault,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

public sealed record CloudCalendarEventExport(
    Guid CalendarId,
    CalendarEvent CalendarEvent);

public sealed record CloudAccountExportDocument
{
    public const string CurrentFormat = "moicalendar-cloud-export";
    public const int CurrentSchemaVersion = 1;

    public required string Format { get; init; }

    public required int SchemaVersion { get; init; }

    public required DateTimeOffset ExportedAtUtc { get; init; }

    public required CloudAccountPreferences Preferences { get; init; }

    public required IReadOnlyList<CloudCalendarExport> Calendars { get; init; }

    public required IReadOnlyList<CloudCalendarEventExport> CalendarEvents { get; init; }
}

public sealed record AccountDataExport(
    string FileName,
    string Json,
    int CalendarCount,
    int EventCount);

public sealed record AccountDeletionResult(bool LocalDataRemoved);

public enum AccountDataFailureKind
{
    AuthenticationRequired,
    RecentAuthenticationRequired,
    ConfirmationRequired,
    Transient,
    Permanent
}

public sealed class AccountDataException : Exception
{
    public AccountDataException(string message, AccountDataFailureKind failureKind)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public AccountDataException(
        string message,
        Exception innerException,
        AccountDataFailureKind failureKind)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public AccountDataFailureKind FailureKind { get; }
}

public interface IAccountDataService
{
    const string RequiredDeletionConfirmation = "永久删除我的云账户";

    bool IsAvailable { get; }

    Task<AccountDataExport> ExportAsync(CancellationToken cancellationToken = default);

    Task<AccountDeletionResult> DeleteAccountAsync(
        string confirmation,
        bool removeLocalData,
        CancellationToken cancellationToken = default);
}

public interface ICloudAccountDataTransport
{
    bool IsAvailable { get; }

    Task<CloudAccountExportDocument> ExportAsync(
        CancellationToken cancellationToken = default);

    Task DeleteCurrentAccountAsync(CancellationToken cancellationToken = default);
}

public sealed class AccountDataService(
    IAccountService accountService,
    ICloudAccountDataTransport transport,
    IAccountDeletionLocalRepository localRepository,
    ILocalDataOperationLock operationLock) : IAccountDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public bool IsAvailable => accountService.IsAvailable && transport.IsAvailable;

    public async Task<AccountDataExport> ExportAsync(
        CancellationToken cancellationToken = default)
    {
        _ = await RequireAccountAsync(cancellationToken);
        CloudAccountExportDocument document;
        try
        {
            document = await transport.ExportAsync(cancellationToken);
            Validate(document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AccountDataException(
                "云账户数据导出失败，请稍后重试。",
                exception,
                AccountDataFailureKind.Permanent);
        }

        var json = JsonSerializer.Serialize(document, JsonOptions);
        return new AccountDataExport(
            $"mycalendar-cloud-export-{document.ExportedAtUtc:yyyy-MM-dd}.json",
            json,
            document.Calendars.Count,
            document.CalendarEvents.Count);
    }

    public async Task<AccountDeletionResult> DeleteAccountAsync(
        string confirmation,
        bool removeLocalData,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                confirmation?.Trim(),
                IAccountDataService.RequiredDeletionConfirmation,
                StringComparison.Ordinal))
        {
            throw new AccountDataException(
                $"请输入“{IAccountDataService.RequiredDeletionConfirmation}”确认永久删除。",
                AccountDataFailureKind.ConfirmationRequired);
        }

        var account = await RequireAccountAsync(cancellationToken);
        await using var lease = await operationLock.AcquireAsync(cancellationToken);
        try
        {
            await transport.DeleteCurrentAccountAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AccountDataException(
                "云账户删除请求未完成，本地数据没有改变。",
                exception,
                AccountDataFailureKind.Transient);
        }

        try
        {
            await localRepository.ResetAfterAccountDeletionAsync(
                account.Id,
                removeLocalData,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AccountDataException(
                "云账户已删除，但此设备的本地同步状态清理未完成。请不要重新登录，并重新载入应用后再试。",
                exception,
                AccountDataFailureKind.Permanent);
        }
        finally
        {
            try
            {
                await accountService.LogoutAsync(CancellationToken.None);
            }
            catch
            {
                // The server account no longer exists. Local cleanup remains authoritative here;
                // the account implementation clears its browser session before its logout request.
            }
        }

        return new AccountDeletionResult(removeLocalData);
    }

    private async Task<CloudAccount> RequireAccountAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new AccountDataException(
                "云账户功能尚未启用。",
                AccountDataFailureKind.AuthenticationRequired);
        }

        CloudAccount? account;
        try
        {
            account = await accountService.GetCurrentAccountAsync(cancellationToken);
        }
        catch (AccountServiceException exception)
        {
            throw new AccountDataException(
                "无法验证当前云账户会话，请重新登录后重试。",
                exception,
                AccountDataFailureKind.AuthenticationRequired);
        }

        return account ?? throw new AccountDataException(
            "请先登录云账户。",
            AccountDataFailureKind.AuthenticationRequired);
    }

    private static void Validate(CloudAccountExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.Format, CloudAccountExportDocument.CurrentFormat, StringComparison.Ordinal) ||
            document.SchemaVersion != CloudAccountExportDocument.CurrentSchemaVersion ||
            document.ExportedAtUtc <= DateTimeOffset.UnixEpoch ||
            document.Preferences is null || document.Calendars is null || document.CalendarEvents is null)
        {
            throw new AccountDataException(
                "云端返回了不受支持的数据导出格式。",
                AccountDataFailureKind.Permanent);
        }

        var calendarIds = new HashSet<Guid>();
        foreach (var calendar in document.Calendars)
        {
            if (calendar.Id == Guid.Empty || !calendarIds.Add(calendar.Id) ||
                string.IsNullOrWhiteSpace(calendar.Name) || calendar.Name.Length > 200 ||
                calendar.UpdatedAtUtc < calendar.CreatedAtUtc ||
                calendar.DeletedAtUtc > calendar.UpdatedAtUtc)
            {
                throw new AccountDataException(
                    "云端日历数据不完整，未创建导出文件。",
                    AccountDataFailureKind.Permanent);
            }
        }

        var eventIds = new HashSet<Guid>();
        foreach (var item in document.CalendarEvents)
        {
            var calendarEvent = item.CalendarEvent;
            if (!calendarIds.Contains(item.CalendarId) || calendarEvent is null ||
                calendarEvent.Id == Guid.Empty || !eventIds.Add(calendarEvent.Id) ||
                string.IsNullOrWhiteSpace(calendarEvent.Title) ||
                calendarEvent.EndUtc <= calendarEvent.StartUtc ||
                calendarEvent.UpdatedAtUtc < calendarEvent.CreatedAtUtc ||
                calendarEvent.DeletedAtUtc > calendarEvent.UpdatedAtUtc)
            {
                throw new AccountDataException(
                    "云端日程数据不完整，未创建导出文件。",
                    AccountDataFailureKind.Permanent);
            }
        }
    }
}
