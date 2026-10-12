using MoiCalendar.Sync.Cloud;

namespace MoiCalendar.App;

// Only known, application-authored messages may cross the UI boundary.
public static class CloudSyncFailureHelp
{
    public static string? Message(CloudSyncFailureReason reason) => reason switch
    {
        CloudSyncFailureReason.RestoreProtection =>
            "备份恢复后的同步保护已启用。请先检查恢复结果，再在“本地备份与恢复”中允许手动同步；本地修改已保留。",
        CloudSyncFailureReason.AccountMismatch =>
            "本地日历已绑定到另一个云账户。请使用原账户登录，避免把不同账户的日历混合；不要清除浏览器数据。",
        CloudSyncFailureReason.LocalAccountBinding =>
            "无法读取或保存本地云账户绑定。请关闭其他日历标签页后重新加载再试；不要清除浏览器数据。",
        _ => null
    };

    public static string Link(CloudSyncFailureReason reason) => reason switch
    {
        CloudSyncFailureReason.RestoreProtection => "settings#sync-protection-heading",
        CloudSyncFailureReason.AccountMismatch => "settings#cloud-account-heading",
        _ => "sync-status#diagnostics-heading"
    };

    public static string LinkLabel(CloudSyncFailureReason reason) => reason switch
    {
        CloudSyncFailureReason.RestoreProtection => "检查恢复后的同步保护",
        CloudSyncFailureReason.AccountMismatch => "检查云账户",
        _ => "查看同步诊断"
    };
}

public sealed class ReminderSyncRequiredException(CloudSyncResult result) : InvalidOperationException(
    CloudSyncFailureHelp.Message(result.FailureReason) ?? result.Status switch
    {
        CloudSyncStatus.Conflict => "请先处理同步冲突，再开启后台提醒；本地提醒仍可使用。",
        CloudSyncStatus.AuthenticationRequired => "请重新登录云账户并同步，再开启后台提醒。",
        CloudSyncStatus.Offline => "无法连接云端。请联网后同步，再开启后台提醒；本地提醒仍可使用。",
        CloudSyncStatus.RetryScheduled => "同步正在等待重试。请稍后同步成功后再开启后台提醒；本地提醒仍可使用。",
        _ => "同步尚未完成。请查看同步诊断，再开启后台提醒；本地提醒仍可使用。"
    })
{
    public CloudSyncFailureReason FailureReason { get; } = result.FailureReason;
}
